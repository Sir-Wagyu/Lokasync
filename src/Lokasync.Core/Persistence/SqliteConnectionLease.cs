using Microsoft.Data.Sqlite;

namespace Lokasync.Core.Persistence;

public sealed class SqliteConnectionLease : IAsyncDisposable, IDisposable
{
    private readonly bool _ownsConnection;
    private readonly SemaphoreSlim? _gate;
    private int _disposed;

    internal SqliteConnectionLease(SqliteConnection connection, bool ownsConnection, SemaphoreSlim? gate)
    {
        Connection = connection;
        _ownsConnection = ownsConnection;
        _gate = gate;
    }

    public SqliteConnection Connection { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_ownsConnection)
            {
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate?.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_ownsConnection)
            {
                Connection.Dispose();
            }
        }
        finally
        {
            _gate?.Release();
        }
    }
}
