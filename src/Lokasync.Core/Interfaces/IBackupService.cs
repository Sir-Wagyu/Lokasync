namespace Lokasync.Core.Interfaces
{
    public interface IBackupService
    {
        Task CreateBackupAsync(string projectId, string gameRootPath, IEnumerable<string> relativeFilePaths, CancellationToken ct);
        Task RestoreBackupAsync(string projectId, string gameRootPath, CancellationToken ct);
    }
}
