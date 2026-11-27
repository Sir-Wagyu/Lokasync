using System.IO;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;

namespace Lokasync.App.Services;

public sealed class DesktopProjectContext :
    IProjectSessionFactory,
    IProjectRepository,
    ISubtitleEntryRepository,
    IBackupManifestRepository,
    IAsyncDisposable
{
    private readonly Func<string, ISqliteConnectionFactory> _connectionFactoryProvider;
    private readonly string? _backupRootDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _currentNormalizedRoot;
    private ProjectSession? _currentSession;
    private Func<string>? _gameRootPathResolver;

    public DesktopProjectContext(
        Func<string, ISqliteConnectionFactory>? connectionFactoryProvider = null,
        string? backupRootDirectory = null)
    {
        _connectionFactoryProvider = connectionFactoryProvider ?? SqliteConnectionFactory.ForProject;
        _backupRootDirectory = backupRootDirectory;
    }

    public void BindGameRootResolver(Func<string> gameRootPathResolver)
    {
        _gameRootPathResolver = gameRootPathResolver ?? throw new ArgumentNullException(nameof(gameRootPathResolver));
    }

    public async Task<ProjectSession> OpenAsync(string gameRootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        string fullRoot = Path.GetFullPath(gameRootPath);
        string normalized = SqliteConnectionFactory.NormalizeGameRootPath(fullRoot);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_currentSession is not null &&
                string.Equals(_currentNormalizedRoot, normalized, StringComparison.Ordinal))
            {
                return CreateNonOwningView(_currentSession);
            }

            if (_currentSession is not null)
            {
                await _currentSession.DisposeAsync().ConfigureAwait(false);
                _currentSession = null;
                _currentNormalizedRoot = null;
            }

            ISqliteConnectionFactory factory = _connectionFactoryProvider(fullRoot);
            try
            {
                await factory.InitializeSchemaAsync(ct).ConfigureAwait(false);

                IProjectRepository projects = new SqliteProjectRepository(factory);
                ISubtitleEntryRepository entries = new SqliteSubtitleEntryRepository(factory);
                IBackupManifestRepository manifests = new SqliteBackupManifestRepository(factory);
                IAppBackupService backupService = new BackupService(manifests, _backupRootDirectory);

                _currentSession = new ProjectSession(
                    fullRoot,
                    factory,
                    projects,
                    entries,
                    manifests,
                    backupService,
                    ownsConnectionFactory: true);
                _currentNormalizedRoot = normalized;

                // Wrap in non-owning view so callers disposing their handle don't close the shared desktop session prematurely.
                return CreateNonOwningView(_currentSession);
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
        finally
        {
            _gate.Release();
        }
    }

    private static ProjectSession CreateNonOwningView(ProjectSession session)
    {
        return new ProjectSession(
            session.GameRootPath,
            session.ConnectionFactory,
            session.Projects,
            session.Entries,
            session.BackupManifests,
            session.BackupService,
            ownsConnectionFactory: false);
    }

    private async Task<ProjectSession?> ResolveSessionAsync(CancellationToken ct)
    {
        string? root = _gameRootPathResolver?.Invoke();
        if (root is null || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return _currentSession;
        }

        return await OpenAsync(root, ct).ConfigureAwait(false);
    }

    private async Task<ProjectSession> RequireSessionAsync(CancellationToken ct)
    {
        return await ResolveSessionAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No game folder is currently selected.");
    }

    // IProjectRepository
    public async Task<ProjectRecord> CreateAsync(ProjectRecord project, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Projects.CreateAsync(project, ct).ConfigureAwait(false);

    public async Task UpsertAsync(ProjectRecord project, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Projects.UpsertAsync(project, ct).ConfigureAwait(false);

    public async Task<ProjectRecord?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null ? null : await session.Projects.GetByIdAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<ProjectRecord?> GetByGameRootPathAsync(string gameRootPath, CancellationToken ct = default)
    {
        ProjectSession session = await OpenAsync(gameRootPath, ct).ConfigureAwait(false);
        return await session.Projects.GetByGameRootPathAsync(gameRootPath, ct).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(ProjectRecord project, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Projects.UpdateAsync(project, ct).ConfigureAwait(false);

    public async Task<bool> UpdateNarrativeContextAsync(string projectId, string? narrativeContextJson, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Projects.UpdateNarrativeContextAsync(projectId, narrativeContextJson, ct).ConfigureAwait(false);

    public async Task<bool> UpdateDetectedEngineAsync(string projectId, string detectedEngine, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Projects.UpdateDetectedEngineAsync(projectId, detectedEngine, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ProjectRecord>> ListAsync(CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null ? Array.Empty<ProjectRecord>() : await session.Projects.ListAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Projects.DeleteAsync(id, ct).ConfigureAwait(false);

    // ISubtitleEntryRepository
    public async Task<IReadOnlyList<SubtitleEntry>> BulkUpsertAsync(IEnumerable<SubtitleEntry> entries, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Entries.BulkUpsertAsync(entries, ct).ConfigureAwait(false);

    public async Task<int> BulkUpdateAsync(IEnumerable<SubtitleEntry> entries, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Entries.BulkUpdateAsync(entries, ct).ConfigureAwait(false);

    public async Task<bool> UpdateTranslationAsync(
        long id,
        string? translatedText,
        string? finalPatchedText,
        EntryStatus status,
        bool hasOverflowWarning,
        CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Entries
            .UpdateTranslationAsync(id, translatedText, finalPatchedText, status, hasOverflowWarning, ct)
            .ConfigureAwait(false);

    public async Task<bool> UpdateEntryAsync(SubtitleEntry entry, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Entries.UpdateEntryAsync(entry, ct).ConfigureAwait(false);

    public async Task<SubtitleEntry?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null ? null : await session.Entries.GetByIdAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<SubtitleEntry?> GetByEntryKeyAsync(string entryKey, CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null ? null : await session.Entries.GetByEntryKeyAsync(entryKey, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubtitleEntry>> GetAllAsync(CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null ? Array.Empty<SubtitleEntry>() : await session.Entries.GetAllAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubtitleEntry>> GetPendingAsync(CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null ? Array.Empty<SubtitleEntry>() : await session.Entries.GetPendingAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubtitleEntry>> QueryAsync(
        EntryStatus? status = null,
        bool? hasOverflowWarning = null,
        string? searchText = null,
        CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null
            ? Array.Empty<SubtitleEntry>()
            : await session.Entries.QueryAsync(status, hasOverflowWarning, searchText, ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(EntryStatus? status = null, CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null ? 0 : await session.Entries.CountAsync(status, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteAllAsync(CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).Entries.DeleteAllAsync(ct).ConfigureAwait(false);

    // IBackupManifestRepository
    public async Task<BackupManifestRecord> UpsertAsync(BackupManifestRecord manifest, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).BackupManifests.UpsertAsync(manifest, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<BackupManifestRecord>> BulkUpsertAsync(
        IEnumerable<BackupManifestRecord> manifests,
        CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).BackupManifests.BulkUpsertAsync(manifests, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<BackupManifestRecord>> GetByProjectIdAsync(string projectId, CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null
            ? Array.Empty<BackupManifestRecord>()
            : await session.BackupManifests.GetByProjectIdAsync(projectId, ct).ConfigureAwait(false);
    }

    public async Task<BackupManifestRecord?> GetByRelativePathAsync(
        string projectId,
        string originalRelativePath,
        CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is null
            ? null
            : await session.BackupManifests.GetByRelativePathAsync(projectId, originalRelativePath, ct).ConfigureAwait(false);
    }

    public async Task<bool> HasManifestsForProjectAsync(string projectId, CancellationToken ct = default)
    {
        ProjectSession? session = await ResolveSessionAsync(ct).ConfigureAwait(false);
        return session is not null &&
               await session.BackupManifests.HasManifestsForProjectAsync(projectId, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteByProjectIdAsync(string projectId, CancellationToken ct = default) =>
        await (await RequireSessionAsync(ct).ConfigureAwait(false)).BackupManifests.DeleteByProjectIdAsync(projectId, ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_currentSession is not null)
        {
            await _currentSession.DisposeAsync().ConfigureAwait(false);
            _currentSession = null;
        }

        _gate.Dispose();
    }
}
