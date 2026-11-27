using System.IO;
using System.Net.Http;
using Lokasync.Adapters;
using Lokasync.App.Services;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Sanitizer;
using Lokasync.Tests.Adapters;
using Lokasync.Tests.Translator.TestDoubles;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Providers;
using Xunit;

namespace Lokasync.Tests.App;

public sealed class DesktopProjectContextTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dbRoot;
    private readonly string _backupRoot;
    private readonly string _profilesPath;
    private readonly AdapterFixtureHelper _fixtureA = new();
    private readonly AdapterFixtureHelper _fixtureB = new();

    public DesktopProjectContextTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Lokasync_DesktopCtxTests", Guid.NewGuid().ToString("N"));
        _dbRoot = Path.Combine(_tempRoot, "dbs");
        _backupRoot = Path.Combine(_tempRoot, "backups");
        _profilesPath = Path.Combine(_tempRoot, "profiles.json");
        Directory.CreateDirectory(_dbRoot);
        Directory.CreateDirectory(_backupRoot);
    }

    public void Dispose()
    {
        _fixtureA.Dispose();
        _fixtureB.Dispose();

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private ISqliteConnectionFactory CreateIsolatedFactory(string gameRoot)
    {
        string dbPath = Path.Combine(_dbRoot, $"{SqliteConnectionFactory.ComputeProjectHash(gameRoot)}.db");
        return SqliteConnectionFactory.ForFile(dbPath, pooling: false);
    }

    private static ProjectRecord CreateProjectRecord(string gameRoot, string title, string engine = "LooseFile") =>
        new(
            Id: SqliteConnectionFactory.ComputeProjectHash(gameRoot),
            GameTitle: title,
            GameRootPath: Path.GetFullPath(gameRoot),
            DetectedEngine: engine,
            NarrativeContextJson: null,
            CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task OpenAsync_SameProjectOpenedTwiceAndHandlesDisposed_DoesNotDisposeSharedConnectionFactory()
    {
        // Arrange
        _fixtureA.CreateLooseFixture();
        await using DesktopProjectContext context = new(CreateIsolatedFactory, _backupRoot);
        ProjectRecord project = CreateProjectRecord(_fixtureA.RootPath, "Game A");

        // Act
        ProjectSession s1 = await context.OpenAsync(_fixtureA.RootPath);
        await s1.Projects.UpsertAsync(project);

        ProjectSession s2 = await context.OpenAsync(_fixtureA.RootPath);
        await s1.DisposeAsync();
        await s2.DisposeAsync();

        // Assert
        IReadOnlyList<ProjectRecord> projects = await context.ListAsync();
        ProjectRecord loaded = Assert.Single(projects);
        Assert.Equal(project.Id, loaded.Id);
        Assert.Empty(await context.GetAllAsync());
    }

    [Fact]
    public async Task LocalizationPipelineService_DisposeAsyncWithDesktopProjectContext_PreservesSharedDesktopContext()
    {
        // Arrange
        _fixtureA.WriteTextFile("dialog.json", "{\"line\":\"Hello\"}");
        await using DesktopProjectContext context = new(CreateIsolatedFactory, _backupRoot);
        using HttpClient httpClient = new(new MockHttpMessageHandler());

        LocalizationPipelineService pipeline = new(
            fingerprinter: new EngineFingerprinter(),
            sanitizer: new TextSanitizer(),
            translatorProvider: new OpenAiTranslatorProvider(httpClient),
            profileStore: new RouterProfileStore(_profilesPath),
            sessionFactory: context);

        // Act
        PipelineProjectState opened = await pipeline.OpenProjectAsync(_fixtureA.RootPath);
        await pipeline.ExtractAndPreparePendingAsync();
        await pipeline.DisposeAsync();

        // Assert
        IReadOnlyList<ProjectRecord> projects = await context.ListAsync();
        ProjectRecord storedProject = Assert.Single(projects);
        Assert.Equal(opened.Project.Id, storedProject.Id);

        IReadOnlyList<SubtitleEntry> entries = await context.GetAllAsync();
        Assert.Single(entries);

        ProjectRecord? byRoot = await context.GetByGameRootPathAsync(_fixtureA.RootPath);
        Assert.NotNull(byRoot);
        Assert.Equal(opened.Project.Id, byRoot.Id);
    }

    [Fact]
    public async Task OpenAsync_SwitchingBetweenTwoProjectRoots_DisposesOldConnectionFactoryAndIsolatesData()
    {
        // Arrange
        _fixtureA.CreateLooseFixture();
        _fixtureB.CreateRpgMakerFixture();
        List<TrackingConnectionFactory> createdFactories = [];

        ISqliteConnectionFactory FactoryProvider(string root)
        {
            TrackingConnectionFactory tracked = new(CreateIsolatedFactory(root));
            createdFactories.Add(tracked);
            return tracked;
        }

        await using DesktopProjectContext context = new(FactoryProvider, _backupRoot);

        // Act
        ProjectSession sessionA = await context.OpenAsync(_fixtureA.RootPath);
        await sessionA.Projects.UpsertAsync(CreateProjectRecord(_fixtureA.RootPath, "Game A"));
        await sessionA.DisposeAsync();

        ProjectSession sessionB = await context.OpenAsync(_fixtureB.RootPath);
        await sessionB.Projects.UpsertAsync(CreateProjectRecord(_fixtureB.RootPath, "Game B", "RpgMaker"));
        await sessionB.DisposeAsync();

        // Assert
        Assert.Equal(2, createdFactories.Count);
        TrackingConnectionFactory factoryA = createdFactories[0];
        TrackingConnectionFactory factoryB = createdFactories[1];

        Assert.Equal(1, factoryA.DisposeCount);
        Assert.Equal(0, factoryB.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => factoryA.OpenConnectionAsync());

        IReadOnlyList<ProjectRecord> projects = await context.ListAsync();
        ProjectRecord active = Assert.Single(projects);
        Assert.Equal("Game B", active.GameTitle);
    }

    [Fact]
    public async Task RepositoryDelegation_WhenNoSessionOpened_ReturnsSafeDefaultsAndThrowsOnWrites()
    {
        // Arrange
        await using DesktopProjectContext context = new(CreateIsolatedFactory, _backupRoot);
        ProjectRecord project = CreateProjectRecord(Path.Combine(_tempRoot, "Unopened"), "None");

        // Act & Assert
        Assert.Null(await context.GetByIdAsync("missing"));
        Assert.Empty(await context.ListAsync());
        Assert.Null(await context.GetByIdAsync(1L));
        Assert.Null(await context.GetByEntryKeyAsync("k"));
        Assert.Empty(await context.GetAllAsync());
        Assert.Empty(await context.GetPendingAsync());
        Assert.Empty(await context.QueryAsync());
        Assert.Equal(0, await context.CountAsync());
        Assert.Empty(await context.GetByProjectIdAsync("p"));
        Assert.Null(await context.GetByRelativePathAsync("p", "f"));
        Assert.False(await context.HasManifestsForProjectAsync("p"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.CreateAsync(project));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.DeleteAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.DeleteByProjectIdAsync("p"));
    }

    [Fact]
    public async Task RepositoryDelegation_WithBoundGameRootResolver_DelegatesProjectEntryAndManifestCrud()
    {
        // Arrange
        _fixtureA.CreateLooseFixture();
        string currentRoot = _fixtureA.RootPath;
        await using DesktopProjectContext context = new(CreateIsolatedFactory, _backupRoot);
        context.BindGameRootResolver(() => currentRoot);

        ProjectRecord project = CreateProjectRecord(_fixtureA.RootPath, "Delegated Game");

        // Act & Assert — Project repository
        await context.UpsertAsync(project);
        Assert.True(await context.UpdateNarrativeContextAsync(project.Id, "{\"GameTitle\":\"Updated\"}"));
        ProjectRecord? fetchedProject = await context.GetByIdAsync(project.Id);
        Assert.NotNull(fetchedProject);
        Assert.Equal("{\"GameTitle\":\"Updated\"}", fetchedProject.NarrativeContextJson);

        // Act & Assert — Subtitle entry repository
        IReadOnlyList<SubtitleEntry> inserted = await context.BulkUpsertAsync([
            new SubtitleEntry(
                0, "dialog.json::/1", "dialog.json", "/1", "Hello", "Hello",
                new Dictionary<string, string>(), null, null, EntryStatus.Pending, false)
        ]);
        SubtitleEntry entry = Assert.Single(inserted);
        Assert.Equal(1, await context.CountAsync(EntryStatus.Pending));
        Assert.True(await context.UpdateTranslationAsync(entry.Id, "Halo", "Halo", EntryStatus.Translated, false));
        Assert.Equal(1, await context.CountAsync(EntryStatus.Translated));
        Assert.Empty(await context.GetPendingAsync());
        Assert.Single(await context.QueryAsync(status: EntryStatus.Translated, searchText: "Halo"));

        // Act & Assert — Backup manifest repository
        await context.BulkUpsertAsync([
            new BackupManifestRecord(0, project.Id, "dialog.json", Path.Combine(_backupRoot, "dialog.json"), "abc123", DateTimeOffset.UtcNow)
        ]);
        Assert.True(await context.HasManifestsForProjectAsync(project.Id));
        Assert.NotNull(await context.GetByRelativePathAsync(project.Id, "dialog.json"));
        Assert.Single(await context.GetByProjectIdAsync(project.Id));
    }

    private sealed class TrackingConnectionFactory(ISqliteConnectionFactory inner)
        : ISqliteConnectionFactory, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public string ConnectionString => inner.ConnectionString;
        public bool IsInMemory => inner.IsInMemory;

        public Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken ct = default) =>
            inner.OpenConnectionAsync(ct);

        public Task InitializeSchemaAsync(CancellationToken ct = default) =>
            inner.InitializeSchemaAsync(ct);

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (inner is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                inner.Dispose();
            }
        }

        public void Dispose()
        {
            DisposeCount++;
            inner.Dispose();
        }
    }
}
