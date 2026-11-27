using System.Collections;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using Lokasync.Core.Interfaces;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F21_GlossaryAndToneManagerUiTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_SetGameTitleAndGenreTone_PersistsToNarrativeContextJson()
    {
        // Arrange
        using TempWorkspace ws = new("f21_title_tone");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t1-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Initial Title", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws);

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            AdaptersBackupUiTestHelper.SetProperty(vm, "Expedition Zero", "GameTitle");
            AdaptersBackupUiTestHelper.SetProperty(vm, "Sci-Fi Thriller", "GenreTone");
            await SaveGlossaryAsync(vm);
        });

        // Assert
        string? json = await AdaptersBackupUiTestHelper.QueryProjectNarrativeContextJsonAsync(db.Connection, projectId);
        Assert.False(string.IsNullOrWhiteSpace(json));
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("Expedition Zero", doc.RootElement.GetProperty("game_title").GetString());
        Assert.Equal("Sci-Fi Thriller", doc.RootElement.GetProperty("genre_tone").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_AddGlossaryTerms_AddsToCollectionAndPersistsToSqlite()
    {
        // Arrange
        using TempWorkspace ws = new("f21_add_terms");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t1-2";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Expedition Zero", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws);

        int uiCount = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            await AddTermAsync(vm, "The Warden", "Sang Sipir", "Antagonis utama");
            await AddTermAsync(vm, "Aether Core", "Inti Aether", "Istilah teknologi");
            await SaveGlossaryAsync(vm);

            uiCount = GetGlossaryItems(vm).Count;
        });

        // Assert
        Assert.Equal(2, uiCount);
        string? json = await AdaptersBackupUiTestHelper.QueryProjectNarrativeContextJsonAsync(db.Connection, projectId);
        Assert.False(string.IsNullOrWhiteSpace(json));
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement glossary = doc.RootElement.GetProperty("glossary");
        Assert.Equal(2, glossary.GetArrayLength());
        Assert.Equal("The Warden", glossary[0].GetProperty("source").GetString());
        Assert.Equal("Sang Sipir", glossary[0].GetProperty("target").GetString());
        Assert.Equal("Antagonis utama", glossary[0].GetProperty("note").GetString());
        Assert.Equal("Aether Core", glossary[1].GetProperty("source").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_EditExistingGlossaryTerm_UpdatesTargetAndNoteInSqlite()
    {
        // Arrange
        using TempWorkspace ws = new("f21_edit_term");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t1-3";
        string seedJson = """{"game_title":"Expedition Zero","genre_tone":"Sci-Fi","glossary":[{"source":"The Warden","target":"Sang Sipir","note":"Lama"}]}""";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Expedition Zero", ws.GameRootPath, "Loose", seedJson);
        object[] deps = CreateUiDependencies(bridge, db, ws);

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            IReadOnlyList<object> items = GetGlossaryItems(vm);
            Assert.Single(items);
            SetTermProperty(items[0], "Penjaga", "Target");
            SetTermProperty(items[0], "Updated", "Note");
            await SaveGlossaryAsync(vm);
        });

        // Assert
        string? json = await AdaptersBackupUiTestHelper.QueryProjectNarrativeContextJsonAsync(db.Connection, projectId);
        Assert.False(string.IsNullOrWhiteSpace(json));
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement glossary = doc.RootElement.GetProperty("glossary");
        Assert.Equal(1, glossary.GetArrayLength());
        Assert.Equal("Penjaga", glossary[0].GetProperty("target").GetString());
        Assert.Equal("Updated", glossary[0].GetProperty("note").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_RemoveGlossaryTerm_DeletesTermAndUpdatesSqlite()
    {
        // Arrange
        using TempWorkspace ws = new("f21_remove_term");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t1-4";
        string seedJson = """{"game_title":"Expedition Zero","genre_tone":"Sci-Fi","glossary":[{"source":"The Warden","target":"Sang Sipir","note":"n1"},{"source":"Aether Core","target":"Inti Aether","note":"n2"}]}""";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Expedition Zero", ws.GameRootPath, "Loose", seedJson);
        object[] deps = CreateUiDependencies(bridge, db, ws);

        int remainingUiCount = -1;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            IReadOnlyList<object> items = GetGlossaryItems(vm);
            object warden = items.First(i => GetTermProperty(i, "Source") == "The Warden");
            await RemoveTermAsync(vm, warden);
            await SaveGlossaryAsync(vm);
            remainingUiCount = GetGlossaryItems(vm).Count;
        });

        // Assert
        Assert.Equal(1, remainingUiCount);
        string? json = await AdaptersBackupUiTestHelper.QueryProjectNarrativeContextJsonAsync(db.Connection, projectId);
        using JsonDocument doc = JsonDocument.Parse(json!);
        JsonElement glossary = doc.RootElement.GetProperty("glossary");
        Assert.Equal(1, glossary.GetArrayLength());
        Assert.Equal("Aether Core", glossary[0].GetProperty("source").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_LoadExistingProject_RestoresTitleToneAndGlossaryFromSqlite()
    {
        // Arrange
        using TempWorkspace ws = new("f21_load_project");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t1-5";
        string seedJson = """{"game_title":"Arcadia","genre_tone":"Dark Fantasy","glossary":[{"source":"Relic","target":"Relik","note":"Benda kuno"}]}""";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Arcadia", ws.GameRootPath, "Loose", seedJson);
        object[] deps = CreateUiDependencies(bridge, db, ws);

        string? loadedTitle = null;
        string? loadedTone = null;
        int loadedCount = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out loadedTitle, "GameTitle"));
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out loadedTone, "GenreTone"));
            loadedCount = GetGlossaryItems(vm).Count;
        });

        // Assert
        Assert.Equal("Arcadia", loadedTitle);
        Assert.Equal("Dark Fantasy", loadedTone);
        Assert.Equal(1, loadedCount);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_SavedContext_FeedsTranslatorPromptBuilderAccurately()
    {
        // Arrange
        using TempWorkspace ws = new("f21_prompt_integration");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t1-6";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Expedition Zero", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws);

        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            AdaptersBackupUiTestHelper.SetProperty(vm, "Expedition Zero", "GameTitle");
            AdaptersBackupUiTestHelper.SetProperty(vm, "Melancholic Drama", "GenreTone");
            await AddTermAsync(vm, "The Warden", "Sang Sipir", "Formal");
            await SaveGlossaryAsync(vm);
        });

        string? savedJson = await AdaptersBackupUiTestHelper.QueryProjectNarrativeContextJsonAsync(db.Connection, projectId);
        using JsonDocument doc = JsonDocument.Parse(savedJson!);
        string gameTitle = doc.RootElement.GetProperty("game_title").GetString()!;
        string genreTone = doc.RootElement.GetProperty("genre_tone").GetString()!;
        List<(string Source, string Target, string Note)> glossary = doc.RootElement
            .GetProperty("glossary")
            .EnumerateArray()
            .Select(el => (
                el.GetProperty("source").GetString()!,
                el.GetProperty("target").GetString()!,
                el.TryGetProperty("note", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty))
            .ToList();

        // Act
        string systemPrompt = bridge.BuildSystemPrompt(gameTitle, genreTone, glossary);

        // Assert
        Assert.Contains("Expedition Zero", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Melancholic Drama", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("The Warden", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Sang Sipir", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_EmptySourceOrTargetTerm_RejectsAddWithValidationError()
    {
        // Arrange
        using TempWorkspace ws = new("f21_empty_term");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t2-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Validation", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws);

        int countAfter = -1;
        string? errMsg = null;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            await TryAddExpectRejectedAsync(vm, string.Empty, "Sang Sipir", "note");
            await TryAddExpectRejectedAsync(vm, "The Warden", "   ", "note");
            countAfter = GetGlossaryItems(vm).Count;
            AdaptersBackupUiTestHelper.TryGetProperty(vm, out errMsg, "ErrorMessage", "ValidationError", "GlossaryError");
        });

        // Assert
        Assert.Equal(0, countAfter);
        Assert.False(string.IsNullOrWhiteSpace(errMsg));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_EmptyOptionalNote_AllowsAddAndSavesCleanly()
    {
        // Arrange
        using TempWorkspace ws = new("f21_empty_note");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t2-2";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Optional Note", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws);

        int countAfter = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            await AddTermAsync(vm, "Mana", "Mana", string.Empty);
            await SaveGlossaryAsync(vm);
            countAfter = GetGlossaryItems(vm).Count;
        });

        // Assert
        Assert.Equal(1, countAfter);
        string? json = await AdaptersBackupUiTestHelper.QueryProjectNarrativeContextJsonAsync(db.Connection, projectId);
        using JsonDocument doc = JsonDocument.Parse(json!);
        Assert.Equal("Mana", doc.RootElement.GetProperty("glossary")[0].GetProperty("source").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_DuplicateSourceTerm_PreventsDuplicateOrOverwritesDeterministically()
    {
        // Arrange
        using TempWorkspace ws = new("f21_duplicate_source");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t2-3";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Dup Test", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws);

        int countAfter = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            await AddTermAsync(vm, "The Warden", "Sang Sipir", "n1");
            try
            {
                await AddTermAsync(vm, "The Warden", "Sipir", "n2");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Rejecting duplicate source is valid deterministic behavior
            }

            await SaveGlossaryAsync(vm);
            countAfter = GetGlossaryItems(vm).Count;
        });

        // Assert
        Assert.Equal(1, countAfter);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_SpecialCharsQuotesAndUnicode_RoundTripsThroughSqliteJson()
    {
        // Arrange
        using TempWorkspace ws = new("f21_unicode_quotes");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t2-4";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Unicode", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws);

        const string tone = "Gunakan 'aku-kamu' & \"saya-Anda\" — é";
        const string source = "O'Connor";
        const string target = "O'Connor \"Komandan\"";

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            AdaptersBackupUiTestHelper.SetProperty(vm, "Test Title", "GameTitle");
            AdaptersBackupUiTestHelper.SetProperty(vm, tone, "GenreTone");
            await AddTermAsync(vm, source, target, "Special chars");
            await SaveGlossaryAsync(vm);
        });

        // Assert
        string? json = await AdaptersBackupUiTestHelper.QueryProjectNarrativeContextJsonAsync(db.Connection, projectId);
        using JsonDocument doc = JsonDocument.Parse(json!);
        Assert.Equal(tone, doc.RootElement.GetProperty("genre_tone").GetString());
        Assert.Equal(source, doc.RootElement.GetProperty("glossary")[0].GetProperty("source").GetString());
        Assert.Equal(target, doc.RootElement.GetProperty("glossary")[0].GetProperty("target").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_SaveWithNoActiveProject_HandlesGracefullyWithoutCrash()
    {
        // Arrange
        using TempWorkspace ws = new("f21_no_project");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        object[] deps = CreateUiDependencies(bridge, db, ws);

        bool handledSafely = false;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            ICommand saveCmd = AdaptersBackupUiTestHelper.GetCommandSafe(
                vm,
                ["SaveGlossary", "SaveNarrativeContext", "SaveContext", "Save"]);

            if (!saveCmd.CanExecute(null))
            {
                handledSafely = true;
            }
            else
            {
                try
                {
                    await AdaptersBackupUiTestHelper.ExecuteCommandAsync(saveCmd);
                }
                catch (InvalidOperationException)
                {
                    handledSafely = true;
                }

                if (AdaptersBackupUiTestHelper.TryGetProperty(vm, out string? err, "ErrorMessage", "ValidationError", "StatusMessage") &&
                    !string.IsNullOrWhiteSpace(err))
                {
                    handledSafely = true;
                }
            }
        });

        // Assert
        Assert.True(handledSafely, "Expected Save with no active project to disable command or surface controlled error.");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F21")]
    public async Task GlossaryViewModel_LargeGlossary100Terms_PersistsAndReloadsWithoutTruncation()
    {
        // Arrange
        using TempWorkspace ws = new("f21_100_terms");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f21-t2-6";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Scale 100", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws);

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);

            await LoadProjectIntoVmAsync(vm, projectId);
            for (int i = 1; i <= 100; i++)
            {
                await AddTermAsync(vm, $"Term_{i:D3}", $"Istilah_{i:D3}", $"Note {i}");
            }

            await SaveGlossaryAsync(vm);
        });

        int reloadedCount = 0;
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm2 = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Glossary", "Narrative", "Context", "Main"],
                deps);
            await LoadProjectIntoVmAsync(vm2, projectId);
            reloadedCount = GetGlossaryItems(vm2).Count;
        });

        // Assert
        Assert.Equal(100, reloadedCount);
        string? json = await AdaptersBackupUiTestHelper.QueryProjectNarrativeContextJsonAsync(db.Connection, projectId);
        using JsonDocument doc = JsonDocument.Parse(json!);
        Assert.Equal(100, doc.RootElement.GetProperty("glossary").GetArrayLength());
    }

    private static object[] CreateUiDependencies(
        OpaqueContractBridge bridge,
        DatabaseSession db,
        TempWorkspace ws)
    {
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(new MockLlmRouterHandler());
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        IReadOnlyList<IEngineAdapter> adapters = bridge.CreateAllEngineAdapters();
        PipelineOrchestratorHandle orchestrator = AdaptersBackupUiTestHelper.CreatePipelineOrchestratorSafe(
            bridge, db, sanitizer, translator, backupService, adapters, ws.BackupRootPath);
        SequentialWorkerHandle worker = AdaptersBackupUiTestHelper.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        return AdaptersBackupUiTestHelper.BuildFullDependencyGraph(
            db, sanitizer, translator, backupService, adapters, orchestrator, worker);
    }

    private static async Task LoadProjectIntoVmAsync(ViewModelHandle vm, string projectId)
    {
        AdaptersBackupUiTestHelper.TrySetProperty(vm, projectId, "ProjectId", "CurrentProjectId");
        await AdaptersBackupUiTestHelper.TryInvokeViewModelMethodAsync(vm, ["LoadProject", "LoadNarrative", "LoadGlossary"], projectId);
    }

    private static async Task AddTermAsync(ViewModelHandle vm, string source, string target, string note)
    {
        AdaptersBackupUiTestHelper.TrySetProperty(vm, source, "NewTermSource", "SourceTerm", "NewSource", "TermSource");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, target, "NewTermTarget", "TargetTerm", "NewTarget", "TermTarget");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, note, "NewTermNote", "TermNote", "NewNote", "Note");

        if (AdaptersBackupUiTestHelper.TryGetCommand(vm, ["AddGlossary", "AddTerm", "AddEntry", "Add"], out ICommand? addCmd) &&
            addCmd is not null)
        {
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(addCmd);
            return;
        }

        await AdaptersBackupUiTestHelper.InvokeViewModelMethodAsync(vm, ["AddGlossary", "AddTerm"], source, target, note);
    }

    private static async Task TryAddExpectRejectedAsync(ViewModelHandle vm, string source, string target, string note)
    {
        AdaptersBackupUiTestHelper.TrySetProperty(vm, source, "NewTermSource", "SourceTerm", "NewSource", "TermSource");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, target, "NewTermTarget", "TargetTerm", "NewTarget", "TermTarget");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, note, "NewTermNote", "TermNote", "NewNote", "Note");

        try
        {
            if (AdaptersBackupUiTestHelper.TryGetCommand(vm, ["AddGlossary", "AddTerm", "AddEntry", "Add"], out ICommand? addCmd) &&
                addCmd is not null)
            {
                if (addCmd.CanExecute(null))
                {
                    await AdaptersBackupUiTestHelper.ExecuteCommandAsync(addCmd);
                }

                return;
            }

            await AdaptersBackupUiTestHelper.InvokeViewModelMethodAsync(vm, ["AddGlossary", "AddTerm"], source, target, note);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Expected rejection
        }
    }

    private static async Task RemoveTermAsync(ViewModelHandle vm, object termItem)
    {
        if (AdaptersBackupUiTestHelper.TryGetCommand(vm, ["RemoveGlossary", "DeleteGlossary", "RemoveTerm", "DeleteTerm", "Remove"], out ICommand? removeCmd) &&
            removeCmd is not null)
        {
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(removeCmd, termItem);
            return;
        }

        await AdaptersBackupUiTestHelper.InvokeViewModelMethodAsync(vm, ["RemoveGlossary", "DeleteGlossary", "RemoveTerm"], termItem);
    }

    private static async Task SaveGlossaryAsync(ViewModelHandle vm)
    {
        if (AdaptersBackupUiTestHelper.TryGetCommand(vm, ["SaveGlossary", "SaveNarrativeContext", "SaveContext", "Save"], out ICommand? saveCmd) &&
            saveCmd is not null)
        {
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(saveCmd);
            return;
        }

        await AdaptersBackupUiTestHelper.InvokeViewModelMethodAsync(vm, ["SaveGlossary", "SaveNarrative", "Save"]);
    }

    private static IReadOnlyList<object> GetGlossaryItems(ViewModelHandle vm)
    {
        object? col = AdaptersBackupUiTestHelper.GetProperty<object>(
            vm,
            "GlossaryTerms", "GlossaryEntries", "GlossaryItems", "Glossary", "Terms");
        return AdaptersBackupUiTestHelper.ReadCollectionItems(col);
    }

    private static string? GetTermProperty(object item, string propName) =>
        item.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item)?.ToString();

    private static void SetTermProperty(object item, string value, string propName)
    {
        PropertyInfo? prop = item.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null && prop.CanWrite)
        {
            prop.SetValue(item, value);
            return;
        }

        throw new InvalidOperationException($"Writable property '{propName}' not found on glossary item '{item.GetType().FullName}'.");
    }
}
