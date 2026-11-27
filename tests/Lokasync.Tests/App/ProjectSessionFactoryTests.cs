using System.IO;
using Lokasync.App.Services;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Xunit;

namespace Lokasync.Tests.App;

public sealed class ProjectSessionFactoryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _gameRoot;
    private readonly string _dbRoot;
    private readonly string _backupRoot;

    public ProjectSessionFactoryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Lokasync_SessionFactoryTests", Guid.NewGuid().ToString("N"));
        _gameRoot = Path.Combine(_tempRoot, "Game");
        _dbRoot = Path.Combine(_tempRoot, "dbs");
        _backupRoot = Path.Combine(_tempRoot, "backups");
        Directory.CreateDirectory(_gameRoot);
        Directory.CreateDirectory(_dbRoot);
        Directory.CreateDirectory(_backupRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OpenAsync_ValidGameRoot_CreatesTempDbAndInitializesSchemaTables()
    {
        // Arrange
        string expectedDbPath = Path.Combine(_dbRoot, $"{SqliteConnectionFactory.ComputeProjectHash(_gameRoot)}.db");
        ProjectSessionFactory factory = new(
            root => SqliteConnectionFactory.ForFile(
                Path.Combine(_dbRoot, $"{SqliteConnectionFactory.ComputeProjectHash(root)}.db"),
                pooling: false),
            _backupRoot);

        // Act
        await using ProjectSession session = await factory.OpenAsync(_gameRoot);

        // Assert
        Assert.True(File.Exists(expectedDbPath));
        Assert.Equal(Path.GetFullPath(_gameRoot), session.GameRootPath);

        string projectId = SqliteConnectionFactory.ComputeProjectHash(_gameRoot);
        await session.Projects.UpsertAsync(new ProjectRecord(
            projectId,
            "Sample",
            session.GameRootPath,
            "LooseFile",
            null,
            DateTimeOffset.UtcNow));

        await session.Entries.BulkUpsertAsync([
            new SubtitleEntry(
                0, "dialog.json::/a", "dialog.json", "/a", "Hello", "Hello",
                new Dictionary<string, string>(), null, null, EntryStatus.Pending, false)
        ]);

        await session.BackupManifests.UpsertAsync(new BackupManifestRecord(
            0, projectId, "dialog.json", Path.Combine(_backupRoot, "dialog.json"), "deadbeef", DateTimeOffset.UtcNow));

        Assert.NotNull(await session.Projects.GetByIdAsync(projectId));
        Assert.Single(await session.Entries.GetAllAsync());
        Assert.Single(await session.BackupManifests.GetByProjectIdAsync(projectId));
    }

    [Fact]
    public async Task DisposeAsync_OwnsConnectionFactoryTrue_DisposesUnderlyingConnectionFactory()
    {
        // Arrange
        string dbPath = Path.Combine(_dbRoot, "owning.db");
        SqliteConnectionFactory connectionFactory = SqliteConnectionFactory.ForFile(dbPath, pooling: false);
        await connectionFactory.InitializeSchemaAsync();

        SqliteBackupManifestRepository manifests = new(connectionFactory);
        ProjectSession session = new(
            _gameRoot,
            connectionFactory,
            new SqliteProjectRepository(connectionFactory),
            new SqliteSubtitleEntryRepository(connectionFactory),
            manifests,
            new BackupService(manifests, _backupRoot),
            ownsConnectionFactory: true);

        // Act
        await session.DisposeAsync();

        // Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connectionFactory.OpenConnectionAsync());
    }

    [Fact]
    public async Task DisposeAsync_OwnsConnectionFactoryFalse_PreservesUnderlyingConnectionFactory()
    {
        // Arrange
        string dbPath = Path.Combine(_dbRoot, "non_owning.db");
        await using SqliteConnectionFactory connectionFactory = SqliteConnectionFactory.ForFile(dbPath, pooling: false);
        await connectionFactory.InitializeSchemaAsync();

        SqliteBackupManifestRepository manifests = new(connectionFactory);
        ProjectSession session = new(
            _gameRoot,
            connectionFactory,
            new SqliteProjectRepository(connectionFactory),
            new SqliteSubtitleEntryRepository(connectionFactory),
            manifests,
            new BackupService(manifests, _backupRoot),
            ownsConnectionFactory: false);

        // Act
        await session.DisposeAsync();

        // Assert
        await using SqliteConnectionLease lease = await connectionFactory.OpenConnectionAsync();
        Assert.NotNull(lease.Connection);
        Assert.Empty(await session.Projects.ListAsync());
    }

    [Fact]
    public async Task OpenAsync_WhenSchemaInitializationThrows_DisposesConnectionFactory()
    {
        // Arrange
        FailingSchemaConnectionFactory failingFactory = new();
        ProjectSessionFactory factory = new(_ => failingFactory, _backupRoot);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.OpenAsync(_gameRoot));
        Assert.Equal(1, failingFactory.DisposeCount);
    }

    private sealed class FailingSchemaConnectionFactory : ISqliteConnectionFactory, IAsyncDisposable
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
}
