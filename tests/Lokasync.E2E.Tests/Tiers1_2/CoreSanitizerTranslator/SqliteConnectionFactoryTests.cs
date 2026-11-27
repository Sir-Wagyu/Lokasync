using System.Data;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.CoreSanitizerTranslator;

public sealed class SqliteConnectionFactoryTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_DefaultProjectHashPath_CreatesDbUnderLocalAppDataAndInitializesSchema()
    {
        // Arrange
        string projectKey = $"e2e_m2_{Guid.NewGuid():N}";
        string expectedProjectsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lokasync",
            "Projects");
        string? resolvedPath = null;

        try
        {
            // Act
            (string path, SqliteConnection conn, object? factory, IDisposable? lease) =
                await CoreTranslatorTestHelpers.OpenDefaultProjectDatabaseAsync(projectKey);
            resolvedPath = path;

            using (lease)
            await using (conn)
            {
                if (factory is IDisposable dispFactory)
                {
                    // Assert
                    Assert.Equal(
                        System.IO.Path.GetFullPath(expectedProjectsDir),
                        System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(path)!),
                        ignoreCase: true);
                    Assert.EndsWith(".db", path, StringComparison.OrdinalIgnoreCase);
                    Assert.True(File.Exists(path), $"Expected SQLite DB at '{path}'.");
                    Assert.Equal(ConnectionState.Open, conn.State);
                    E2EAssertions.AssertSqliteSchemaValid(conn);
                    dispFactory.Dispose();
                    return;
                }

                // Assert
                Assert.Equal(
                    System.IO.Path.GetFullPath(expectedProjectsDir),
                    System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(path)!),
                    ignoreCase: true);
                Assert.EndsWith(".db", path, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(path), $"Expected SQLite DB at '{path}'.");
                Assert.Equal(ConnectionState.Open, conn.State);
                E2EAssertions.AssertSqliteSchemaValid(conn);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
            {
                try
                {
                    File.Delete(resolvedPath);
                }
                catch (IOException)
                {
                    // Ignore transient pool lock on cleanup
                }
            }
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_CustomFilePathOverride_CreatesDatabaseFileAndAllThreeTables()
    {
        // Arrange
        using TempWorkspace ws = new("f1_custom");
        OpaqueContractBridge bridge = new();

        // Act
        await using DatabaseSession session = await bridge.OpenDatabaseAsync(ws.DatabasePath);

        // Assert
        Assert.True(File.Exists(ws.DatabasePath), $"Expected database file at '{ws.DatabasePath}'.");
        Assert.Equal(ConnectionState.Open, session.Connection.State);
        E2EAssertions.AssertSqliteSchemaValid(session.Connection);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_InMemoryConnection_InitializesProjectsSubtitleEntriesAndBackupManifests()
    {
        // Arrange
        OpaqueContractBridge bridge = new();

        // Act
        await using DatabaseSession session = await bridge.OpenDatabaseAsync("Data Source=:memory:");

        // Assert
        Assert.Equal(ConnectionState.Open, session.Connection.State);
        E2EAssertions.AssertSqliteSchemaValid(session.Connection);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F1")]
    public async Task InitializeSchemaAsync_CalledTwiceOnPopulatedDb_IsIdempotentAndPreservesRows()
    {
        // Arrange
        using TempWorkspace ws = new("f1_idempotent");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession session = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await CoreTranslatorTestHelpers.CreateProjectAsync(
            session,
            "proj-idempotent",
            "Expedition Zero",
            ws.GameRootPath,
            "LooseFile",
            null);

        // Act
        await CoreTranslatorTestHelpers.InvokeFactorySchemaInitAsync(session);
        ProjectSnapshot? fetched = await CoreTranslatorTestHelpers.GetProjectByIdAsync(session, "proj-idempotent");

        // Assert
        E2EAssertions.AssertSqliteSchemaValid(session.Connection);
        Assert.NotNull(fetched);
        Assert.Equal("Expedition Zero", fetched!.GameTitle);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_VerifyColumnSchemasAndDefaults_MatchesPrdSection32()
    {
        // Arrange
        using TempWorkspace ws = new("f1_schema_defaults");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession session = await bridge.OpenDatabaseAsync(ws.DatabasePath);

        // Act
        E2EAssertions.AssertSqliteSchemaValid(session.Connection);
        await using (SqliteCommand insertCmd = session.Connection.CreateCommand())
        {
            insertCmd.CommandText = """
                INSERT INTO subtitle_entries (entry_key, source_file, raw_text, masked_text, placeholders_json)
                VALUES (@key, @file, @raw, @masked, @ph);
                """;
            insertCmd.Parameters.AddWithValue("@key", "k_default");
            insertCmd.Parameters.AddWithValue("@file", "dialog.srt");
            insertCmd.Parameters.AddWithValue("@raw", "Hello");
            insertCmd.Parameters.AddWithValue("@masked", "Hello");
            insertCmd.Parameters.AddWithValue("@ph", "{}");
            await insertCmd.ExecuteNonQueryAsync();
        }

        await using SqliteCommand readCmd = session.Connection.CreateCommand();
        readCmd.CommandText = "SELECT status, has_overflow_warning FROM subtitle_entries WHERE entry_key = 'k_default';";
        await using SqliteDataReader reader = await readCmd.ExecuteReaderAsync();

        // Assert
        Assert.True(await reader.ReadAsync());
        Assert.Equal("PENDING", reader.GetString(0), ignoreCase: true);
        Assert.Equal(0, reader.GetInt32(1));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_SequentialConnectionsToSameFile_SharePersistedData()
    {
        // Arrange
        using TempWorkspace ws = new("f1_sequential_persist");
        OpaqueContractBridge bridge = new();
        await using (DatabaseSession first = await bridge.OpenDatabaseAsync(ws.DatabasePath))
        {
            await CoreTranslatorTestHelpers.CreateProjectAsync(
                first,
                "proj-persist",
                "Persisted Game",
                ws.GameRootPath,
                "RpgMaker",
                "{\"game_title\":\"Persisted Game\"}");
        }

        // Act
        await using DatabaseSession second = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        ProjectSnapshot? loaded = await CoreTranslatorTestHelpers.GetProjectByIdAsync(second, "proj-persist");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("Persisted Game", loaded!.GameTitle);
        Assert.Equal("RpgMaker", loaded.DetectedEngine);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using TempWorkspace ws = new("f1_cancelled");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            (SqliteConnection conn, IDisposable? lease) =
                await CoreTranslatorTestHelpers.OpenViaFactoryPathAsync(ws.DatabasePath, cts.Token);
            using (lease)
            await using (conn) { }
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_NullOrWhitespaceDatabasePath_ThrowsArgumentException(string badPath)
    {
        // Arrange
        OpaqueContractBridge bridge = new();

        // Act & Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(() => bridge.OpenDatabaseAsync(badPath));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_PathPointingToExistingDirectory_ThrowsSqliteOrIoException()
    {
        // Arrange
        using TempWorkspace ws = new("f1_dir_path");

        // Act
        Exception? ex = await Record.ExceptionAsync(async () =>
        {
            (SqliteConnection conn, IDisposable? lease) =
                await CoreTranslatorTestHelpers.OpenViaFactoryPathAsync(ws.RootPath);
            using (lease)
            await using (conn) { }
        });

        // Assert
        Assert.NotNull(ex);
        Assert.True(ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException,
            $"Unexpected exception type: {ex!.GetType().FullName}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_NestedNonExistentParentDirectory_CreatesDirectoriesOrFailsPredictably()
    {
        // Arrange
        using TempWorkspace ws = new("f1_nested_dir");
        string nestedDb = System.IO.Path.Combine(ws.RootPath, "nested", "a", "b", "test.db");
        OpaqueContractBridge bridge = new();

        // Act
        DatabaseSession? session = null;
        Exception? ex = await Record.ExceptionAsync(async () =>
        {
            session = await bridge.OpenDatabaseAsync(nestedDb);
        });

        // Assert
        if (ex is null && session is not null)
        {
            await using (session)
            {
                Assert.True(File.Exists(nestedDb));
                E2EAssertions.AssertSqliteSchemaValid(session.Connection);
            }
        }
        else
        {
            Assert.NotNull(ex);
            Assert.True(ex is IOException or SqliteException, $"Expected IO/SqliteException, got {ex!.GetType().FullName}");
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_InvalidPathCharacters_ThrowsArgumentOrSqliteException()
    {
        // Arrange
        string invalidPath = "bad\0path.db";

        // Act
        Exception? ex = await Record.ExceptionAsync(async () =>
        {
            (SqliteConnection conn, IDisposable? lease) =
                await CoreTranslatorTestHelpers.OpenViaFactoryPathAsync(invalidPath);
            using (lease)
            await using (conn) { }
        });

        // Assert
        Assert.NotNull(ex);
        Assert.True(ex is ArgumentException or SqliteException, $"Unexpected exception type: {ex!.GetType().FullName}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F1")]
    public async Task CreateConnectionAsync_InMemoryDbClosedAndReopened_StartsWithCleanEmptySchema()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        await using (DatabaseSession first = await bridge.OpenDatabaseAsync("Data Source=:memory:"))
        {
            await using SqliteCommand cmd = first.Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO projects (id, game_title, game_root_path, detected_engine) VALUES ('mem1', 'MemGame', 'C:\\Game', 'LooseFile');";
            await cmd.ExecuteNonQueryAsync();
        }

        // Act
        await using DatabaseSession second = await bridge.OpenDatabaseAsync("Data Source=:memory:");
        await using SqliteCommand countCmd = second.Connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM projects;";
        long count = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

        // Assert
        E2EAssertions.AssertSqliteSchemaValid(second.Connection);
        Assert.Equal(0L, count);
    }
}
