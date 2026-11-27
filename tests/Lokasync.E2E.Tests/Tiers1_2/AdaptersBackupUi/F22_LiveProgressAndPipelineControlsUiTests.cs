using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F22_LiveProgressAndPipelineControlsUiTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_InitialLoadedProjectState_HasValidCommandCanExecuteFlags()
    {
        // Arrange
        using TempWorkspace ws = new("f22_initial_commands");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t1-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Initial State", ws.GameRootPath, "Loose");
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        bool canStart = false;
        bool canPause = true;
        bool canResume = true;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaAsync(() =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile());
            canStart = GetStartCommand(vm).CanExecute(null);
            canPause = GetPauseCommand(vm).CanExecute(null);
            canResume = GetResumeCommand(vm).CanExecute(null);
        });

        // Assert
        Assert.True(canStart);
        Assert.False(canPause);
        Assert.False(canResume);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_StartCommand_TranslatesAllEntriesAndUpdatesProgressTo100Percent()
    {
        // Arrange
        using TempWorkspace ws = new("f22_start_100");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t1-2";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Start 100%", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        int completed = 0;
        int total = 0;
        double progressPct = 0;
        int logCount = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile(batchSize: 2));
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetStartCommand(vm));

            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out completed, "CompletedCount", "ProcessedCount", "TranslatedCount"));
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out total, "TotalCount", "TotalEntriesCount"));
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out progressPct, "ProgressPercentage", "ProgressPercent", "Progress"));
            logCount = GetLogItems(vm).Count;
        });

        // Assert
        IReadOnlyList<SubtitleEntry> persisted = await db.GetAllEntriesAsync();
        Assert.NotEmpty(persisted);
        Assert.All(persisted, e => Assert.Equal(EntryStatus.Translated, e.Status));
        Assert.True(total > 0);
        Assert.Equal(total, completed);
        Assert.Equal(100.0, progressPct, precision: 1);
        Assert.True(logCount > 0);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_PauseAndResumeCommands_CheckpointsAndCompletesRemainingPendingRows()
    {
        // Arrange
        using TempWorkspace ws = new("f22_pause_resume");
        Dictionary<string, string> lines = Enumerable.Range(1, 20)
            .ToDictionary(i => $"line_{i:D2}", i => $"Dialog line number {i}");
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(lines));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t1-3";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Pause Resume", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            SimulatedDelay = TimeSpan.FromMilliseconds(120),
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        int countAfterPause = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile(batchSize: 5));
            Task runTask = AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetStartCommand(vm));

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (mockRouter.Requests.Count < 1 && DateTime.UtcNow < deadline && !runTask.IsCompleted)
            {
                await Task.Delay(25);
            }

            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetPauseCommand(vm));
            await runTask;

            IReadOnlyList<SubtitleEntry> midState = await db.GetAllEntriesAsync();
            countAfterPause = midState.Count(e => e.Status == EntryStatus.Translated);
            Assert.True(countAfterPause < 20, "Expected Pause to stop before all 20 entries completed.");
            Assert.Contains(midState, e => e.Status == EntryStatus.Pending);

            mockRouter.SimulatedDelay = TimeSpan.Zero;
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetResumeCommand(vm));
        });

        // Assert
        IReadOnlyList<SubtitleEntry> finalEntries = await db.GetAllEntriesAsync();
        Assert.Equal(20, finalEntries.Count);
        Assert.All(finalEntries, e => Assert.Equal(EntryStatus.Translated, e.Status));
        Assert.True(countAfterPause >= 0);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_Http429AutoPause_ShowsAutoPauseBannerAndResumesAfterProfileFix()
    {
        // Arrange
        using TempWorkspace ws = new("f22_autopause_429");
        Dictionary<string, string> lines = Enumerable.Range(1, 6)
            .ToDictionary(i => $"line_{i}", i => $"Line {i}");
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(lines));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t1-4";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "AutoPause 429", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mockRouter.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mockRouter.EnqueueHttpStatus(HttpStatusCode.TooManyRequests);
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        bool isAutoPausedOn429 = false;
        string? autoPauseBanner = null;
        bool resumeEnabledOn429 = false;
        bool isAutoPausedAfterResume = true;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            RouterProfile initialProfile = MockLlmRouterHandler.CreateProfile(apiKey: "old-key", batchSize: 2);
            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, initialProfile);
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetStartCommand(vm));

            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out isAutoPausedOn429, "IsAutoPaused", "IsPaused"));
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out autoPauseBanner, "AutoPauseMessage", "AutoPauseBanner", "BannerMessage", "StatusMessage"));
            resumeEnabledOn429 = GetResumeCommand(vm).CanExecute(null);

            RouterProfile updatedProfile = initialProfile with { ApiKey = "new-valid-key" };
            AdaptersBackupUiTestHelper.TrySetProperty(vm, updatedProfile, "ActiveRouterProfile", "RouterProfile", "Profile");
            AdaptersBackupUiTestHelper.TrySetProperty(vm, "new-valid-key", "ApiKey");

            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetResumeCommand(vm));
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out isAutoPausedAfterResume, "IsAutoPaused", "IsPaused"));
        });

        // Assert
        Assert.True(isAutoPausedOn429);
        Assert.False(string.IsNullOrWhiteSpace(autoPauseBanner));
        Assert.True(resumeEnabledOn429);
        Assert.False(isAutoPausedAfterResume);
        IReadOnlyList<SubtitleEntry> finalEntries = await db.GetAllEntriesAsync();
        Assert.Equal(6, finalEntries.Count);
        Assert.All(finalEntries, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_PatchGameCommand_CreatesBackupAndPatchesGameFiles()
    {
        // Arrange
        using TempWorkspace ws = new("f22_patch_cmd");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t1-5";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Patch Cmd", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[PAT] {kv.Value}")
        };
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile());
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetStartCommand(vm));
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetPatchGameCommand(vm));
        });

        // Assert
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        Assert.Contains("[PAT] ", ws.ReadTextFile(manifest.JsonRelativePath), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_RestoreOriginalCommand_RestoresOriginalFilesAndLogsAction()
    {
        // Arrange
        using TempWorkspace ws = new("f22_restore_cmd");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t1-6";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Restore Cmd", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[PAT] {kv.Value}")
        };
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        int logCountAfterRestore = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile());
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetStartCommand(vm));
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetPatchGameCommand(vm));
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetRestoreCommand(vm));
            logCountAfterRestore = GetLogItems(vm).Count;
        });

        // Assert
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
        Assert.True(logCountAfterRestore > 0);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_WhileRunning_DisablesStartPatchRestoreAndEnablesPause()
    {
        // Arrange
        using TempWorkspace ws = new("f22_running_canexecute");
        ws.WriteTextFile("dialog.json", """{"a":"Line 1","b":"Line 2"}""");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t2-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Running State", ws.GameRootPath, "Loose");

        using SemaphoreSlim inFlightEntered = new(0, 1);
        using SemaphoreSlim releaseHttp = new(0, 1);
        MockLlmRouterHandler mockRouter = new()
        {
            // Force SendAsync to yield asynchronously before invoking the blocking responder so STA thread is never blocked.
            SimulatedDelay = TimeSpan.FromMilliseconds(20)
        };
        mockRouter.EnqueueCustom(req =>
        {
            inFlightEntered.Release();
            releaseHttp.Wait(TimeSpan.FromSeconds(5));
            Dictionary<string, string> resp = (req.ParsedInputBatch ?? new Dictionary<string, string>())
                .ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}");
            string envelope = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-hold",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = JsonSerializer.Serialize(resp) } } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            };
        });
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        bool startWhileRunning = true;
        bool patchWhileRunning = true;
        bool restoreWhileRunning = true;
        bool pauseWhileRunning = false;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile());
            Task runTask = AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetStartCommand(vm));
            Assert.True(await inFlightEntered.WaitAsync(TimeSpan.FromSeconds(5)));

            startWhileRunning = GetStartCommand(vm).CanExecute(null);
            patchWhileRunning = GetPatchGameCommand(vm).CanExecute(null);
            restoreWhileRunning = GetRestoreCommand(vm).CanExecute(null);
            pauseWhileRunning = GetPauseCommand(vm).CanExecute(null);

            releaseHttp.Release();
            await runTask;
        });

        // Assert
        Assert.False(startWhileRunning);
        Assert.False(patchWhileRunning);
        Assert.False(restoreWhileRunning);
        Assert.True(pauseWhileRunning);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_Http401And403AuthFailure_TriggersAutoPauseAndSavesCheckpoint()
    {
        // Arrange
        HttpStatusCode[] authFailureCodes = [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden];

        // Act & Assert
        foreach (HttpStatusCode statusCode in authFailureCodes)
        {
            await VerifyAuthFailureAutoPauseAsync(statusCode);
        }
    }

    private static async Task VerifyAuthFailureAutoPauseAsync(HttpStatusCode authFailureStatus)
    {
        using TempWorkspace ws = new($"f22_autopause_{(int)authFailureStatus}");
        Dictionary<string, string> lines = Enumerable.Range(1, 6)
            .ToDictionary(i => $"line_{i}", i => $"Line {i}");
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(lines));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        string projectId = $"proj-f22-t2-{(int)authFailureStatus}";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, $"AutoPause {(int)authFailureStatus}", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new();
        mockRouter.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mockRouter.EnqueueHttpStatus(authFailureStatus); // 401 or 403 on batch 2 -> must stop before batch 3
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        bool isAutoPaused = false;

        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile(batchSize: 2));
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetStartCommand(vm));
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out isAutoPaused, "IsAutoPaused", "IsPaused"));
        });

        Assert.Equal(2, mockRouter.Requests.Count);
        Assert.True(isAutoPaused);
        IReadOnlyList<SubtitleEntry> entries = await db.GetAllEntriesAsync();
        Assert.Equal(2, entries.Count(e => e.Status == EntryStatus.Translated));
        Assert.Equal(4, entries.Count(e => e.Status == EntryStatus.Pending));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_ZeroPercentAndIdleState_CannotExecutePauseOrResume()
    {
        // Arrange
        using TempWorkspace ws = new("f22_idle_pause_resume");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        bool canPause = true;
        bool canResume = true;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaAsync(() =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ICommand pauseCmd = GetPauseCommand(vm);
            ICommand resumeCmd = GetResumeCommand(vm);
            canPause = pauseCmd.CanExecute(null);
            canResume = resumeCmd.CanExecute(null);
        });

        // Assert
        Assert.False(canPause);
        Assert.False(canResume);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_TranslatorFatalHttp500_LogsErrorAndRecoversToIdleState()
    {
        // Arrange
        using TempWorkspace ws = new("f22_fatal_500");
        ws.WriteTextFile("dialog.json", """{"line":"Hello"}""");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t2-4";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Fatal 500", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new();
        mockRouter.EnqueueHttpStatus(HttpStatusCode.InternalServerError);
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        bool isRunningAfterError = true;
        int logCount = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile());
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetStartCommand(vm));

            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out isRunningAfterError, "IsRunning", "IsBusy", "IsTranslating"));
            logCount = GetLogItems(vm).Count;
        });

        // Assert
        Assert.False(isRunningAfterError);
        Assert.True(logCount > 0);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_RestoreWithNoBackupManifest_LogsErrorWithoutCrash()
    {
        // Arrange
        using TempWorkspace ws = new("f22_restore_nobackup");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t2-5";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "No Backup", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        bool handledSafely = false;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile());
            ICommand restoreCmd = GetRestoreCommand(vm);
            if (!restoreCmd.CanExecute(null))
            {
                handledSafely = true;
            }
            else
            {
                await AdaptersBackupUiTestHelper.ExecuteCommandAsync(restoreCmd);
                handledSafely = GetLogItems(vm).Count > 0 ||
                    (AdaptersBackupUiTestHelper.TryGetProperty(vm, out string? err, "ErrorMessage", "StatusMessage") && !string.IsNullOrWhiteSpace(err));
            }
        });

        // Assert
        Assert.True(handledSafely);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F22")]
    public async Task PipelineViewModel_CancelledOrPausedZeroEntries_HandlesEmptyProjectSafely()
    {
        // Arrange
        using TempWorkspace ws = new("f22_zero_entries");
        ws.WriteTextFile("empty.json", "{}");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f22-t2-6";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Zero Entries", ws.GameRootPath, "Loose");
        object[] deps = CreateUiDependencies(bridge, db, ws, new MockLlmRouterHandler());

        double progressPct = double.NaN;
        bool isRunning = true;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigureProjectOnVm(vm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile());
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(GetStartCommand(vm));

            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out progressPct, "ProgressPercentage", "ProgressPercent", "Progress"));
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out isRunning, "IsRunning", "IsBusy", "IsTranslating"));
        });

        // Assert
        Assert.False(double.IsNaN(progressPct));
        Assert.False(double.IsInfinity(progressPct));
        Assert.False(isRunning);
    }

    private static object[] CreateUiDependencies(
        OpaqueContractBridge bridge,
        DatabaseSession db,
        TempWorkspace ws,
        MockLlmRouterHandler mockRouter)
    {
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        IReadOnlyList<IEngineAdapter> adapters = bridge.CreateAllEngineAdapters();
        PipelineOrchestratorHandle orchestrator = AdaptersBackupUiTestHelper.CreatePipelineOrchestratorSafe(
            bridge, db, sanitizer, translator, backupService, adapters, ws.BackupRootPath);
        SequentialWorkerHandle worker = AdaptersBackupUiTestHelper.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        return AdaptersBackupUiTestHelper.BuildFullDependencyGraph(
            db, sanitizer, translator, backupService, adapters, orchestrator, worker);
    }

    private static void ConfigureProjectOnVm(
        ViewModelHandle vm,
        string projectId,
        string gameRootPath,
        RouterProfile profile)
    {
        AdaptersBackupUiTestHelper.TrySetProperty(vm, projectId, "ProjectId", "CurrentProjectId");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, gameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "ProjectPath");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, profile, "ActiveRouterProfile", "RouterProfile", "Profile");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, profile.BaseUrl, "BaseUrl");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, profile.ApiKey, "ApiKey");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, profile.Model, "Model");
        AdaptersBackupUiTestHelper.TrySetProperty(vm, profile.BatchSize, "BatchSize");
    }

    private static ICommand GetStartCommand(ViewModelHandle vm) =>
        AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["StartTranslation", "StartPipeline", "Start"]);

    private static ICommand GetPauseCommand(ViewModelHandle vm) =>
        AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["PauseTranslation", "PausePipeline", "Pause"], excludedSubstrings: ["AutoPause"]);

    private static ICommand GetResumeCommand(ViewModelHandle vm) =>
        AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["ResumeTranslation", "ResumePipeline", "Resume"]);

    private static ICommand GetPatchGameCommand(ViewModelHandle vm) =>
        AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["PatchGame", "Patch"], excludedSubstrings: ["RePatch"]);

    private static ICommand GetRestoreCommand(ViewModelHandle vm) =>
        AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["RestoreOriginal", "RestoreBackup", "Restore"]);

    private static IReadOnlyList<object> GetLogItems(ViewModelHandle vm)
    {
        object? col = AdaptersBackupUiTestHelper.GetProperty<object>(vm, "LogMessages", "ActivityLogs", "Logs", "LogEntries");
        return AdaptersBackupUiTestHelper.ReadCollectionItems(col);
    }
}
