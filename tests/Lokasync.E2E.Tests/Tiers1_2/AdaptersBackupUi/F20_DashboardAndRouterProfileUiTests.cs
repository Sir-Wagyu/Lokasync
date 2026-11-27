using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F20_DashboardAndRouterProfileUiTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public async Task DashboardViewModel_SelectValidGameFolder_AutoDetectsAndDisplaysEngine()
    {
        // Arrange
        using TempWorkspace ws = new("f20_detect_ui");
        SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        string? detectedEngine = null;
        string? errorMessage = null;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Dashboard", "Project", "Main"],
                deps);

            AdaptersBackupUiTestHelper.SetProperty(vm, ws.GameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "FolderPath", "ProjectPath");
            await TriggerDetectIfAvailableAsync(vm, ws.GameRootPath);

            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out detectedEngine, "DetectedEngine", "DetectedEngineName", "EngineName"));
            AdaptersBackupUiTestHelper.TryGetProperty(vm, out errorMessage, "ErrorMessage", "ValidationError", "StatusError");
        });

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(detectedEngine));
        Assert.Contains("Rpg", detectedEngine, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public async Task DashboardViewModel_DefaultRouterProfile_HasExpectedDefaultLocalEndpoint()
    {
        // Arrange
        using TempWorkspace ws = new("f20_default_profile");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        string? baseUrl = null;
        int maxConcurrency = 0;
        int batchSize = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaAsync(() =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Profile", "Router", "Dashboard", "Main"],
                deps);

            if (AdaptersBackupUiTestHelper.TryGetProperty(vm, out RouterProfile? profile, "ActiveRouterProfile", "CurrentProfile", "Profile", "SelectedProfile") &&
                profile is not null)
            {
                baseUrl = profile.BaseUrl;
                maxConcurrency = profile.MaxConcurrency;
                batchSize = profile.BatchSize;
            }
            else
            {
                Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out baseUrl, "BaseUrl", "EndpointUrl", "RouterBaseUrl"));
                Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out maxConcurrency, "MaxConcurrency"));
                Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out batchSize, "BatchSize"));
            }
        });

        // Assert
        Assert.Equal("http://127.0.0.1:8045/v1/chat/completions", baseUrl);
        Assert.Equal(1, maxConcurrency);
        Assert.True(batchSize > 0);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public async Task ProfileConfig_SaveAndReloadProfilesJson_PersistsAllRouterProfileFields()
    {
        // Arrange
        using TempWorkspace ws = new("f20_save_reload_profiles");
        OpaqueContractBridge bridge = new();
        RouterProfile customProfile = new(
            BaseUrl: "http://127.0.0.1:9090/v1/chat/completions",
            ApiKey: "sk-custom-key-99",
            Model: "qwen2.5-14b-instruct",
            MaxConcurrency: 1,
            DelayBetweenBatchesMs: 1500,
            JitterMs: 200,
            BatchSize: 10);

        Dictionary<string, RouterProfile> map = new(StringComparer.Ordinal)
        {
            ["local_router"] = customProfile
        };

        // Act
        await AdaptersBackupUiTestHelper.SaveProfilesSafeAsync(bridge, ws.ProfilesJsonPath, "local_router", map);
        (string activeProfile, IReadOnlyDictionary<string, RouterProfile> loaded) =
            await bridge.LoadProfilesAsync(ws.ProfilesJsonPath);

        // Assert
        Assert.True(File.Exists(ws.ProfilesJsonPath));
        Assert.Equal("local_router", activeProfile);
        Assert.True(loaded.TryGetValue("local_router", out RouterProfile? reloaded));
        Assert.Equal(customProfile, reloaded);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ws.ProfilesJsonPath));
        Assert.Equal("local_router", doc.RootElement.GetProperty("active_profile").GetString());
        JsonElement savedNode = doc.RootElement.GetProperty("profiles").GetProperty("local_router");
        Assert.Equal(customProfile.BaseUrl, savedNode.GetProperty("base_url").GetString());
        Assert.Equal(customProfile.DelayBetweenBatchesMs, savedNode.GetProperty("delay_between_batches_ms").GetInt32());
        Assert.Equal(customProfile.JitterMs, savedNode.GetProperty("jitter_ms").GetInt32());
        Assert.Equal(customProfile.BatchSize, savedNode.GetProperty("batch_size").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public async Task ProfileConfig_SwitchActiveProfile_UpdatesBoundFieldsToSelectedProfile()
    {
        // Arrange
        using TempWorkspace ws = new("f20_switch_profile");
        OpaqueContractBridge bridge = new();
        RouterProfile p1 = MockLlmRouterHandler.CreateProfile(
            baseUrl: "http://127.0.0.1:8045/v1/chat/completions",
            model: "gemini-pro",
            batchSize: 25);
        RouterProfile p2 = MockLlmRouterHandler.CreateProfile(
            baseUrl: "http://127.0.0.1:11434/v1/chat/completions",
            apiKey: "ollama",
            model: "llama3.1:8b",
            batchSize: 12);

        await AdaptersBackupUiTestHelper.SaveProfilesSafeAsync(bridge, ws.ProfilesJsonPath, "local_router", new Dictionary<string, RouterProfile>
        {
            ["local_router"] = p1,
            ["ollama_local"] = p2
        });

        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        string? boundUrl = null;
        string? boundModel = null;
        int boundBatchSize = 0;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Profile", "Router", "Dashboard", "Main"],
                deps);

            AdaptersBackupUiTestHelper.TrySetProperty(vm, ws.ProfilesJsonPath, "ProfilesJsonPath", "ProfilesPath", "ConfigPath");
            (bool loadedViaMethod, _) = await AdaptersBackupUiTestHelper.TryInvokeViewModelMethodAsync(
                vm, ["LoadProfile", "LoadConfig"], ws.ProfilesJsonPath);
            if (!loadedViaMethod)
            {
                ICommand loadCmd = AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["LoadProfiles", "ReloadProfiles"]);
                await AdaptersBackupUiTestHelper.ExecuteCommandAsync(loadCmd, ws.ProfilesJsonPath);
            }

            AdaptersBackupUiTestHelper.SetProperty(vm, "ollama_local", "ActiveProfile", "SelectedProfileName", "ActiveProfileName");
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out boundUrl, "BaseUrl", "EndpointUrl", "RouterBaseUrl"));
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out boundModel, "Model", "ModelName"));
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out boundBatchSize, "BatchSize"));
        });

        // Assert
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", boundUrl);
        Assert.Equal("llama3.1:8b", boundModel);
        Assert.Equal(12, boundBatchSize);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public async Task DashboardViewModel_ChangeGameFolderBetweenEngines_UpdatesDetectedEngineDynamically()
    {
        // Arrange
        using TempWorkspace wsLoose = new("f20_switch_a");
        using TempWorkspace wsUnity = new("f20_switch_b");
        SyntheticGameBuilder.CreateLooseFileGame(wsLoose.GameRootPath);
        SyntheticGameBuilder.CreateUnityGame(wsUnity.GameRootPath);

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(wsLoose.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, wsLoose, mockRouter);

        string? firstEngine = null;
        string? secondEngine = null;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Dashboard", "Project", "Main"],
                deps);

            AdaptersBackupUiTestHelper.SetProperty(vm, wsLoose.GameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "FolderPath", "ProjectPath");
            await TriggerDetectIfAvailableAsync(vm, wsLoose.GameRootPath);
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out firstEngine, "DetectedEngine", "DetectedEngineName", "EngineName"));

            AdaptersBackupUiTestHelper.SetProperty(vm, wsUnity.GameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "FolderPath", "ProjectPath");
            await TriggerDetectIfAvailableAsync(vm, wsUnity.GameRootPath);
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out secondEngine, "DetectedEngine", "DetectedEngineName", "EngineName"));
        });

        // Assert
        Assert.Contains("Loose", firstEngine ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unity", secondEngine ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public async Task DashboardViewModel_LoadExistingProjectFolder_InitializesOrReopensProjectMetadata()
    {
        // Arrange
        using TempWorkspace ws = new("f20_open_project");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Dashboard", "Project", "Main"],
                deps);

            AdaptersBackupUiTestHelper.SetProperty(vm, ws.GameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "FolderPath", "ProjectPath");
            await TriggerDetectIfAvailableAsync(vm, ws.GameRootPath);
        });

        // Assert
        await using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT detected_engine FROM projects WHERE game_root_path = @path LIMIT 1;";
        cmd.Parameters.AddWithValue("@path", ws.GameRootPath);
        string? dbEngine = Convert.ToString(await cmd.ExecuteScalarAsync());
        Assert.False(string.IsNullOrWhiteSpace(dbEngine));
        Assert.Contains("Loose", dbEngine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public async Task DashboardViewModel_NonExistentGameFolder_SetsValidationErrorAndNoEngine()
    {
        // Arrange
        using TempWorkspace ws = new("f20_missing_folder");
        string missingPath = Path.Combine(ws.RootPath, "NonExistentGameFolder");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        string? detectedEngine = null;
        string? errorMessage = null;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Dashboard", "Project", "Main"],
                deps);

            AdaptersBackupUiTestHelper.SetProperty(vm, missingPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "FolderPath", "ProjectPath");
            try
            {
                await TriggerDetectIfAvailableAsync(vm, missingPath);
            }
            catch (DirectoryNotFoundException)
            {
                // Acceptable if surface error is also populated
            }

            AdaptersBackupUiTestHelper.TryGetProperty(vm, out detectedEngine, "DetectedEngine", "DetectedEngineName", "EngineName");
            AdaptersBackupUiTestHelper.TryGetProperty(vm, out errorMessage, "ErrorMessage", "ValidationError", "StatusMessage", "StatusError");
        });

        // Assert
        Assert.True(
            string.IsNullOrWhiteSpace(detectedEngine) ||
            detectedEngine.Contains("None", StringComparison.OrdinalIgnoreCase) ||
            detectedEngine.Contains("Unknown", StringComparison.OrdinalIgnoreCase),
            $"Expected empty/None/Unknown engine for missing folder, got '{detectedEngine}'.");
        Assert.False(string.IsNullOrWhiteSpace(errorMessage));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public async Task ProfileConfig_InvalidBaseUrl_RejectsSaveOrFlagsValidationError()
    {
        // Arrange
        using TempWorkspace ws = new("f20_invalid_url");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        bool saveRejectedOrErrored = false;
        string? errorMessage = null;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Profile", "Router", "Dashboard", "Main"],
                deps);

            AdaptersBackupUiTestHelper.TrySetProperty(vm, ws.ProfilesJsonPath, "ProfilesJsonPath", "ProfilesPath", "ProfilesFilePath", "ConfigPath");
            AdaptersBackupUiTestHelper.SetProperty(vm, "not-a-valid-url", "BaseUrl", "EndpointUrl", "RouterBaseUrl");

            ICommand saveCmd = AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["SaveProfile", "SaveConfig"]);
            if (!saveCmd.CanExecute(null))
            {
                saveRejectedOrErrored = true;
            }
            else
            {
                try
                {
                    await AdaptersBackupUiTestHelper.ExecuteCommandAsync(saveCmd);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
                {
                    saveRejectedOrErrored = true;
                }
            }

            AdaptersBackupUiTestHelper.TryGetProperty(vm, out errorMessage, "ErrorMessage", "ValidationError", "ProfileError", "StatusMessage");
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                saveRejectedOrErrored = true;
            }
        });

        // Assert
        Assert.True(saveRejectedOrErrored, "Expected invalid BaseUrl to fail validation or block Save.");
        Assert.False(File.Exists(ws.ProfilesJsonPath) && File.ReadAllText(ws.ProfilesJsonPath).Contains("not-a-valid-url", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public async Task ProfileConfig_NonPositiveBatchSizeOrNegativeDelay_RejectsInvalidNumericValues()
    {
        // Arrange
        using TempWorkspace ws = new("f20_invalid_numbers");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        Dictionary<string, bool> rejectedByProperty = new(StringComparer.Ordinal);

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Profile", "Router", "Dashboard", "Main"],
                deps);

            Assert.True(
                AdaptersBackupUiTestHelper.TrySetProperty(vm, ws.ProfilesJsonPath, "ProfilesJsonPath", "ProfilesPath", "ProfilesFilePath", "ConfigPath"),
                "Expected ViewModel to expose a writable ProfilesJsonPath/ProfilesPath/ConfigPath property.");

            (string PropName, int InvalidValue)[] invalidCases =
            [
                ("BatchSize", 0),
                ("DelayBetweenBatchesMs", -100),
                ("JitterMs", -5)
            ];

            ICommand saveCmd = AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["SaveProfile", "SaveConfig"]);

            foreach ((string propName, int invalidValue) in invalidCases)
            {
                // Reset all fields to valid defaults before testing each invalid numeric property individually
                AdaptersBackupUiTestHelper.TrySetProperty(vm, "http://127.0.0.1:8045/v1/chat/completions", "BaseUrl", "EndpointUrl", "RouterBaseUrl");
                AdaptersBackupUiTestHelper.TrySetProperty(vm, "gemini-pro", "Model", "ModelName");
                Assert.True(AdaptersBackupUiTestHelper.TrySetProperty(vm, 25, "BatchSize"));
                Assert.True(AdaptersBackupUiTestHelper.TrySetProperty(vm, 0, "DelayBetweenBatchesMs"));
                Assert.True(AdaptersBackupUiTestHelper.TrySetProperty(vm, 0, "JitterMs"));
                AdaptersBackupUiTestHelper.TrySetProperty(vm, string.Empty, "ErrorMessage", "ValidationError", "ProfileError");

                Assert.True(
                    AdaptersBackupUiTestHelper.TrySetProperty(vm, invalidValue, propName),
                    $"Expected writable '{propName}' property on profile ViewModel.");

                bool caseRejected = false;
                if (!saveCmd.CanExecute(null))
                {
                    caseRejected = true;
                }
                else
                {
                    try
                    {
                        await AdaptersBackupUiTestHelper.ExecuteCommandAsync(saveCmd);
                    }
                    catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
                    {
                        caseRejected = true;
                    }
                }

                if (AdaptersBackupUiTestHelper.TryGetProperty(vm, out string? err, "ErrorMessage", "ValidationError", "ProfileError") &&
                    !string.IsNullOrWhiteSpace(err))
                {
                    caseRejected = true;
                }

                rejectedByProperty[propName] = caseRejected;
            }
        });

        // Assert
        Assert.True(rejectedByProperty.GetValueOrDefault("BatchSize"), "Expected BatchSize=0 alone to be rejected by profile validation.");
        Assert.True(rejectedByProperty.GetValueOrDefault("DelayBetweenBatchesMs"), "Expected DelayBetweenBatchesMs=-100 alone to be rejected by profile validation.");
        Assert.True(rejectedByProperty.GetValueOrDefault("JitterMs"), "Expected JitterMs=-5 alone to be rejected by profile validation.");
        if (File.Exists(ws.ProfilesJsonPath))
        {
            (_, IReadOnlyDictionary<string, RouterProfile> loaded) = await bridge.LoadProfilesAsync(ws.ProfilesJsonPath);
            Assert.All(loaded.Values, p =>
            {
                Assert.True(p.BatchSize > 0);
                Assert.True(p.DelayBetweenBatchesMs >= 0);
                Assert.True(p.JitterMs >= 0);
            });
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public async Task ProfileConfig_EmptyModelName_RejectsSaveWithValidationMessage()
    {
        // Arrange
        using TempWorkspace ws = new("f20_empty_model");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        bool rejected = false;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Profile", "Router", "Dashboard", "Main"],
                deps);

            AdaptersBackupUiTestHelper.TrySetProperty(vm, ws.ProfilesJsonPath, "ProfilesJsonPath", "ProfilesPath", "ConfigPath");
            AdaptersBackupUiTestHelper.SetProperty(vm, "   ", "Model", "ModelName");

            ICommand saveCmd = AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["SaveProfile", "SaveConfig"]);
            if (!saveCmd.CanExecute(null))
            {
                rejected = true;
            }
            else
            {
                try
                {
                    await AdaptersBackupUiTestHelper.ExecuteCommandAsync(saveCmd);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    rejected = true;
                }
            }

            if (AdaptersBackupUiTestHelper.TryGetProperty(vm, out string? err, "ErrorMessage", "ValidationError", "ProfileError") &&
                !string.IsNullOrWhiteSpace(err))
            {
                rejected = true;
            }
        });

        // Assert
        Assert.True(rejected, "Expected blank Model name to fail validation.");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public async Task ProfileConfig_CorruptProfilesJsonFile_FallsBackSafelyWithoutCrash()
    {
        // Arrange
        using TempWorkspace ws = new("f20_corrupt_profiles");
        File.WriteAllText(ws.ProfilesJsonPath, "{ corrupt json");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        const string sentinelBaseUrl = "http://sentinel.invalid/v1/chat/completions";
        string? baseUrl = null;
        string? errorMsg = null;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Profile", "Router", "Dashboard", "Main"],
                deps);

            AdaptersBackupUiTestHelper.TrySetProperty(vm, ws.ProfilesJsonPath, "ProfilesJsonPath", "ProfilesPath", "ProfilesFilePath", "ConfigPath");
            AdaptersBackupUiTestHelper.SetProperty(vm, sentinelBaseUrl, "BaseUrl", "EndpointUrl", "RouterBaseUrl");

            (bool loadedViaMethod, _) = await AdaptersBackupUiTestHelper.TryInvokeViewModelMethodAsync(
                vm, ["LoadProfile", "LoadConfig"], ws.ProfilesJsonPath);
            if (!loadedViaMethod)
            {
                ICommand loadCmd = AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["LoadProfiles", "ReloadProfiles"]);
                await AdaptersBackupUiTestHelper.ExecuteCommandAsync(loadCmd, ws.ProfilesJsonPath);
            }

            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out baseUrl, "BaseUrl", "EndpointUrl", "RouterBaseUrl"));
            AdaptersBackupUiTestHelper.TryGetProperty(vm, out errorMsg, "ErrorMessage", "ValidationError", "StatusMessage");
        });

        // Assert
        Assert.True(
            !string.IsNullOrWhiteSpace(errorMsg) ||
            string.Equals(baseUrl, "http://127.0.0.1:8045/v1/chat/completions", StringComparison.Ordinal),
            $"Expected corrupt profiles.json load to surface a non-empty error or reset BaseUrl from sentinel to default, got errorMsg='{errorMsg}', baseUrl='{baseUrl}'.");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public async Task DashboardViewModel_EmptyFolderWithNoSupportedEngine_ReportsUnsupportedEngine()
    {
        // Arrange
        using TempWorkspace ws = new("f20_empty_folder");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        MockLlmRouterHandler mockRouter = new();
        object[] deps = CreateUiDependencies(bridge, db, ws, mockRouter);

        string? detectedEngine = null;
        bool startCanExecute = true;

        // Act
        await AdaptersBackupUiTestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle vm = AdaptersBackupUiTestHelper.ResolveViewModel(
                bridge,
                ["Dashboard", "Project", "Main"],
                deps);

            AdaptersBackupUiTestHelper.SetProperty(vm, ws.GameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "FolderPath", "ProjectPath");
            await TriggerDetectIfAvailableAsync(vm, ws.GameRootPath);
            Assert.True(AdaptersBackupUiTestHelper.TryGetProperty(vm, out detectedEngine, "DetectedEngine", "DetectedEngineName", "EngineName"));

            ICommand startCmd = AdaptersBackupUiTestHelper.GetCommandSafe(vm, ["StartTranslation", "StartPipeline", "Start"]);
            startCanExecute = startCmd.CanExecute(null);
        });

        // Assert
        Assert.True(
            string.IsNullOrWhiteSpace(detectedEngine) ||
            detectedEngine.Contains("None", StringComparison.OrdinalIgnoreCase) ||
            detectedEngine.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            detectedEngine.Contains("Unsupported", StringComparison.OrdinalIgnoreCase),
            $"Expected unsupported/none engine for empty directory, got '{detectedEngine}'.");
        Assert.False(startCanExecute);
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

    private static async Task TriggerDetectIfAvailableAsync(ViewModelHandle vm, string gameRootPath)
    {
        if (AdaptersBackupUiTestHelper.TryGetCommand(
                vm,
                ["LoadProject", "OpenProject", "SelectFolder", "DetectEngine", "Detect"],
                out ICommand? detectCmd) &&
            detectCmd is not null)
        {
            await AdaptersBackupUiTestHelper.ExecuteCommandAsync(detectCmd, gameRootPath);
            return;
        }

        await AdaptersBackupUiTestHelper.TryInvokeViewModelMethodAsync(
            vm,
            ["LoadProject", "OpenProject", "DetectEngine", "SelectGameFolder"],
            gameRootPath);
    }
}
