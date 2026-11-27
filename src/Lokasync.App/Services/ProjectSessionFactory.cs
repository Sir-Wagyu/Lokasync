using System.IO;
using Lokasync.Core.Persistence;

namespace Lokasync.App.Services;

public sealed class ProjectSession : IAsyncDisposable
{
    private readonly bool _ownsConnectionFactory;

    public ProjectSession(
        string gameRootPath,
        ISqliteConnectionFactory connectionFactory,
        IProjectRepository projects,
        ISubtitleEntryRepository entries,
        IBackupManifestRepository backupManifests,
        IAppBackupService backupService,
        bool ownsConnectionFactory = true)
    {
        GameRootPath = gameRootPath ?? throw new ArgumentNullException(nameof(gameRootPath));
        ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        Projects = projects ?? throw new ArgumentNullException(nameof(projects));
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        BackupManifests = backupManifests ?? throw new ArgumentNullException(nameof(backupManifests));
        BackupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _ownsConnectionFactory = ownsConnectionFactory;
    }

    public string GameRootPath { get; }
    public ISqliteConnectionFactory ConnectionFactory { get; }
    public IProjectRepository Projects { get; }
    public ISubtitleEntryRepository Entries { get; }
    public IBackupManifestRepository BackupManifests { get; }
    public IAppBackupService BackupService { get; }

    public async ValueTask DisposeAsync()
    {
        if (_ownsConnectionFactory && ConnectionFactory is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_ownsConnectionFactory && ConnectionFactory is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public interface IProjectSessionFactory
{
    Task<ProjectSession> OpenAsync(string gameRootPath, CancellationToken ct = default);
}

public sealed class ProjectSessionFactory : IProjectSessionFactory
{
    private readonly Func<string, ISqliteConnectionFactory> _connectionFactoryProvider;
    private readonly string? _backupRootDirectory;

    public ProjectSessionFactory(
        Func<string, ISqliteConnectionFactory>? connectionFactoryProvider = null,
        string? backupRootDirectory = null)
    {
        _connectionFactoryProvider = connectionFactoryProvider ?? SqliteConnectionFactory.ForProject;
        _backupRootDirectory = backupRootDirectory;
    }

    public async Task<ProjectSession> OpenAsync(string gameRootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);

        string normalizedRoot = Path.GetFullPath(gameRootPath);
        ISqliteConnectionFactory factory = _connectionFactoryProvider(normalizedRoot);

        try
        {
            await factory.InitializeSchemaAsync(ct).ConfigureAwait(false);

            IProjectRepository projects = new SqliteProjectRepository(factory);
            ISubtitleEntryRepository entries = new SqliteSubtitleEntryRepository(factory);
            IBackupManifestRepository manifests = new SqliteBackupManifestRepository(factory);
            IAppBackupService backupService = new BackupService(manifests, _backupRootDirectory);

            return new ProjectSession(
                normalizedRoot,
                factory,
                projects,
                entries,
                manifests,
                backupService,
                ownsConnectionFactory: true);
        }
        catch
        {
            if (factory is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (factory is IDisposable disposable)
            {
                disposable.Dispose();
            }

            throw;
        }
    }
}
