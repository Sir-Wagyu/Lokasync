using Lokasync.Core.Models;

namespace Lokasync.Core.Persistence;

public interface IBackupManifestRepository
{
    Task<BackupManifestRecord> UpsertAsync(BackupManifestRecord manifest, CancellationToken ct = default);
    Task<IReadOnlyList<BackupManifestRecord>> BulkUpsertAsync(IEnumerable<BackupManifestRecord> manifests, CancellationToken ct = default);
    Task<IReadOnlyList<BackupManifestRecord>> GetByProjectIdAsync(string projectId, CancellationToken ct = default);
    Task<BackupManifestRecord?> GetByRelativePathAsync(string projectId, string originalRelativePath, CancellationToken ct = default);
    Task<bool> HasManifestsForProjectAsync(string projectId, CancellationToken ct = default);
    Task<int> DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
}
