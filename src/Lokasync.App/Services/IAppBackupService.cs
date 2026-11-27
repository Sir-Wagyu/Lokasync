using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;

namespace Lokasync.App.Services;

public interface IAppBackupService : IBackupService
{
    Task<bool> HasVerifiedBackupAsync(
        string projectId,
        IEnumerable<string> relativeFilePaths,
        CancellationToken ct = default);

    Task EnsureBackupVerifiedAsync(
        string projectId,
        IEnumerable<string> relativeFilePaths,
        CancellationToken ct = default);

    Task<IReadOnlyList<BackupManifestRecord>> GetManifestsAsync(
        string projectId,
        CancellationToken ct = default);
}
