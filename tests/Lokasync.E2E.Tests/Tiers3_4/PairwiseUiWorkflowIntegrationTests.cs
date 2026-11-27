using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers3_4;

public sealed class PairwiseUiWorkflowIntegrationTests
{
    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F1,F2,F13,F20")]
    public async Task DashboardViewModelAndDatabase_SelectGameFolderAndSaveProfile_AutoDetectsAndPersistsProject()
    {
        // Arrange
        using TempWorkspace ws = new("t3_19_dashboard_db_profile");
        SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, mockRouter);

        string? detectedEngine = null;

        // Act
        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle dashboardVm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Dashboard", "Project", "Main"],
                deps);

            Tiers3_4TestHelper.SetProperty(dashboardVm, ws.GameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "FolderPath", "ProjectPath");
            await TriggerDetectAsync(dashboardVm, ws.GameRootPath);
            Tiers3_4TestHelper.TryGetProperty(dashboardVm, out detectedEngine, "DetectedEngine", "DetectedEngineName", "EngineName");

            ViewModelHandle profileVm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Profile", "Router", "Dashboard", "Main"],
                deps);
            Tiers3_4TestHelper.TrySetProperty(profileVm, ws.ProfilesJsonPath, "ProfilesJsonPath", "ProfilesPath", "ProfilesFilePath", "ConfigPath");
            Tiers3_4TestHelper.TrySetProperty(profileVm, "ollama_local", "ActiveProfile", "SelectedProfileName", "ActiveProfileName", "ProfileName");
            Tiers3_4TestHelper.SetProperty(profileVm, "http://127.0.0.1:11434/v1/chat/completions", "BaseUrl", "EndpointUrl", "RouterBaseUrl");
            Tiers3_4TestHelper.TrySetProperty(profileVm, "sk-dash-19", "ApiKey");
            Tiers3_4TestHelper.SetProperty(profileVm, "llama3.1", "Model", "ModelName");
            Tiers3_4TestHelper.TrySetProperty(profileVm, 1, "MaxConcurrency");
            Tiers3_4TestHelper.TrySetProperty(profileVm, 75, "DelayBetweenBatchesMs");
            Tiers3_4TestHelper.TrySetProperty(profileVm, 15, "JitterMs");
            Tiers3_4TestHelper.TrySetProperty(profileVm, 12, "BatchSize");

            ICommand saveCmd = Tiers3_4TestHelper.GetCommandSafe(profileVm, ["SaveProfile", "SaveConfig"]);
            await Tiers3_4TestHelper.ExecuteCommandAsync(saveCmd);
        });

        // Assert
        Assert.NotNull(detectedEngine);
        Assert.Contains("Rpg", detectedEngine, StringComparison.OrdinalIgnoreCase);

        await using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT detected_engine FROM projects WHERE game_root_path = @path LIMIT 1;";
        cmd.Parameters.AddWithValue("@path", ws.GameRootPath);
        string? dbEngine = Convert.ToString(await cmd.ExecuteScalarAsync());
        Assert.False(string.IsNullOrWhiteSpace(dbEngine));
        Assert.Contains("Rpg", dbEngine, StringComparison.OrdinalIgnoreCase);

        Assert.True(File.Exists(ws.ProfilesJsonPath));
        (string active, IReadOnlyDictionary<string, RouterProfile> loaded) = await bridge.LoadProfilesAsync(ws.ProfilesJsonPath);
        RouterProfile saved = loaded[active];
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", saved.BaseUrl);
        Assert.Equal("llama3.1", saved.Model);
        Assert.Equal(12, saved.BatchSize);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F2,F7,F8,F21")]
    public async Task GlossaryViewModelAndTranslator_EditGlossaryInUi_PersistsNarrativeJsonAndFeedsHttpPrompt()
    {
        // Arrange
        using TempWorkspace ws = new("t3_20_glossary_http_prompt");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-20";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "Expedition Zero", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, mockRouter);

        // Act — Edit Glossary via GlossaryViewModel directly
        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = bridge.CreateViewModel("Glossary", deps);
            Assert.DoesNotContain("Term", vm.ViewModelType.Name, StringComparison.OrdinalIgnoreCase);

            Tiers3_4TestHelper.TrySetProperty(vm, projectId, "ProjectId", "CurrentProjectId");
            await Tiers3_4TestHelper.TryInvokeViewModelMethodAsync(vm, ["LoadProject", "LoadNarrative", "LoadGlossary"], projectId);

            Tiers3_4TestHelper.SetProperty(vm, "Expedition Zero", "GameTitle");
            Tiers3_4TestHelper.SetProperty(vm, "Militer semi-formal", "GenreTone");
            await AddGlossaryTermAsync(vm, "The Warden", "Sang Sipir", "Antagonis");
            await SaveGlossaryAsync(vm);
        });

        string? narrativeJson = await Tiers3_4TestHelper.QueryProjectNarrativeContextJsonAsync(db.Connection, projectId);
        Assert.False(string.IsNullOrWhiteSpace(narrativeJson));
        using JsonDocument doc = JsonDocument.Parse(narrativeJson!);
        string title = doc.RootElement.GetProperty("game_title").GetString()!;
        string tone = doc.RootElement.GetProperty("genre_tone").GetString()!;
        List<(string Source, string Target, string Note)> glossary = doc.RootElement
            .GetProperty("glossary")
            .EnumerateArray()
            .Select(el => (
                el.GetProperty("source").GetString()!,
                el.GetProperty("target").GetString()!,
                el.TryGetProperty("note", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty))
            .ToList();

        string systemPrompt = bridge.BuildSystemPrompt(title, tone, glossary);
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        await translator.TranslateBatchAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["k1"] = "The Warden arrives." },
            systemPrompt,
            MockLlmRouterHandler.CreateProfile(),
            CancellationToken.None);

        // Assert
        CapturedRequest req = Assert.Single(mockRouter.Requests);
        Assert.Contains("Expedition Zero", req.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Militer semi-formal", req.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("The Warden", req.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Sang Sipir", req.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F11,F12,F20,F22")]
    public async Task PipelineControlsViewModel_Http401AutoPauseAndProfileHotSwap_ResumesAndCompletes()
    {
        // Arrange
        using TempWorkspace ws = new("t3_21_pipeline_401_hotswap");
        Dictionary<string, string> lines = Enumerable.Range(1, 4)
            .ToDictionary(i => $"line_{i}", i => $"Dialog line {i}");
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(lines));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-21";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "HotSwap 401", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mockRouter.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mockRouter.EnqueueHttpStatus(HttpStatusCode.Unauthorized);

        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, mockRouter);

        bool isAutoPausedOn401 = false;
        bool resumeEnabledOn401 = false;
        int requestsBeforeResume = 0;
        int translatedMid = 0;
        int pendingMid = 0;
        double progressAfter = 0;
        int logCount = 0;

        // Act
        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            RouterProfile initialProfile = MockLlmRouterHandler.CreateProfile(apiKey: "sk-expired-401", batchSize: 2);
            ConfigurePipelineVm(vm, projectId, ws.GameRootPath, initialProfile);

            await Tiers3_4TestHelper.ExecuteCommandAsync(GetStartCommand(vm));

            Tiers3_4TestHelper.TryGetProperty(vm, out isAutoPausedOn401, "IsAutoPaused", "IsPaused");
            resumeEnabledOn401 = GetResumeCommand(vm).CanExecute(null);
            requestsBeforeResume = mockRouter.Requests.Count;

            IReadOnlyList<SubtitleEntry> mid = await db.GetAllEntriesAsync();
            translatedMid = mid.Count(e => e.Status == EntryStatus.Translated);
            pendingMid = mid.Count(e => e.Status == EntryStatus.Pending);

            RouterProfile freshProfile = initialProfile with { ApiKey = "sk-fresh-401" };
            Tiers3_4TestHelper.TrySetProperty(vm, freshProfile, "ActiveRouterProfile", "RouterProfile", "Profile");
            Tiers3_4TestHelper.TrySetProperty(vm, "sk-fresh-401", "ApiKey");

            await Tiers3_4TestHelper.ExecuteCommandAsync(GetResumeCommand(vm));

            Tiers3_4TestHelper.TryGetProperty(vm, out progressAfter, "ProgressPercentage", "ProgressPercent", "Progress");
            logCount = GetLogItems(vm).Count;
        });

        // Assert
        Assert.True(isAutoPausedOn401);
        Assert.True(resumeEnabledOn401);
        Assert.Equal(2, requestsBeforeResume);
        Assert.Equal(2, translatedMid);
        Assert.Equal(2, pendingMid);
        Assert.True(logCount > 0);
        Assert.Equal(100.0, progressAfter, precision: 1);
        Assert.Equal("Bearer sk-fresh-401", mockRouter.Requests.Last().Authorization);

        IReadOnlyList<SubtitleEntry> finalEntries = await db.GetAllEntriesAsync();
        Assert.Equal(4, finalEntries.Count);
        Assert.All(finalEntries, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F4,F14,F18,F22")]
    public async Task PipelineControlsViewModel_PatchAndRestoreCommands_EnforcesBackupAndRestoresOriginals()
    {
        // Arrange
        using TempWorkspace ws = new("t3_22_pipeline_patch_restore");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-22";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "Patch Restore UI", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[UI] {kv.Value}")
        };
        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, mockRouter);

        string patchedJson = string.Empty;
        int logCountAfterRestore = 0;

        // Act
        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigurePipelineVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile(batchSize: 10));
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetStartCommand(vm));
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetPatchGameCommand(vm));

            E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
            patchedJson = ws.ReadTextFile(manifest.JsonRelativePath);

            await Tiers3_4TestHelper.ExecuteCommandAsync(GetRestoreCommand(vm));
            logCountAfterRestore = GetLogItems(vm).Count;
        });

        // Assert
        Assert.Contains("[UI] ", patchedJson, StringComparison.Ordinal);
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
        Assert.True(logCountAfterRestore > 0);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F3,F6,F19,F23")]
    public async Task InlineGridViewModel_FilterEditValidAndBrokenTokens_UpdatesStatusOverflowAndSqlite()
    {
        // Arrange
        using TempWorkspace ws = new("t3_23_inline_filter_edit");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-23";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "Inline QA", ws.GameRootPath, "Loose");

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        const string rawFailed = "<b>Welcome</b>, {player_name}!";
        (string maskedFailed, Dictionary<string, string> phFailed) = sanitizer.Mask(rawFailed);

        await db.InsertSubtitleEntriesAsync([
            Tiers3_4TestHelper.CreateEntry(0, "k_fail", "dialog.json", rawFailed, maskedText: maskedFailed, placeholders: phFailed, translatedText: "Broken", finalPatchedText: null, status: EntryStatus.FailedValidation, hasOverflowWarning: false),
            Tiers3_4TestHelper.CreateEntry(0, "k_over", "dialog.json", "Start Game", translatedText: "Mulai Permainan Panjang Sekali", finalPatchedText: "Mulai Permainan Panjang Sekali", status: EntryStatus.Translated, hasOverflowWarning: true)
        ]);

        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        // Act & Assert
        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);

            // 1. Filter FailedValidation -> apply broken edit (dropping [VAR_0]) -> FailedValidation
            EnableFailedValidationFilter(vm, true);
            object failRow = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, failRow, "[TAG_0]Halo[TAG_1] tanpa var!");

            IReadOnlyList<SubtitleEntry> afterBroken = await db.GetAllEntriesAsync();
            Assert.Equal(EntryStatus.FailedValidation, afterBroken.Single(e => e.EntryKey == "k_fail").Status);

            // 2. Fix with valid tokens -> ManuallyEdited & HasOverflowWarning == false
            failRow = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, failRow, "[TAG_0]Halo[TAG_1], [VAR_0]!");

            IReadOnlyList<SubtitleEntry> afterValid = await db.GetAllEntriesAsync();
            SubtitleEntry fixedRow = afterValid.Single(e => e.EntryKey == "k_fail");
            Assert.Equal(EntryStatus.ManuallyEdited, fixedRow.Status);
            Assert.Equal("<b>Halo</b>, {player_name}!", fixedRow.FinalPatchedText);
            Assert.False(fixedRow.HasOverflowWarning);

            // 3. Switch to OverflowWarning filter -> shorten to <= 12 chars -> clears HasOverflowWarning
            EnableFailedValidationFilter(vm, false);
            EnableOverflowFilter(vm, true);
            object overRow = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, overRow, "Mulai Main");
        });

        IReadOnlyList<SubtitleEntry> finalRows = await db.GetAllEntriesAsync();
        SubtitleEntry finalOver = finalRows.Single(e => e.EntryKey == "k_over");
        Assert.Equal(EntryStatus.ManuallyEdited, finalOver.Status);
        Assert.False(finalOver.HasOverflowWarning);
        E2EAssertions.AssertOverflowWarningRule(finalOver);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F8,F14,F18,F23")]
    public async Task InlineGridViewModel_RePatchLooseGame_WritesManualEditToDiskWithZeroLlmRequests()
    {
        // Arrange
        using TempWorkspace ws = new("t3_24_inline_repatch_loose");
        ws.WriteTextFile("dialog.json", """{"greeting":"Hello hero","bye":"Goodbye"}""");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-24";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "RePatch Loose", ws.GameRootPath, "Loose");

        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, ["dialog.json"], CancellationToken.None);

        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        List<SubtitleEntry> extracted = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = extracted
            .Select(e => e with { TranslatedText = $"[ID] {e.RawText}", FinalPatchedText = $"[ID] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();
        await db.InsertSubtitleEntriesAsync(translated);
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        MockLlmRouterHandler mockRouter = new();
        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, mockRouter, backupService);
        int llmCallsBefore = mockRouter.Requests.Count;

        // Act
        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            Tiers3_4TestHelper.SetProperty(vm, "hero", "SearchText", "SearchQuery", "FilterText");
            object row = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, row, "Salam ksatria!");
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetRePatchCommand(vm));
        });

        // Assert
        Assert.Equal(llmCallsBefore, mockRouter.Requests.Count);
        Assert.Empty(mockRouter.Requests);
        using JsonDocument doc = JsonDocument.Parse(ws.ReadTextFile("dialog.json"));
        Assert.Equal("Salam ksatria!", doc.RootElement.GetProperty("greeting").GetString());
        Assert.Equal("[ID] Goodbye", doc.RootElement.GetProperty("bye").GetString());
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F3,F15,F19,F23")]
    public async Task InlineGridViewModel_RePatchRpgMakerAndPreserveUntouchedCodes_UpdatesMapJsonWithZeroLlmCalls()
    {
        // Arrange
        using TempWorkspace ws = new("t3_25_inline_repatch_rpgmz");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-25";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "RePatch MZ", ws.GameRootPath, "RpgMaker");

        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [manifest.Map001RelativePath, manifest.CommonEventsRelativePath], CancellationToken.None);

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");
        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(
            sanitizer,
            await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None));

        List<SubtitleEntry> seeded = masked.Select(e =>
        {
            if (e.RawText.Contains("Warden", StringComparison.Ordinal))
            {
                return e with { TranslatedText = "Teks lama", FinalPatchedText = "Teks lama", Status = EntryStatus.Translated };
            }

            if (e.RawText.Contains("{crystal_count}", StringComparison.Ordinal))
            {
                return e with { TranslatedText = "Rusak tanpa var", FinalPatchedText = null, Status = EntryStatus.FailedValidation };
            }

            return e with { TranslatedText = e.MaskedText, FinalPatchedText = e.RawText, Status = EntryStatus.Translated };
        }).ToList();

        await db.InsertSubtitleEntriesAsync(seeded);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, mockRouter, backupService);

        // Act
        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            Tiers3_4TestHelper.SetProperty(vm, "Warden", "SearchText", "SearchQuery", "FilterText");
            object row = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, row, "Dahulu kala, Sang Sipir menyegel langit.");
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetRePatchCommand(vm));
        });

        // Assert
        Assert.Empty(mockRouter.Requests);
        using JsonDocument doc = JsonDocument.Parse(ws.ReadTextFile(manifest.Map001RelativePath));
        JsonElement eventsArr = doc.RootElement.GetProperty("events");
        Assert.Equal(JsonValueKind.Null, eventsArr[0].ValueKind);
        JsonElement list = eventsArr[1].GetProperty("pages")[0].GetProperty("list");

        Assert.Equal("Actor1", list[0].GetProperty("parameters")[0].GetString());
        Assert.Equal("Bring {crystal_count} shards.", list[2].GetProperty("parameters")[0].GetString());
        Assert.Equal("Dahulu kala, Sang Sipir menyegel langit.", list[4].GetProperty("parameters")[0].GetString());
        Assert.Equal("Accept mission", list[6].GetProperty("parameters")[1].GetString());
        Assert.Equal(0, list[8].GetProperty("code").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F1,F2,F3,F4,F5,F6,F7,F8,F9,F10,F11,F12,F13,F14,F18,F19,F20,F21,F22,F23")]
    public async Task AllViewModels_DashboardToGlossaryToPipelineToInlineRePatch_CoordinatesSharedProjectState()
    {
        // Arrange
        using TempWorkspace ws = new("t3_26_all_vms_e2e");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Replace("Start Expedition", "Mulai Ekspedisi", StringComparison.Ordinal))
        };
        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, mockRouter);

        string projectId = string.Empty;
        int llmCallsBeforeRePatch = 0;
        string patchedJsonAfterRePatch = string.Empty;

        // Act
        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            // 1. Dashboard: detect Loose game + save profile
            ViewModelHandle dashboardVm = Tiers3_4TestHelper.ResolveViewModel(bridge, ["Dashboard", "Project", "Main"], deps);
            Tiers3_4TestHelper.SetProperty(dashboardVm, ws.GameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "FolderPath", "ProjectPath");
            await TriggerDetectAsync(dashboardVm, ws.GameRootPath);

            Tiers3_4TestHelper.TryGetProperty(dashboardVm, out string? resolvedId, "ProjectId", "CurrentProjectId");
            if (string.IsNullOrWhiteSpace(resolvedId))
            {
                await using var idCmd = db.Connection.CreateCommand();
                idCmd.CommandText = "SELECT id FROM projects WHERE game_root_path = @path LIMIT 1;";
                idCmd.Parameters.AddWithValue("@path", ws.GameRootPath);
                resolvedId = Convert.ToString(await idCmd.ExecuteScalarAsync());
            }

            Assert.False(string.IsNullOrWhiteSpace(resolvedId));
            projectId = resolvedId!;

            ViewModelHandle profileVm = Tiers3_4TestHelper.ResolveViewModel(bridge, ["Profile", "Router", "Dashboard", "Main"], deps);
            Tiers3_4TestHelper.TrySetProperty(profileVm, ws.ProfilesJsonPath, "ProfilesJsonPath", "ProfilesPath", "ProfilesFilePath", "ConfigPath");
            Tiers3_4TestHelper.SetProperty(profileVm, "http://127.0.0.1:8045/v1/chat/completions", "BaseUrl", "EndpointUrl", "RouterBaseUrl");
            Tiers3_4TestHelper.SetProperty(profileVm, "gemini-pro", "Model", "ModelName");
            Tiers3_4TestHelper.TrySetProperty(profileVm, 10, "BatchSize");
            await Tiers3_4TestHelper.ExecuteCommandAsync(Tiers3_4TestHelper.GetCommandSafe(profileVm, ["SaveProfile", "SaveConfig"]));

            // 2. Glossary: add term & save
            ViewModelHandle glossaryVm = bridge.CreateViewModel("Glossary", deps);
            Tiers3_4TestHelper.TrySetProperty(glossaryVm, projectId, "ProjectId", "CurrentProjectId");
            await Tiers3_4TestHelper.TryInvokeViewModelMethodAsync(glossaryVm, ["LoadProject", "LoadNarrative", "LoadGlossary"], projectId);
            Tiers3_4TestHelper.SetProperty(glossaryVm, "Arcadia Quest", "GameTitle");
            Tiers3_4TestHelper.SetProperty(glossaryVm, "Heroic Fantasy", "GenreTone");
            await AddGlossaryTermAsync(glossaryVm, "The Warden", "Sang Sipir", "Boss");
            await SaveGlossaryAsync(glossaryVm);

            // 3. Pipeline: Start + Patch
            ViewModelHandle pipelineVm = Tiers3_4TestHelper.ResolveViewModel(bridge, ["Pipeline", "Progress", "Main"], deps);
            ConfigurePipelineVm(pipelineVm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile(batchSize: 10));
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetStartCommand(pipelineVm));
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetPatchGameCommand(pipelineVm));

            // 4. Inline Grid: edit row & Re-Patch with 0 LLM calls
            llmCallsBeforeRePatch = mockRouter.Requests.Count;
            ViewModelHandle gridVm = Tiers3_4TestHelper.ResolveViewModel(bridge, ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"], deps);
            await LoadGridAsync(gridVm, projectId, ws.GameRootPath);
            Tiers3_4TestHelper.SetProperty(gridVm, "Start Expedition", "SearchText", "SearchQuery", "FilterText");
            object targetRow = Assert.Single(GetFilteredRows(gridVm));
            await ApplyManualEditAsync(gridVm, targetRow, "Berangkat Misi");
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetRePatchCommand(gridVm));

            patchedJsonAfterRePatch = ws.ReadTextFile(manifest.JsonRelativePath);

            // 5. Restore original files via Pipeline RestoreCommand
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetRestoreCommand(pipelineVm));
        });

        // Assert
        Assert.NotEmpty(mockRouter.Requests);
        Assert.Contains(mockRouter.Requests, r => r.SystemPrompt?.Contains("The Warden", StringComparison.Ordinal) == true &&
                                                  r.SystemPrompt.Contains("Sang Sipir", StringComparison.Ordinal));
        Assert.Equal(llmCallsBeforeRePatch, mockRouter.Requests.Count);
        Assert.Contains("Berangkat Misi", patchedJsonAfterRePatch, StringComparison.Ordinal);
        Assert.Contains(await db.GetAllEntriesAsync(), e => e.Status == EntryStatus.ManuallyEdited);
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    private static async Task TriggerDetectAsync(ViewModelHandle vm, string gameRootPath)
    {
        if (Tiers3_4TestHelper.TryGetCommand(
                vm,
                ["LoadProject", "OpenProject", "SelectFolder", "DetectEngine", "Detect"],
                out ICommand? detectCmd) &&
            detectCmd is not null)
        {
            await Tiers3_4TestHelper.ExecuteCommandAsync(detectCmd, gameRootPath);
            return;
        }

        await Tiers3_4TestHelper.TryInvokeViewModelMethodAsync(
            vm,
            ["LoadProject", "OpenProject", "DetectEngine", "SelectGameFolder"],
            gameRootPath);
    }

    private static async Task AddGlossaryTermAsync(ViewModelHandle vm, string source, string target, string note)
    {
        Tiers3_4TestHelper.TrySetProperty(vm, source, "NewTermSource", "SourceTerm", "NewSource", "TermSource");
        Tiers3_4TestHelper.TrySetProperty(vm, target, "NewTermTarget", "TargetTerm", "NewTarget", "TermTarget");
        Tiers3_4TestHelper.TrySetProperty(vm, note, "NewTermNote", "TermNote", "NewNote", "Note");

        if (Tiers3_4TestHelper.TryGetCommand(vm, ["AddGlossary", "AddTerm", "AddEntry", "Add"], out ICommand? addCmd) &&
            addCmd is not null)
        {
            await Tiers3_4TestHelper.ExecuteCommandAsync(addCmd);
            return;
        }

        await Tiers3_4TestHelper.InvokeViewModelMethodAsync(vm, ["AddGlossary", "AddTerm"], source, target, note);
    }

    private static async Task SaveGlossaryAsync(ViewModelHandle vm)
    {
        if (Tiers3_4TestHelper.TryGetCommand(vm, ["SaveGlossary", "SaveNarrativeContext", "SaveContext", "Save"], out ICommand? saveCmd) &&
            saveCmd is not null)
        {
            await Tiers3_4TestHelper.ExecuteCommandAsync(saveCmd);
            return;
        }

        await Tiers3_4TestHelper.InvokeViewModelMethodAsync(vm, ["SaveGlossary", "SaveNarrative", "Save"]);
    }

    private static void ConfigurePipelineVm(
        ViewModelHandle vm,
        string projectId,
        string gameRootPath,
        RouterProfile profile)
    {
        Tiers3_4TestHelper.TrySetProperty(vm, projectId, "ProjectId", "CurrentProjectId");
        Tiers3_4TestHelper.TrySetProperty(vm, gameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "ProjectPath");
        Tiers3_4TestHelper.TrySetProperty(vm, profile, "ActiveRouterProfile", "RouterProfile", "Profile");
        Tiers3_4TestHelper.TrySetProperty(vm, profile.BaseUrl, "BaseUrl");
        Tiers3_4TestHelper.TrySetProperty(vm, profile.ApiKey, "ApiKey");
        Tiers3_4TestHelper.TrySetProperty(vm, profile.Model, "Model");
        Tiers3_4TestHelper.TrySetProperty(vm, profile.BatchSize, "BatchSize");
    }

    private static ICommand GetStartCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["StartTranslation", "StartPipeline", "Start"]);

    private static ICommand GetResumeCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["ResumeTranslation", "ResumePipeline", "Resume"]);

    private static ICommand GetPatchGameCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["PatchGame", "Patch"], excludedSubstrings: ["RePatch"]);

    private static ICommand GetRestoreCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["RestoreOriginal", "RestoreBackup", "Restore"]);

    private static ICommand GetRePatchCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["RePatch", "InstantRePatch"]);

    private static IReadOnlyList<object> GetLogItems(ViewModelHandle vm)
    {
        object? col = Tiers3_4TestHelper.GetProperty<object>(vm, "LogMessages", "ActivityLogs", "Logs", "LogEntries");
        return Tiers3_4TestHelper.ReadCollectionItems(col);
    }

    private static async Task LoadGridAsync(ViewModelHandle vm, string projectId, string gameRootPath)
    {
        Tiers3_4TestHelper.TrySetProperty(vm, projectId, "ProjectId", "CurrentProjectId");
        Tiers3_4TestHelper.TrySetProperty(vm, gameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "ProjectPath");

        (bool loadedWithId, _) = await Tiers3_4TestHelper.TryInvokeViewModelMethodAsync(
            vm, ["LoadEntries", "LoadGrid", "Refresh", "LoadProject"], projectId);
        if (loadedWithId)
        {
            return;
        }

        (bool loadedNoArgs, _) = await Tiers3_4TestHelper.TryInvokeViewModelMethodAsync(
            vm, ["LoadEntries", "LoadGrid", "Refresh"]);
        if (loadedNoArgs)
        {
            return;
        }

        ICommand refreshCmd = Tiers3_4TestHelper.GetCommandSafe(vm, ["Refresh", "LoadEntries", "Reload"]);
        await Tiers3_4TestHelper.ExecuteCommandAsync(refreshCmd);
    }

    private static IReadOnlyList<object> GetFilteredRows(ViewModelHandle vm)
    {
        object? col = Tiers3_4TestHelper.GetProperty<object>(
            vm,
            "FilteredEntries", "EntriesView", "GridEntries", "Entries", "Items");
        return Tiers3_4TestHelper.ReadCollectionItems(col);
    }

    private static void EnableFailedValidationFilter(ViewModelHandle vm, bool enabled)
    {
        if (Tiers3_4TestHelper.TrySetProperty(vm, enabled, "FilterFailedValidationOnly", "ShowFailedValidationOnly", "OnlyFailedValidation", "FailedValidationOnly"))
        {
            return;
        }

        Tiers3_4TestHelper.SetProperty(vm, enabled ? "FailedValidation" : "All", "SelectedStatusFilter", "StatusFilter", "FilterMode");
    }

    private static void EnableOverflowFilter(ViewModelHandle vm, bool enabled)
    {
        if (Tiers3_4TestHelper.TrySetProperty(vm, enabled, "FilterOverflowWarningOnly", "ShowOverflowOnly", "ShowOverflowWarningOnly", "OnlyOverflowWarning", "OverflowOnly"))
        {
            return;
        }

        Tiers3_4TestHelper.SetProperty(vm, enabled ? "OverflowWarning" : "All", "SelectedStatusFilter", "StatusFilter", "FilterMode");
    }

    private static async Task ApplyManualEditAsync(ViewModelHandle vm, object row, string newText)
    {
        Tiers3_4TestHelper.TrySetProperty(vm, row, "SelectedEntry", "CurrentEntry");

        (bool editedDirect, _) = await Tiers3_4TestHelper.TryInvokeViewModelMethodAsync(
            vm, ["EditEntry", "UpdateTranslation", "ApplyManualEdit", "SaveEdit", "CommitEdit"], row, newText);
        if (editedDirect)
        {
            return;
        }

        PropertyInfo? editProp =
            row.GetType().GetProperty("EditedText", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) ??
            row.GetType().GetProperty("TranslatedText", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) ??
            row.GetType().GetProperty("FinalPatchedText", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (editProp is not null && editProp.CanWrite)
        {
            editProp.SetValue(row, newText);
        }

        Tiers3_4TestHelper.TrySetProperty(vm, newText, "EditedText", "ManualEditText");

        if (Tiers3_4TestHelper.TryGetCommand(vm, ["SaveEdit", "CommitEdit", "ApplyEdit", "SaveManualEdit"], out ICommand? saveCmd) &&
            saveCmd is not null)
        {
            await Tiers3_4TestHelper.ExecuteCommandAsync(saveCmd, row);
            return;
        }

        await Tiers3_4TestHelper.InvokeViewModelMethodAsync(vm, ["SaveEdit", "CommitEdit", "ApplyEdit"], row);
    }
}
