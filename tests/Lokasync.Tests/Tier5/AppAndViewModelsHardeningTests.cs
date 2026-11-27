using System.IO;
using System.Net.Http;
using Lokasync.Adapters;
using Lokasync.App.Services;
using Lokasync.App.ViewModels;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Sanitizer;
using Lokasync.Tests.App.TestDoubles;
using Lokasync.Tests.Translator.TestDoubles;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Providers;
using Xunit;

namespace Lokasync.Tests.Tier5;

public sealed class AppAndViewModelsHardeningTests : IDisposable
{
    private readonly string _tempRoot;

    public AppAndViewModelsHardeningTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Lokasync_Tier5_App", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopProjectContext_WhenInitializeSchemaThrows_DisposesConnectionFactory()
    {
        // Arrange
        string gameDir = Path.Combine(_tempRoot, "SchemaFailGame");
        Directory.CreateDirectory(gameDir);
        TrackingFailingSchemaFactory trackingFactory = new();
        await using DesktopProjectContext context = new(_ => trackingFactory, Path.Combine(_tempRoot, "backups"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.OpenAsync(gameDir));
        Assert.Equal(1, trackingFactory.DisposeCount);
    }

    [Fact]
    public async Task BackupService_RestoreBackup_PreservesThirdPartyModsAndVerifiesAllBeforeOverwriting()
    {
        // Arrange
        string gameDir = Path.Combine(_tempRoot, "RestoreGame");
        string backupDir = Path.Combine(_tempRoot, "RestoreBackups");
        string modsDir = Path.Combine(gameDir, "Content", "Paks", "~mods");
        Directory.CreateDirectory(modsDir);

        string f1Path = Path.Combine(gameDir, "f1.json");
        string f2Path = Path.Combine(gameDir, "f2.json");
        string otherModPath = Path.Combine(modsDir, "OtherUserMod.pak");
        string tuturModPath = Path.Combine(modsDir, "Lokasync_ID_P.pak");

        await File.WriteAllTextAsync(f1Path, "{\"line\":\"Original 1\"}");
        await File.WriteAllTextAsync(f2Path, "{\"line\":\"Original 2\"}");
        await File.WriteAllTextAsync(otherModPath, "THIRD_PARTY_MOD");
        await File.WriteAllTextAsync(tuturModPath, "LOKASYNC_MOD");

        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        SqliteProjectRepository projectRepo = new(factory);
        await projectRepo.CreateAsync(new ProjectRecord(
            Id: "proj-restore",
            GameTitle: "Restore Test",
            GameRootPath: gameDir,
            DetectedEngine: "LooseFile",
            NarrativeContextJson: null,
            CreatedAt: DateTimeOffset.UtcNow));

        SqliteBackupManifestRepository manifestRepo = new(factory);
        BackupService backupService = new(manifestRepo, backupDir);

        await backupService.CreateBackupAsync("proj-restore", gameDir, ["f1.json", "f2.json"]);
        BackupManifestRecord? f2Manifest = await manifestRepo.GetByRelativePathAsync("proj-restore", "f2.json");
        Assert.NotNull(f2Manifest);

        string originalF2Backup = await File.ReadAllTextAsync(f2Manifest!.BackupFilePath);
        await File.WriteAllTextAsync(f2Manifest.BackupFilePath, "TAMPERED_BACKUP");
        await File.WriteAllTextAsync(f1Path, "{\"line\":\"Modified on disk\"}");

        // Act — Phase 1 fails before overwriting f1.json
        await Assert.ThrowsAsync<InvalidDataException>(() => backupService.RestoreBackupAsync("proj-restore", gameDir));
        string f1AfterFailedRestore = await File.ReadAllTextAsync(f1Path);

        // Restore valid backup for f2 and retry
        await File.WriteAllTextAsync(f2Manifest.BackupFilePath, originalF2Backup);
        await backupService.RestoreBackupAsync("proj-restore", gameDir);

        // Assert
        Assert.Equal("{\"line\":\"Modified on disk\"}", f1AfterFailedRestore);
        Assert.Equal("{\"line\":\"Original 1\"}", await File.ReadAllTextAsync(f1Path));
        Assert.Equal("{\"line\":\"Original 2\"}", await File.ReadAllTextAsync(f2Path));
        Assert.False(File.Exists(tuturModPath));
        Assert.True(File.Exists(otherModPath));
    }

    [Fact]
    public async Task BackupService_InvalidProjectIdOrRootedPath_ThrowsInvalidOperationException()
    {
        // Arrange
        string gameDir = Path.Combine(_tempRoot, "InvalidBackupGame");
        Directory.CreateDirectory(gameDir);
        await File.WriteAllTextAsync(Path.Combine(gameDir, "f1.json"), "{\"a\":\"b\"}");

        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        BackupService backupService = new(factory, Path.Combine(_tempRoot, "InvalidBackups"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => backupService.CreateBackupAsync("../escape", gameDir, ["f1.json"]));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => backupService.CreateBackupAsync("proj1", gameDir, [@"C:\Windows\win.ini"]));
    }

    [Fact]
    public async Task LocalizationPipelineService_ManualEditUnmaskedLiteralTag0AndDuplicateTagCheck_EnforcesExactSemantics()
    {
        // Arrange
        string gameDir = Path.Combine(_tempRoot, "ManualEditGame");
        Directory.CreateDirectory(gameDir);
        await File.WriteAllTextAsync(
            Path.Combine(gameDir, "dialog.json"),
            """{"literal":"Use [TAG_0] for <b>bonus</b>","dup":"<b>One</b> and <b>Two</b>"}""");

        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        SqliteProjectRepository projectRepo = new(factory);
        SqliteSubtitleEntryRepository entryRepo = new(factory);
        SqliteBackupManifestRepository manifestRepo = new(factory);
        BackupService backupService = new(manifestRepo, Path.Combine(_tempRoot, "ManualBackups"));
        using HttpClient httpClient = new(new MockHttpMessageHandler());

        await using LocalizationPipelineService pipeline = new(
            projectRepo,
            entryRepo,
            manifestRepo,
            backupService,
            new TextSanitizer(),
            new OpenAiTranslatorProvider(httpClient),
            connectionFactory: factory);

        await pipeline.OpenProjectAsync(gameDir);
        IReadOnlyList<SubtitleEntry> entries = await pipeline.ExtractAndPreparePendingAsync();
        SubtitleEntry literalEntry = entries.Single(e => e.EntryKey.EndsWith("/literal", StringComparison.Ordinal));
        SubtitleEntry dupEntry = entries.Single(e => e.EntryKey.EndsWith("/dup", StringComparison.Ordinal));

        // Act
        SubtitleEntry literalUpdated = await pipeline.ApplyManualEditAsync(
            literalEntry,
            "Pakai [TAG_0] untuk <b>bonus</b>",
            editedAsMasked: false);

        SubtitleEntry dupUpdated = await pipeline.ApplyManualEditAsync(
            dupEntry,
            "<b>Satu</b>",
            editedAsMasked: false);

        // Assert
        Assert.Equal(EntryStatus.ManuallyEdited, literalUpdated.Status);
        Assert.Equal("Pakai [TAG_0] untuk <b>bonus</b>", literalUpdated.FinalPatchedText);

        Assert.Equal(EntryStatus.FailedValidation, dupUpdated.Status);
        Assert.Equal("<b>One</b> and <b>Two</b>", dupUpdated.FinalPatchedText);
    }

    [Fact]
    public async Task LocalizationPipelineService_DiSessionFactorySwitchingRootsAndPlainBackupAdapter_IsolatesAndGates()
    {
        // Arrange
        string gameA = Path.Combine(_tempRoot, "GameA");
        string gameB = Path.Combine(_tempRoot, "GameB");
        string dbRoot = Path.Combine(_tempRoot, "SessionDbs");
        string backupRoot = Path.Combine(_tempRoot, "SessionBackups");
        Directory.CreateDirectory(gameA);
        Directory.CreateDirectory(gameB);
        Directory.CreateDirectory(dbRoot);

        await File.WriteAllTextAsync(Path.Combine(gameA, "a.json"), """{"kA":"Alpha"}""");
        await File.WriteAllTextAsync(Path.Combine(gameB, "b.json"), """{"kB":"Beta"}""");

        Dictionary<string, TrackingSqliteFactory> createdFactories = new(StringComparer.OrdinalIgnoreCase);
        ProjectSessionFactory sessionFactory = new(
            root =>
            {
                string dbPath = Path.Combine(dbRoot, $"{SqliteConnectionFactory.ComputeProjectHash(root)}.db");
                TrackingSqliteFactory tracked = new(SqliteConnectionFactory.ForFile(dbPath, pooling: false));
                createdFactories[root] = tracked;
                return tracked;
            },
            backupRoot);

        RouterProfileStore profileStore = new(Path.Combine(_tempRoot, "profiles.json"));
        using HttpClient httpClient = new(new MockHttpMessageHandler());
        OpenAiTranslatorProvider provider = new(httpClient);

        await using LocalizationPipelineService diPipeline = new(
            new EngineFingerprinter(),
            new TextSanitizer(),
            provider,
            profileStore,
            sessionFactory);

        // Act — GameA extract + patch, then switch to GameB
        await diPipeline.OpenProjectAsync(gameA);
        await diPipeline.ExtractAndPreparePendingAsync();
        await diPipeline.PatchGameAsync();

        await diPipeline.OpenProjectAsync(gameB);
        IReadOnlyList<SubtitleEntry> gameBEntries = await diPipeline.ExtractAndPreparePendingAsync();

        // Assert — GameA factory disposed on switch; GameB entries isolated
        Assert.Equal(1, createdFactories[Path.GetFullPath(gameA)].DisposeCount);
        Assert.Equal(0, createdFactories[Path.GetFullPath(gameB)].DisposeCount);
        SubtitleEntry bEntry = Assert.Single(gameBEntries);
        Assert.Equal("Beta", bEntry.RawText);

        // Arrange & Act — plain IBackupService (BackupServiceGateAdapter) blocks unbacked RePatchAsync
        await using SqliteConnectionFactory memFactory = SqliteConnectionFactory.ForInMemory();
        await memFactory.InitializeSchemaAsync();
        await using LocalizationPipelineService plainGatePipeline = new(
            new SqliteProjectRepository(memFactory),
            new SqliteSubtitleEntryRepository(memFactory),
            new SqliteBackupManifestRepository(memFactory),
            new PlainNoOpBackupService(),
            new TextSanitizer(),
            provider,
            connectionFactory: memFactory);

        await plainGatePipeline.OpenProjectAsync(gameB);
        await plainGatePipeline.ExtractAndPreparePendingAsync();

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => plainGatePipeline.RePatchAsync());
    }

    [Fact]
    public async Task TranslationGridViewModel_ManualEditHallucinatedOrDuplicateToken_MarksFailedValidation()
    {
        // Arrange
        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        SqliteProjectRepository projectRepo = new(factory);
        SqliteSubtitleEntryRepository entryRepo = new(factory);
        SqliteBackupManifestRepository manifestRepo = new(factory);
        BackupService backupService = new(manifestRepo, Path.Combine(_tempRoot, "GridBackups"));
        TextSanitizer sanitizer = new();
        using HttpClient httpClient = new(new MockHttpMessageHandler());

        await using LocalizationPipelineService pipeline = new(
            projectRepo,
            entryRepo,
            manifestRepo,
            backupService,
            sanitizer,
            new OpenAiTranslatorProvider(httpClient),
            connectionFactory: factory);

        (string masked, Dictionary<string, string> placeholders) = sanitizer.Mask("<b>Hero</b>");
        await entryRepo.BulkUpsertAsync(
        [
            new SubtitleEntry(0, "dialog.json::/k1", "dialog.json", "/k1", "<b>Hero</b>", masked, placeholders, null, null, EntryStatus.Pending, false)
        ]);

        TranslationGridViewModel grid = new(entryRepo, sanitizer, pipeline, dispatcher: new InlineDispatcherService());
        await grid.LoadEntriesAsync("proj-grid");
        SubtitleEntryRowViewModel row = Assert.Single(grid.AllEntries);

        // Act
        await grid.ApplyManualEditAsync(row, "[TAG_0]Pahlawan[TAG_1] [TAG_99]");
        EntryStatus hallucinatedStatus = row.Status;
        string? hallucinatedFinal = row.FinalPatchedText;
        string? hallucinatedError = grid.ErrorMessage;

        await grid.ApplyManualEditAsync(row, "[TAG_0]Pahlawan[TAG_1] [TAG_0]");

        // Assert
        Assert.Equal(EntryStatus.FailedValidation, hallucinatedStatus);
        Assert.Equal("<b>Hero</b>", hallucinatedFinal);
        Assert.False(string.IsNullOrWhiteSpace(hallucinatedError));

        Assert.Equal(EntryStatus.FailedValidation, row.Status);
        Assert.Equal("<b>Hero</b>", row.FinalPatchedText);
        Assert.False(string.IsNullOrWhiteSpace(grid.ErrorMessage));
    }

    [Fact]
    public async Task MainViewModel_WhenGridLoadThrows_SurfacesErrorMessageInsteadOfSilentEmptyGrid()
    {
        // Arrange
        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        SqliteProjectRepository projectRepo = new(factory);
        SqliteBackupManifestRepository manifestRepo = new(factory);
        ThrowingEntryRepository throwingEntryRepo = new();
        BackupService backupService = new(manifestRepo, Path.Combine(_tempRoot, "MainVmBackups"));
        TextSanitizer sanitizer = new();
        using HttpClient httpClient = new(new MockHttpMessageHandler());
        OpenAiTranslatorProvider provider = new(httpClient);
        EngineFingerprinter fingerprinter = new();
        ProjectSessionState session = new();
        InlineDispatcherService dispatcher = new();
        FakeUserNotificationService notifications = new();

        await using LocalizationPipelineService pipeline = new(
            projectRepo,
            throwingEntryRepo,
            manifestRepo,
            backupService,
            sanitizer,
            provider,
            fingerprinter: fingerprinter,
            connectionFactory: factory);

        PipelineControlViewModel pipelineVm = new(
            pipeline,
            projectRepo,
            throwingEntryRepo,
            fingerprinter,
            session: session,
            dispatcher: dispatcher,
            notifications: notifications);

        MainViewModel mainVm = new(
            new ProjectDashboardViewModel(
                projectRepository: projectRepo,
                fingerprinter: fingerprinter,
                session: session,
                folderPicker: new FakeFolderPickerService(),
                notifications: notifications,
                pipeline: pipelineVm),
            new RouterProfileViewModel(new RouterProfileStore(Path.Combine(_tempRoot, "vm_profiles.json")), session, notifications),
            new GlossaryViewModel(projectRepo, session),
            pipelineVm,
            new TranslationGridViewModel(throwingEntryRepo, sanitizer, pipeline, session, dispatcher),
            session);

        // Act
        session.ProjectId = "p-err";

        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (string.IsNullOrEmpty(mainVm.TranslationGrid.ErrorMessage) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(mainVm.TranslationGrid.ErrorMessage));
        Assert.Contains("Simulated entry load failure", mainVm.TranslationGrid.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class TrackingFailingSchemaFactory : ISqliteConnectionFactory, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public string ConnectionString => "Data Source=:memory:";
        public bool IsInMemory => true;

        public Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task InitializeSchemaAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated schema failure.");

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class TrackingSqliteFactory(SqliteConnectionFactory inner) : ISqliteConnectionFactory, IAsyncDisposable
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
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            DisposeCount++;
            inner.Dispose();
        }
    }

    private sealed class PlainNoOpBackupService : IBackupService
    {
        public Task CreateBackupAsync(string projectId, string gameRootPath, IEnumerable<string> relativeFilePaths, CancellationToken ct) =>
            Task.CompletedTask;

        public Task RestoreBackupAsync(string projectId, string gameRootPath, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingEntryRepository : ISubtitleEntryRepository
    {
        public Task<IReadOnlyList<SubtitleEntry>> BulkUpsertAsync(IEnumerable<SubtitleEntry> entries, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubtitleEntry>>([]);

        public Task<int> BulkUpdateAsync(IEnumerable<SubtitleEntry> entries, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<bool> UpdateTranslationAsync(long id, string? translatedText, string? finalPatchedText, EntryStatus status, bool hasOverflowWarning, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> UpdateEntryAsync(SubtitleEntry entry, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<SubtitleEntry?> GetByIdAsync(long id, CancellationToken ct = default) =>
            Task.FromResult<SubtitleEntry?>(null);

        public Task<SubtitleEntry?> GetByEntryKeyAsync(string entryKey, CancellationToken ct = default) =>
            Task.FromResult<SubtitleEntry?>(null);

        public Task<IReadOnlyList<SubtitleEntry>> GetAllAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated entry load failure.");

        public Task<IReadOnlyList<SubtitleEntry>> GetPendingAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubtitleEntry>>([]);

        public Task<IReadOnlyList<SubtitleEntry>> QueryAsync(EntryStatus? status = null, bool? hasOverflowWarning = null, string? searchText = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubtitleEntry>>([]);

        public Task<int> CountAsync(EntryStatus? status = null, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> DeleteAllAsync(CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}
