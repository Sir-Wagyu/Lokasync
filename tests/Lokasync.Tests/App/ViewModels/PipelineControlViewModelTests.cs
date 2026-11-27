using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lokasync.Adapters;
using Lokasync.App.Services;
using Lokasync.App.ViewModels;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Sanitizer;
using Lokasync.Tests.Adapters;
using Lokasync.Tests.App.TestDoubles;
using Lokasync.Tests.Translator.TestDoubles;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Providers;
using Xunit;

namespace Lokasync.Tests.App.ViewModels;

public sealed class PipelineControlViewModelTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _connectionFactory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteProjectRepository _projectRepository;
    private readonly SqliteSubtitleEntryRepository _entryRepository;
    private readonly SqliteBackupManifestRepository _manifestRepository;
    private readonly AdapterFixtureHelper _fixture = new();
    private readonly string _backupRoot;

    public PipelineControlViewModelTests()
    {
        _projectRepository = new SqliteProjectRepository(_connectionFactory);
        _entryRepository = new SqliteSubtitleEntryRepository(_connectionFactory);
        _manifestRepository = new SqliteBackupManifestRepository(_connectionFactory);
        _backupRoot = Path.Combine(Path.GetTempPath(), "Lokasync_PipelineVmBackups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupRoot);
    }

    public Task InitializeAsync() => _connectionFactory.InitializeSchemaAsync();

    public async Task DisposeAsync()
    {
        await _connectionFactory.DisposeAsync();
        _fixture.Dispose();
        if (Directory.Exists(_backupRoot))
        {
            Directory.Delete(_backupRoot, recursive: true);
        }
    }

    private (LocalizationPipelineService Pipeline, PipelineControlViewModel ViewModel) CreateVm(
        MockHttpMessageHandler handler,
        int batchSize = 25)
    {
        HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        BackupService backupService = new(_manifestRepository, _backupRoot);
        FakeDelayProvider delayProvider = new();

        LocalizationPipelineService pipeline = new(
            projectRepository: _projectRepository,
            entryRepository: _entryRepository,
            manifestRepository: _manifestRepository,
            backupService: backupService,
            sanitizer: new TextSanitizer(),
            translatorProvider: provider,
            connectionFactory: _connectionFactory,
            delayProvider: delayProvider);

        ProjectSessionState session = new()
        {
            ActiveRouterProfile = RouterProfileDefaults.CreateDefaultProfile() with
            {
                BatchSize = batchSize,
                DelayBetweenBatchesMs = 0,
                JitterMs = 0
            }
        };

        PipelineControlViewModel vm = new(
            pipelineService: pipeline,
            projectRepository: _projectRepository,
            subtitleEntryRepository: _entryRepository,
            session: session,
            dispatcher: new InlineDispatcherService(),
            notifications: new FakeUserNotificationService());

        return (pipeline, vm);
    }

    [Fact]
    public void CanExecute_InitialIdleState_StartEnabledPauseAndResumeDisabled()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"a\":\"Hello\"}");
        (_, PipelineControlViewModel vm) = CreateVm(new MockHttpMessageHandler());
        vm.ProjectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);
        vm.GameRootPath = _fixture.RootPath;

        // Act & Assert
        Assert.True(vm.StartTranslationCommand.CanExecute(null));
        Assert.False(vm.PauseTranslationCommand.CanExecute(null));
        Assert.False(vm.ResumeTranslationCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartTranslationCommand_ValidLooseProject_Reaches100PercentAndLogsProgress()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"a\":\"Hello\",\"b\":\"World\"}");
        MockHttpMessageHandler handler = new();
        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            Dictionary<string, string> reply = sent.ToDictionary(kv => kv.Key, kv => $"ID:{kv.Value}");
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = JsonSerializer.Serialize(reply) } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        (_, PipelineControlViewModel vm) = CreateVm(handler);
        vm.ProjectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);
        vm.GameRootPath = _fixture.RootPath;

        // Act
        await vm.StartTranslationCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsRunning);
        Assert.Equal(100.0, vm.ProgressPercentage);
        Assert.Equal(2, vm.CompletedCount);
        Assert.Equal(2, vm.TotalCount);
        Assert.NotEmpty(vm.ActivityLogs);
    }

    [Fact]
    public async Task StartTranslationCommand_EmptyProjectZeroEntries_ProgressIsZeroNotNaN()
    {
        // Arrange
        _fixture.WriteTextFile("empty.json", "{}");
        (_, PipelineControlViewModel vm) = CreateVm(new MockHttpMessageHandler());
        vm.ProjectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);
        vm.GameRootPath = _fixture.RootPath;

        // Act
        await vm.StartTranslationCommand.ExecuteAsync(null);

        // Assert
        Assert.False(double.IsNaN(vm.ProgressPercentage));
        Assert.False(double.IsInfinity(vm.ProgressPercentage));
        Assert.Equal(0.0, vm.ProgressPercentage);
    }

    [Fact]
    public async Task PauseAndResumeCommands_MidRunPause_CheckpointsAndResumesPendingOnly()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"a\":\"One\",\"b\":\"Two\"}");
        MockHttpMessageHandler handler = new();
        (_, PipelineControlViewModel vm) = CreateVm(handler, batchSize: 1);
        vm.ProjectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);
        vm.GameRootPath = _fixture.RootPath;

        handler.EnqueueHandler((_, body, _) =>
        {
            vm.PauseTranslationCommand.Execute(null);
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            string key = sent.Keys.Single();
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "Satu" }) } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });
        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            string key = sent.Keys.Single();
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "Dua" }) } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        // Act
        await vm.StartTranslationCommand.ExecuteAsync(null);
        Assert.True(vm.IsPaused);
        Assert.True(vm.ResumeTranslationCommand.CanExecute(null));
        Assert.Single(await _entryRepository.GetPendingAsync());

        await vm.ResumeTranslationCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsPaused);
        Assert.Equal(100.0, vm.ProgressPercentage);
        Assert.Empty(await _entryRepository.GetPendingAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task StartTranslationCommand_Http401Or429_TriggersAutoPauseBannerAndEnablesResume(HttpStatusCode statusCode)
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"a\":\"Hello\"}");
        MockHttpMessageHandler handler = new();
        handler.EnqueueResponse(statusCode, "{\"error\":\"auth or limit\"}");

        (_, PipelineControlViewModel vm) = CreateVm(handler);
        vm.ProjectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);
        vm.GameRootPath = _fixture.RootPath;

        // Act
        await vm.StartTranslationCommand.ExecuteAsync(null);

        // Assert
        Assert.True(vm.IsAutoPaused);
        Assert.False(string.IsNullOrWhiteSpace(vm.AutoPauseBanner));
        Assert.True(vm.ResumeTranslationCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartTranslationCommand_Http500Fatal_LogsErrorAndResetsIsRunningFalse()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"a\":\"Hello\"}");
        MockHttpMessageHandler handler = new();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "{\"error\":\"server crash\"}");

        (_, PipelineControlViewModel vm) = CreateVm(handler);
        vm.ProjectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);
        vm.GameRootPath = _fixture.RootPath;

        // Act
        await vm.StartTranslationCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsRunning);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains(vm.ActivityLogs, log => log.Contains("error", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PatchGameCommand_CreatesBackupManifestAndPatchesGameDisk()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"a\":\"Hello\"}");
        MockHttpMessageHandler handler = new();
        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            string key = sent.Keys.Single();
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "Halo" }) } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        (_, PipelineControlViewModel vm) = CreateVm(handler);
        vm.ProjectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);
        vm.GameRootPath = _fixture.RootPath;
        await vm.StartTranslationCommand.ExecuteAsync(null);

        // Act
        await vm.PatchGameCommand.ExecuteAsync(null);

        // Assert
        Assert.Null(vm.ErrorMessage);
        Assert.Contains("Halo", _fixture.ReadTextFile("dialog.json"), StringComparison.Ordinal);
        Assert.Single(await _manifestRepository.GetByProjectIdAsync(vm.ProjectId!));
    }

    [Fact]
    public async Task RestoreOriginalCommand_NoBackupManifest_LogsErrorWithoutThrowing()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"a\":\"Hello\"}");
        (_, PipelineControlViewModel vm) = CreateVm(new MockHttpMessageHandler());
        vm.ProjectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);
        vm.GameRootPath = _fixture.RootPath;

        // Act
        await vm.RestoreOriginalCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsRunning);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains(vm.ActivityLogs, log => log.Contains("Restore failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanStart_WhenSessionHasSupportedEngineForMatchingPath_SkipsDiskFingerprinterScan()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"a\":\"Hello\"}");
        CountingEngineFingerprinter countingFingerprinter = new();
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);

        LocalizationPipelineService pipeline = new(
            projectRepository: _projectRepository,
            entryRepository: _entryRepository,
            manifestRepository: _manifestRepository,
            backupService: new BackupService(_manifestRepository, _backupRoot),
            sanitizer: new TextSanitizer(),
            translatorProvider: new OpenAiTranslatorProvider(httpClient),
            fingerprinter: countingFingerprinter,
            connectionFactory: _connectionFactory,
            delayProvider: new FakeDelayProvider());

        ProjectSessionState session = new()
        {
            ProjectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath),
            GameRootPath = _fixture.RootPath,
            HasSupportedEngine = true
        };

        PipelineControlViewModel vm = new(
            pipelineService: pipeline,
            projectRepository: _projectRepository,
            subtitleEntryRepository: _entryRepository,
            fingerprinter: countingFingerprinter,
            session: session,
            dispatcher: new InlineDispatcherService(),
            notifications: new FakeUserNotificationService());

        // Act
        bool canStartFirst = vm.StartTranslationCommand.CanExecute(null);
        bool canStartSecond = vm.StartTranslationCommand.CanExecute(null);
        bool canStartThird = vm.StartTranslationCommand.CanExecute(null);

        // Assert
        Assert.True(canStartFirst);
        Assert.True(canStartSecond);
        Assert.True(canStartThird);
        Assert.Equal(0, countingFingerprinter.DetectAdapterCallCount);
    }

    [Fact]
    public void CanStart_WhenGameRootPathSetDirectlyWithoutDashboard_CachesResultUntilPathChanges()
    {
        // Arrange
        string supportedDir = _fixture.CreateSubDir("Supported");
        File.WriteAllText(Path.Combine(supportedDir, "dialog.json"), "{\"a\":\"Hello\"}");
        string emptyDir = _fixture.CreateSubDir("Empty");

        CountingEngineFingerprinter countingFingerprinter = new();
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);

        LocalizationPipelineService pipeline = new(
            projectRepository: _projectRepository,
            entryRepository: _entryRepository,
            manifestRepository: _manifestRepository,
            backupService: new BackupService(_manifestRepository, _backupRoot),
            sanitizer: new TextSanitizer(),
            translatorProvider: new OpenAiTranslatorProvider(httpClient),
            fingerprinter: countingFingerprinter,
            connectionFactory: _connectionFactory,
            delayProvider: new FakeDelayProvider());

        PipelineControlViewModel vm = new(
            pipelineService: pipeline,
            projectRepository: _projectRepository,
            subtitleEntryRepository: _entryRepository,
            fingerprinter: countingFingerprinter,
            session: new ProjectSessionState(),
            dispatcher: new InlineDispatcherService(),
            notifications: new FakeUserNotificationService());

        // Act — first path evaluated multiple times
        vm.ProjectId = SqliteConnectionFactory.ComputeProjectHash(supportedDir);
        vm.GameRootPath = supportedDir;

        Assert.True(vm.StartTranslationCommand.CanExecute(null));
        Assert.True(vm.StartTranslationCommand.CanExecute(null));
        Assert.True(vm.StartTranslationCommand.CanExecute(null));
        Assert.Equal(1, countingFingerprinter.DetectAdapterCallCount);

        // Act — path changed invalidates cache and scans once for new path
        vm.GameRootPath = emptyDir;

        Assert.False(vm.StartTranslationCommand.CanExecute(null));
        Assert.False(vm.StartTranslationCommand.CanExecute(null));

        // Assert
        Assert.Equal(2, countingFingerprinter.DetectAdapterCallCount);
    }

    private sealed class CountingEngineFingerprinter : IEngineFingerprinter
    {
        private readonly EngineFingerprinter _inner = new();

        public int DetectAdapterCallCount { get; private set; }

        public IReadOnlyList<IEngineAdapter> Adapters => _inner.Adapters;

        public IEngineAdapter? DetectAdapter(string? gameRootPath)
        {
            DetectAdapterCallCount++;
            return _inner.DetectAdapter(gameRootPath);
        }

        public string? DetectEngineName(string? gameRootPath) => DetectAdapter(gameRootPath)?.EngineName;

        public bool TryGetAdapterByName(string? engineName, [NotNullWhen(true)] out IEngineAdapter? adapter) =>
            _inner.TryGetAdapterByName(engineName, out adapter);
    }
}
