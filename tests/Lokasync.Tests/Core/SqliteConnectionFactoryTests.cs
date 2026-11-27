using System.IO;
using Lokasync.Core.Persistence;
using Xunit;

namespace Lokasync.Tests.Core;

public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        AppContext.BaseDirectory,
        "_test_tmp",
        nameof(SqliteConnectionFactoryTests),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ComputeProjectHash_SameWindowsPathWithDifferentCaseAndTrailingSlash_ReturnsIdenticalHash()
    {
        // Arrange
        const string pathA = @"C:\Games\ExpeditionZero\";
        const string pathB = @"c:\games\expeditionzero";

        // Act
        var hashA = SqliteConnectionFactory.ComputeProjectHash(pathA);
        var hashB = SqliteConnectionFactory.ComputeProjectHash(pathB);

        // Assert
        Assert.Equal(64, hashA.Length);
        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void ComputeProjectHash_DistinctPaths_ReturnsDistinctHexHashes()
    {
        // Arrange
        const string pathA = @"C:\Games\GameAlpha";
        const string pathB = @"C:\Games\GameBeta";

        // Act
        var hashA = SqliteConnectionFactory.ComputeProjectHash(pathA);
        var hashB = SqliteConnectionFactory.ComputeProjectHash(pathB);

        // Assert
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void GetDefaultDatabasePath_ValidGameRoot_ReturnsLocalAppDataProjectsDbPath()
    {
        // Arrange
        const string gameRoot = @"C:\Games\ExpeditionZero";
        var expectedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lokasync",
            "Projects");
        var expectedHash = SqliteConnectionFactory.ComputeProjectHash(gameRoot);

        // Act
        var dbPath = SqliteConnectionFactory.GetDefaultDatabasePath(gameRoot);

        // Assert
        Assert.StartsWith(expectedDir, dbPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"{expectedHash}.db", Path.GetFileName(dbPath));
    }

    [Fact]
    public async Task InitializeSchemaAsync_InMemoryDatabase_CreatesExpectedTablesAndIndexes()
    {
        // Arrange
        await using var factory = SqliteConnectionFactory.ForInMemory();

        // Act
        await factory.InitializeSchemaAsync();
        var objects = new List<string>();
        await using (var lease = await factory.OpenConnectionAsync())
        await using (var cmd = lease.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'index') ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                objects.Add(reader.GetString(0));
            }
        }

        // Assert
        Assert.Contains("projects", objects);
        Assert.Contains("subtitle_entries", objects);
        Assert.Contains("backup_manifests", objects);
        Assert.Contains("idx_subtitle_entries_entry_key", objects);
        Assert.Contains("idx_subtitle_entries_status", objects);
        Assert.Contains("idx_subtitle_entries_overflow", objects);
        Assert.Contains("idx_backup_manifests_project_path", objects);
    }

    [Fact]
    public async Task InitializeSchemaAsync_InvokedTwice_RemainsIdempotent()
    {
        // Arrange
        await using var factory = SqliteConnectionFactory.ForInMemory();

        // Act
        await factory.InitializeSchemaAsync();
        await factory.InitializeSchemaAsync();

        await using var lease = await factory.OpenConnectionAsync();
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('projects', 'subtitle_entries', 'backup_manifests');";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        // Assert
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task OpenConnectionAsync_InMemoryDatabase_EnablesForeignKeys()
    {
        // Arrange
        await using var factory = SqliteConnectionFactory.ForInMemory();

        // Act
        await using var lease = await factory.OpenConnectionAsync();
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var fkEnabled = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        // Assert
        Assert.Equal(1, fkEnabled);
    }

    [Fact]
    public async Task ForFile_NonPooledTempDatabase_AllowsFileDeletionAfterDispose()
    {
        // Arrange
        Directory.CreateDirectory(_tempRoot);
        var dbPath = Path.Combine(_tempRoot, "temp_non_pooled.db");

        await using (var factory = SqliteConnectionFactory.ForFile(dbPath, pooling: false))
        {
            await factory.InitializeSchemaAsync();
        }

        // Act
        File.Delete(dbPath);

        // Assert
        Assert.False(File.Exists(dbPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
