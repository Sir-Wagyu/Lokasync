namespace Lokasync.Core.Persistence;

public interface ISqliteConnectionFactory : IAsyncDisposable, IDisposable
{
    string ConnectionString { get; }
    bool IsInMemory { get; }
    Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken ct = default);
    Task InitializeSchemaAsync(CancellationToken ct = default);
}
