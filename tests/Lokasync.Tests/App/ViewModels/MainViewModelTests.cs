using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lokasync.Adapters;
using Lokasync.App.Services;
using Lokasync.App.ViewModels;
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

public sealed class MainViewModelTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _connectionFactory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteProjectRepository _projectRepository;
    private readonly SqliteSubtitleEntryRepository _entryRepository;
    private readonly SqliteBackupManifestRepository _manifestRepository;
    private readonly AdapterFixtureHelper _fixture = new();
    private readonly string _tempRoot;
    private readonly string _backupRoot;
    private readonly string _profilesPath;

    public MainViewModelTests()
    {
        _projectRepository = new SqliteProjectRepository(_connectionFactory);
        _entryRepository = new SqliteSubtitleEntryRepository(_connectionFactory);
        _manifestRepository = new SqliteBackupManifestRepository(_connectionFactory);
        _tempRoot = Path.Combine(Path.GetTempPath(), "Lokasync_MainVmTests", Guid.NewGuid().ToString("N"));
        _backupRoot = Path.Combine(_tempRoot, "backups");
        _profilesPath = Path.Combine(_tempRoot, "profiles.json");
        Directory.CreateDirectory(_backupRoot);
    }

    public Task InitializeAsync() => _connectionFactory.InitializeSchemaAsync();

    public async Task DisposeAsync()
    {
        await _connectionFactory.DisposeAsync();
        _fixture.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private (MainViewModel MainVm, MockHttpMessageHandler Handler) CreateMainViewModel()
    {
        ProjectSessionState session = new();
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        TextSanitizer sanitizer = new();
        EngineFingerprinter fingerprinter = new();
        RouterProfileStore profileStore = new(_profilesPath);
        BackupService backupService = new(_manifestRepository, _backupRoot);
        InlineDispatcherService dispatcher = new();
        FakeUserNotificationService notifications = new();
        FakeFolderPickerService folderPicker = new();

        LocalizationPipelineService pipelineService = new(
            projectRepository: _projectRepository,
            entryRepository: _entryRepository,
            manifestRepository: _manifestRepository,
            backupService: backupService,
            sanitizer: sanitizer,
            translatorProvider: provider,
            fingerprinter: fingerprinter,
            profileStore: profileStore,
            connectionFactory: _connectionFactory,
            delayProvider: new FakeDelayProvider());

        PipelineControlViewModel pipelineVm = new(
            pipelineService: pipelineService,
            projectRepository: _projectRepository,
            subtitleEntryRepository: _entryRepository,
            fingerprinter: fingerprinter,
            session: session,
            dispatcher: dispatcher,
            notifications: notifications);

        ProjectDashboardViewModel dashboardVm = new(
            projectRepository: _projectRepository,
            fingerprinter: fingerprinter,
            session: session,
            folderPicker: folderPicker,
            notifications: notifications,
            pipeline: pipelineVm);

        RouterProfileViewModel routerProfileVm = new(
            profileStore: profileStore,
            session: session,
            notifications: notifications);

        GlossaryViewModel glossaryVm = new(
            projectRepository: _projectRepository,
            session: session);

        TranslationGridViewModel translationGridVm = new(
            entryRepository: _entryRepository,
            sanitizer: sanitizer,
            pipelineService: pipelineService,
            session: session,
            dispatcher: dispatcher);

        MainViewModel mainVm = new(
            dashboard: dashboardVm,
            routerProfile: routerProfileVm,
            glossary: glossaryVm,
            pipeline: pipelineVm,
            translationGrid: translationGridVm,
            session: session);

        return (mainVm, handler);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for async MainViewModel propagation.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task InitializeAsync_WithSavedProfilesJson_LoadsRouterProfileIntoViewModelAndSession()
    {
        // Arrange
        RouterProfileStore store = new(_profilesPath);
        RouterProfile custom = RouterProfileDefaults.CreateDefaultProfile() with
        {
            BaseUrl = "http://127.0.0.1:9999/v1/chat/completions",
            Model = "custom-local-model",
            BatchSize = 12
        };
        await store.SaveAsync("Custom", new Dictionary<string, RouterProfile> { ["Custom"] = custom });

        (MainViewModel mainVm, _) = CreateMainViewModel();

        // Act
        await mainVm.InitializeAsync();

        // Assert
        Assert.Equal("http://127.0.0.1:9999/v1/chat/completions", mainVm.RouterProfile.BaseUrl);
        Assert.Equal("custom-local-model", mainVm.RouterProfile.Model);
        Assert.Equal(12, mainVm.RouterProfile.BatchSize);
        Assert.Equal("http://127.0.0.1:9999/v1/chat/completions", mainVm.Session.ActiveRouterProfile.BaseUrl);
    }

    [Fact]
    public async Task OnSessionPropertyChanged_WhenProjectIdAndGameRootPathChange_PropagatesToGlossaryAndTranslationGrid()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"k1\":\"Hello\",\"k2\":\"World\"}");
        string projectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);

        NarrativeContext narrative = new(
            "My Narrative Game",
            "Cozy mystery",
            [new GlossaryTerm("Hero", "Pahlawan", "Main protagonist")]);

        await _projectRepository.UpsertAsync(new ProjectRecord(
            Id: projectId,
            GameTitle: "My Narrative Game",
            GameRootPath: _fixture.RootPath,
            DetectedEngine: "LooseFile",
            NarrativeContextJson: JsonSerializer.Serialize(narrative),
            CreatedAt: DateTimeOffset.UtcNow));

        await _entryRepository.BulkUpsertAsync([
            new SubtitleEntry(
                0, "dialog.json::/k1", "dialog.json", "/k1", "Hello", "Hello",
                new Dictionary<string, string>(), null, null, EntryStatus.Pending, false),
            new SubtitleEntry(
                0, "dialog.json::/k2", "dialog.json", "/k2", "World", "World",
                new Dictionary<string, string>(), null, null, EntryStatus.Pending, false)
        ]);

        (MainViewModel mainVm, _) = CreateMainViewModel();

        // Act
        mainVm.Session.GameRootPath = _fixture.RootPath;
        mainVm.Session.ProjectId = projectId;

        await WaitUntilAsync(() =>
            mainVm.Glossary.GlossaryTerms.Count == 1 &&
            mainVm.TranslationGrid.AllEntries.Count == 2);

        // Assert
        Assert.Equal(projectId, mainVm.Glossary.ProjectId);
        Assert.Equal("Cozy mystery", mainVm.Glossary.GenreTone);
        GlossaryTermViewModel term = Assert.Single(mainVm.Glossary.GlossaryTerms);
        Assert.Equal("Hero", term.Source);

        Assert.Equal(projectId, mainVm.TranslationGrid.ProjectId);
        Assert.Equal(_fixture.RootPath, mainVm.TranslationGrid.GameRootPath);
        Assert.Equal(2, mainVm.TranslationGrid.AllEntries.Count);
        Assert.Equal(2, mainVm.TranslationGrid.FilteredEntries.Count);
    }

    [Fact]
    public async Task OnPipelineWorkCompleted_AfterTranslationRun_RefreshesTranslationGridEntries()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"greet\":\"Hello\"}");
        (MainViewModel mainVm, MockHttpMessageHandler handler) = CreateMainViewModel();
        mainVm.RouterProfile.DelayBetweenBatchesMs = 0;
        mainVm.RouterProfile.JitterMs = 0;

        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            string key = sent.Keys.Single();
            string payload = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "Halo" });
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = payload } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        await mainVm.Dashboard.LoadProjectCommand.ExecuteAsync(_fixture.RootPath);
        await WaitUntilAsync(() => mainVm.TranslationGrid.ProjectId == mainVm.Dashboard.ProjectId);

        // Act
        await mainVm.Pipeline.StartTranslationCommand.ExecuteAsync(null);
        await WaitUntilAsync(() =>
            mainVm.TranslationGrid.AllEntries.Any(e => e.Status == EntryStatus.Translated));

        // Assert
        SubtitleEntryRowViewModel row = Assert.Single(mainVm.TranslationGrid.AllEntries);
        Assert.Equal(EntryStatus.Translated, row.Status);
        Assert.Equal("Halo", row.FinalPatchedText);
    }
}
