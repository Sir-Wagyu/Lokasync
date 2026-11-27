using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F23_InlineTranslationGridAndRePatchUiTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_SearchQuery_FiltersEntriesCaseInsensitively()
    {
        // Arrange
        using TempWorkspace ws = new("f23_search");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t1-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Grid Search", ws.GameRootPath, "Loose");
        await db.InsertSubtitleEntriesAsync([
            AdaptersBackupUiTestHelper.CreateEntry(0, "k1", "a.json", "Welcome hero", status: EntryStatus.Translated, finalPatchedText: "Selamat datang"),
            AdaptersBackupUiTestHelper.CreateEntry(0, "k2", "a.json", "The Warden approaches", status: EntryStatus.Translated, finalPatchedText: "Sang Sipir datang"),
            AdaptersBackupUiTestHelper.CreateEntry(0, "k3", "a.json", "Aether Core ready", status: EntryStatus.Translated, finalPatchedText: "Inti Aether siap"),
            AdaptersBackupUiTestHelper.CreateEntry(0, "k4", "a.json", "Warden gate locked", status: EntryStatus.FailedValidation, finalPatchedText: null),
            AdaptersBackupUiTestHelper.CreateEntry(0, "k5", "a.json", "Exit menu", status: EntryStatus.Translated, finalPatchedText: "Keluar")
        ]);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        IReadOnlyList<object> filtered = [];

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            AdaptersBackupUiTestHelper.SetProperty(vm, "warden", "SearchText", "SearchQuery", "FilterText");
            filtered = GetFilteredRows(vm);
        });

        // Assert
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, r => Assert.Contains("Warden", GetRowString(r, "RawText") ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_FailedValidationFilter_ShowsOnlyFailedValidationRows()
    {
        // Arrange
        using TempWorkspace ws = new("f23_filter_failed");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t1-2";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Failed Filter", ws.GameRootPath, "Loose");
        await db.InsertSubtitleEntriesAsync([
            AdaptersBackupUiTestHelper.CreateEntry(0, "k1", "a.json", "OK line", status: EntryStatus.Translated, finalPatchedText: "Baris OK"),
            AdaptersBackupUiTestHelper.CreateEntry(0, "k2", "a.json", "<b>Broken</b>", status: EntryStatus.FailedValidation, finalPatchedText: null),
            AdaptersBackupUiTestHelper.CreateEntry(0, "k3", "a.json", "Edited line", status: EntryStatus.ManuallyEdited, finalPatchedText: "Disunting")
        ]);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        IReadOnlyList<object> filtered = [];

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            EnableFailedValidationFilter(vm, true);
            filtered = GetFilteredRows(vm);
        });

        // Assert
        object row = Assert.Single(filtered);
        Assert.Equal(EntryStatus.FailedValidation, GetRowStatus(row));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_OverflowWarningFilter_ShowsOnlyOverflowRows()
    {
        // Arrange
        using TempWorkspace ws = new("f23_filter_overflow");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t1-3";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Overflow Filter", ws.GameRootPath, "Loose");
        await db.InsertSubtitleEntriesAsync([
            AdaptersBackupUiTestHelper.CreateEntry(0, "k1", "a.json", "Hello", status: EntryStatus.Translated, finalPatchedText: "Halo", hasOverflowWarning: false),
            AdaptersBackupUiTestHelper.CreateEntry(0, "k2", "a.json", "Go", status: EntryStatus.Translated, finalPatchedText: "Berangkat sekarang juga!", hasOverflowWarning: true)
        ]);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        IReadOnlyList<object> filtered = [];

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            EnableOverflowFilter(vm, true);
            filtered = GetFilteredRows(vm);
        });

        // Assert
        object row = Assert.Single(filtered);
        Assert.True(GetRowBool(row, "HasOverflowWarning"));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_ManualEditValidTokens_SetsManuallyEditedAndPersistsToSqlite()
    {
        // Arrange
        using TempWorkspace ws = new("f23_manual_edit_valid");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t1-4";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Manual Edit", ws.GameRootPath, "Loose");
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        const string rawText = "<b>Welcome</b>, {player_name}!";
        (string masked, Dictionary<string, string> placeholders) = sanitizer.Mask(rawText);

        await db.InsertSubtitleEntriesAsync([
            AdaptersBackupUiTestHelper.CreateEntry(
                0, "k1", "dialog.json", rawText, maskedText: masked, placeholders: placeholders,
                translatedText: "Broken", finalPatchedText: null, status: EntryStatus.FailedValidation)
        ]);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            object row = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, row, "[TAG_0]Hai[TAG_1], [VAR_0]!");
        });

        // Assert
        SubtitleEntry persisted = Assert.Single(await db.GetAllEntriesAsync());
        Assert.Equal(EntryStatus.ManuallyEdited, persisted.Status);
        Assert.Equal("<b>Hai</b>, {player_name}!", persisted.FinalPatchedText);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_RePatchCommand_WritesManualEditToGameDiskWithZeroLlmCalls()
    {
        // Arrange
        using TempWorkspace ws = new("f23_repatch_zero_llm");
        ws.WriteTextFile("dialog.json", """{"greeting":"Hello hero"}""");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t1-5";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "RePatch", ws.GameRootPath, "Loose");

        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, ["dialog.json"], CancellationToken.None);

        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        List<SubtitleEntry> extracted = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        SubtitleEntry initial = extracted[0] with
        {
            TranslatedText = "Halo pahlawan",
            FinalPatchedText = "Halo pahlawan",
            Status = EntryStatus.Translated
        };
        await db.InsertSubtitleEntriesAsync([initial]);
        await adapter.PatchAsync(ws.GameRootPath, [initial], CancellationToken.None);

        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter, backupService);

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            object row = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, row, "Salam ksatria!");
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetRePatchCommand(vm));
        });

        // Assert
        Assert.Empty(mockRouter.Requests);
        using JsonDocument doc = JsonDocument.Parse(ws.ReadTextFile("dialog.json"));
        Assert.Equal("Salam ksatria!", doc.RootElement.GetProperty("greeting").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_ShortenOverflowingTranslation_ClearsHasOverflowWarning()
    {
        // Arrange
        using TempWorkspace ws = new("f23_shorten_overflow");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t1-6";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Shorten", ws.GameRootPath, "Loose");
        await db.InsertSubtitleEntriesAsync([
            AdaptersBackupUiTestHelper.CreateEntry(
                0, "k1", "a.json", "Start Game", // len 10 -> 11*4=44 <= 10*5=50
                translatedText: "Mulai Petualangan Ekspedisi Panjang Sekali",
                finalPatchedText: "Mulai Petualangan Ekspedisi Panjang Sekali",
                status: EntryStatus.Translated,
                hasOverflowWarning: true)
        ]);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            object row = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, row, "Mulai Main!");
        });

        // Assert
        SubtitleEntry persisted = Assert.Single(await db.GetAllEntriesAsync());
        Assert.Equal(EntryStatus.ManuallyEdited, persisted.Status);
        Assert.False(persisted.HasOverflowWarning);
        E2EAssertions.AssertOverflowWarningRule(persisted);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_SearchQueryNoMatch_ReturnsEmptyViewWithoutError()
    {
        // Arrange
        using TempWorkspace ws = new("f23_search_empty");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t2-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "No Match", ws.GameRootPath, "Loose");
        await db.InsertSubtitleEntriesAsync(
            Enumerable.Range(1, 5)
                .Select(i => AdaptersBackupUiTestHelper.CreateEntry(0, $"k{i}", "a.json", $"Dialog {i}", status: EntryStatus.Translated, finalPatchedText: $"Teks {i}"))
                .ToList());
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        int emptyCount = -1;
        int restoredCount = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            AdaptersBackupUiTestHelper.SetProperty(vm, "zzzz_nonexistent_term", "SearchText", "SearchQuery", "FilterText");
            emptyCount = GetFilteredRows(vm).Count;

            AdaptersBackupUiTestHelper.SetProperty(vm, string.Empty, "SearchText", "SearchQuery", "FilterText");
            restoredCount = GetFilteredRows(vm).Count;
        });

        // Assert
        Assert.Equal(0, emptyCount);
        Assert.Equal(5, restoredCount);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_ManualEditDropsRequiredPlaceholder_FailsTryUnmaskAndStaysSafeOnRePatch()
    {
        // Arrange
        using TempWorkspace ws = new("f23_edit_drops_var");
        const string rawText = "Welcome, {player_name}!";
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new { greeting = rawText }));
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t2-2";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Drop Var", ws.GameRootPath, "Loose");

        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, ["dialog.json"], CancellationToken.None);

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        (string masked, Dictionary<string, string> placeholders) = sanitizer.Mask(rawText);
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        List<SubtitleEntry> extracted = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        SubtitleEntry seeded = extracted[0] with
        {
            MaskedText = masked,
            Placeholders = placeholders,
            TranslatedText = "Selamat datang, [VAR_0]!",
            FinalPatchedText = "Selamat datang, {player_name}!",
            Status = EntryStatus.Translated
        };
        await db.InsertSubtitleEntriesAsync([seeded]);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler(), backupService);

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            object row = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, row, "Halo tanpa variabel");
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetRePatchCommand(vm));
        });

        // Assert
        SubtitleEntry persisted = Assert.Single(await db.GetAllEntriesAsync());
        E2EAssertions.AssertFailedValidationFallback(persisted, ws.ReadTextFile("dialog.json"));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_ManualEditExceeds25Percent_SetsHasOverflowWarningTrue()
    {
        // Arrange
        using TempWorkspace ws = new("f23_edit_overflow");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t2-3";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Edit Overflow", ws.GameRootPath, "Loose");
        await db.InsertSubtitleEntriesAsync([
            AdaptersBackupUiTestHelper.CreateEntry(0, "k1", "a.json", "12345678", translatedText: "12345678", finalPatchedText: "12345678", status: EntryStatus.Translated)
        ]);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            object row = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, row, new string('X', 15)); // 15*4=60 > 8*5=40
        });

        // Assert
        SubtitleEntry persisted = Assert.Single(await db.GetAllEntriesAsync());
        Assert.Equal(EntryStatus.ManuallyEdited, persisted.Status);
        Assert.True(persisted.HasOverflowWarning);
        E2EAssertions.AssertOverflowWarningRule(persisted);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_CombinedSearchAndFlagFilter_AppliesLogicalAndIntersection()
    {
        // Arrange
        using TempWorkspace ws = new("f23_combined_filter");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t2-4";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Combined", ws.GameRootPath, "Loose");
        await db.InsertSubtitleEntriesAsync([
            AdaptersBackupUiTestHelper.CreateEntry(0, "k1", "a.json", "Alpha broken", status: EntryStatus.FailedValidation),
            AdaptersBackupUiTestHelper.CreateEntry(0, "k2", "a.json", "Beta broken", status: EntryStatus.FailedValidation),
            AdaptersBackupUiTestHelper.CreateEntry(0, "k3", "a.json", "Alpha ok", status: EntryStatus.Translated, finalPatchedText: "Alpha baik")
        ]);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        IReadOnlyList<object> filtered = [];

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            AdaptersBackupUiTestHelper.SetProperty(vm, "alpha", "SearchText", "SearchQuery", "FilterText");
            EnableFailedValidationFilter(vm, true);
            filtered = GetFilteredRows(vm);
        });

        // Assert
        object row = Assert.Single(filtered);
        Assert.Equal("Alpha broken", GetRowString(row, "RawText"));
        Assert.Equal(EntryStatus.FailedValidation, GetRowStatus(row));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_ManualEditEmptyStringOnNonEmptyRawText_DoesNotCrashAndHandlesGracefully()
    {
        // Arrange
        using TempWorkspace ws = new("f23_edit_empty");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f23-t2-5";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Empty Edit", ws.GameRootPath, "Loose");
        await db.InsertSubtitleEntriesAsync([
            AdaptersBackupUiTestHelper.CreateEntry(0, "k1", "a.json", "Hello", translatedText: "Halo", finalPatchedText: "Halo", status: EntryStatus.Translated)
        ]);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            await LoadGridAsync(vm, projectId, ws.GameRootPath);
            object row = Assert.Single(GetFilteredRows(vm));
            await ApplyManualEditAsync(vm, row, string.Empty);
        });

        // Assert
        SubtitleEntry persisted = Assert.Single(await db.GetAllEntriesAsync());
        Assert.False(persisted.HasOverflowWarning);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F23")]
    public async Task InlineGridViewModel_RePatchWhenNoProjectOrNoBackup_EnforcesBackupGateOrBlocksSafely()
    {
        // Arrange
        using TempWorkspace ws = new("f23_repatch_nobackup");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);

        // Blocking path for backup root so unbacked RePatch cannot silently bypass backup
        string blockingFile = Path.Combine(ws.RootPath, "blocking_file.txt");
        File.WriteAllText(blockingFile, "not a dir");
        IBackupService failingBackup = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, blockingFile);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler(), failingBackup);

        bool blockedOrLogged = false;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);

            ICommand rePatchCmd = GetRePatchCommand(vm);
            if (!rePatchCmd.CanExecute(null))
            {
                blockedOrLogged = true;
            }
            else
            {
                try
                {
                    await AdaptersBackupUiTestHelper.ExecuteCommandAsync(rePatchCmd);
                    if (AdaptersBackupUiTestHelper.TryGetProperty(vm, out string? err, "ErrorMessage", "ValidationError", "StatusMessage") &&
                        !string.IsNullOrWhiteSpace(err))
                    {
                        blockedOrLogged = true;
                    }
                }
                catch (Exception ex) when (
                    (ex is InvalidOperationException &&
                     !ex.Message.StartsWith("Could not bind", StringComparison.OrdinalIgnoreCase)) ||
                    ex is FileNotFoundException)
                {
                    blockedOrLogged = true;
                }
            }
        });

        // Assert
        Assert.True(blockedOrLogged);
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    private static object[] CreateUiDependencies(
        OpaqueContractBridge bridge,
        DatabaseSession db,
        TempWorkspace ws,
        MockLlmRouterHandler mockRouter,
        IBackupService? customBackup = null)
    {
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IBackupService backupService = customBackup ?? AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        IReadOnlyList<IEngineAdapter> adapters = bridge.CreateAllEngineAdapters();
        PipelineOrchestratorHandle orchestrator = AdaptersBackupUiTestHelper.CreatePipelineOrchestratorSafe(
            bridge, db, sanitizer, translator, backupService, adapters, ws.BackupRootPath);
        SequentialWorkerHandle worker = AdaptersBackupUiTestHelper.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        return AdaptersBackupUiTestHelper.BuildFullDependencyGraph(
            db, sanitizer, translator, backupService, adapters, orchestrator, worker);
    }

    private static async Task LoadGridAsync(ViewModelHandle vm, string projectId, string gameRootPath)
    {
        AdaptersBackupUiTestHelper.TrySetProperty(vm, projectId, "ProjectId", "CurrentProjectId");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, gameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "ProjectPath");

        (bool loadedWithId, _) = await AdaptersBackupUiTestHelper.TryInvokeViewModelMethodAsync(
            vm, ["LoadEntries", "LoadGrid", "Refresh", "LoadProject"], projectId);
        if (loadedWithId)
        {
            return;
        }

        (bool loadedNoArgs, _) = await AdaptersBackupUiTestHelper.TryInvokeViewModelMethodAsync(
            vm, ["LoadEntries", "LoadGrid", "Refresh"]);
        if (loadedNoArgs)
        {
            return;
        }

        ICommand refreshCmd = AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["Refresh", "LoadEntries", "Reload"]);
        await AdaptersBackupUiTestHelper.ExecuteCommandAsync(refreshCmd);
    }

    private static IReadOnlyList<object> GetFilteredRows(ViewModelHandle vm)
    {
        object? col = AdaptersBackupUiTestHelper.GetProperty<object>(
            vm,
            "FilteredEntries", "EntriesView", "GridEntries", "Entries", "Items");
        return AdaptersBackupUiTestHelper.ReadCollectionItems(col);
    }

    private static void EnableFailedValidationFilter(ViewModelHandle vm, bool enabled)
    {
        if (AdaptersBackupUiTestHelper.TrySetProperty(vm, enabled, "FilterFailedValidationOnly", "ShowFailedValidationOnly", "OnlyFailedValidation", "FailedValidationOnly"))
        {
            return;
        }

        AdaptersBackupUiTestHelper.SetProperty(vm, "FailedValidation", "SelectedStatusFilter", "StatusFilter", "FilterMode");
    }

    private static void EnableOverflowFilter(ViewModelHandle vm, bool enabled)
    {
        if (AdaptersBackupUiTestHelper.TrySetProperty(vm, enabled, "FilterOverflowWarningOnly", "ShowOverflowOnly", "ShowOverflowWarningOnly", "OnlyOverflowWarning", "OverflowOnly"))
        {
            return;
        }

        AdaptersBackupUiTestHelper.SetProperty(vm, "OverflowWarning", "SelectedStatusFilter", "StatusFilter", "FilterMode");
    }

    private static async Task ApplyManualEditAsync(ViewModelHandle vm, object row, string newText)
    {
        AdaptersBackupUiTestHelper.TrySetProperty(vm, row, "SelectedEntry", "CurrentEntry");

        (bool editedDirect, _) = await AdaptersBackupUiTestHelper.TryInvokeViewModelMethodAsync(
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

        AdaptersBackupUiTestHelper.TrySetProperty(vm, newText, "EditedText", "ManualEditText");

        if (AdaptersBackupUiTestHelper.TryGetCommand(vm, ["SaveEdit", "CommitEdit", "ApplyEdit", "SaveManualEdit"], out ICommand? saveCmd) &&
            saveCmd is not null)
        {
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(saveCmd, row);
            return;
        }

        await AdaptersBackupUiTestHelper.InvokeViewModelMethodAsync(vm, ["SaveEdit", "CommitEdit", "ApplyEdit"], row);
    }

    private static ICommand GetRePatchCommand(ViewModelHandle vm) =>
        AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["RePatch", "InstantRePatch"]);

    private static string? GetRowString(object row, string propName) =>
        row.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(row)?.ToString();

    private static bool GetRowBool(object row, string propName) =>
        row.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(row) is true;

    private static EntryStatus GetRowStatus(object row)
    {
        object? val = row.GetType().GetProperty("Status", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(row);
        return val is EntryStatus status ? status : throw new InvalidOperationException($"Row '{row.GetType().FullName}' Status was '{val}'.");
    }
}
