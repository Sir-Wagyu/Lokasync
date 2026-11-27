using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lokasync.Core.Persistence;

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly SemaphoreSlim _memoryGate = new(1, 1);
    private SqliteConnection? _anchorConnection;
    private int _disposed;

    private SqliteConnectionFactory(string connectionString, bool isInMemory)
    {
        ConnectionString = connectionString;
        IsInMemory = isInMemory;
    }

    public string ConnectionString { get; }
    public bool IsInMemory { get; }

    public static string NormalizeGameRootPath(string gameRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        var full = Path.GetFullPath(gameRootPath);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return OperatingSystem.IsWindows() ? trimmed.ToUpperInvariant() : trimmed;
    }

    public static string ComputeProjectHash(string gameRootPath)
    {
        var normalized = NormalizeGameRootPath(gameRootPath);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string GetDefaultDatabasePath(string gameRootPath)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Lokasync", "Projects", $"{ComputeProjectHash(gameRootPath)}.db");
    }

    public static SqliteConnectionFactory ForProject(string gameRootPath)
    {
        var dbPath = GetDefaultDatabasePath(gameRootPath);
        return ForFile(dbPath, pooling: true);
    }

    public static SqliteConnectionFactory ForFile(string databasePath, bool pooling = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = pooling
        };

        return new SqliteConnectionFactory(builder.ToString(), isInMemory: false);
    }

    public static SqliteConnectionFactory ForInMemory()
    {
        return new SqliteConnectionFactory("Data Source=:memory:", isInMemory: true);
    }

    public async Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (IsInMemory)
        {
            await _memoryGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                if (_anchorConnection is null)
                {
                    var anchor = new SqliteConnection(ConnectionString);
                    try
                    {
                        await anchor.OpenAsync(ct).ConfigureAwait(false);
                        await ExecutePragmaAsync(anchor, "PRAGMA foreign_keys = ON;", ct).ConfigureAwait(false);
                        _anchorConnection = anchor;
                    }
                    catch
                    {
                        await anchor.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
                else
                {
                    await ExecutePragmaAsync(_anchorConnection, "PRAGMA foreign_keys = ON;", ct).ConfigureAwait(false);
                }

                return new SqliteConnectionLease(_anchorConnection, ownsConnection: false, _memoryGate);
            }
            catch
            {
                _memoryGate.Release();
                throw;
            }
        }

        var connection = new SqliteConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ExecutePragmaAsync(
                connection,
                "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;",
                ct).ConfigureAwait(false);
            return new SqliteConnectionLease(connection, ownsConnection: true, gate: null);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task InitializeSchemaAsync(CancellationToken ct = default)
    {
        await using var lease = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SqliteSchemaInitializer.SchemaSql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_anchorConnection is not null)
        {
            await _anchorConnection.DisposeAsync().ConfigureAwait(false);
            _anchorConnection = null;
        }

        _memoryGate.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _anchorConnection?.Dispose();
        _anchorConnection = null;
        _memoryGate.Dispose();
    }

    private static async Task ExecutePragmaAsync(SqliteConnection connection, string pragmaSql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = pragmaSql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
