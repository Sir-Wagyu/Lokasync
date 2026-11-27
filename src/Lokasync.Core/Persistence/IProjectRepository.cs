using Lokasync.Core.Models;

namespace Lokasync.Core.Persistence;

public interface IProjectRepository
{
    Task<ProjectRecord> CreateAsync(ProjectRecord project, CancellationToken ct = default);
    Task UpsertAsync(ProjectRecord project, CancellationToken ct = default);
    Task<ProjectRecord?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<ProjectRecord?> GetByGameRootPathAsync(string gameRootPath, CancellationToken ct = default);
    Task<bool> UpdateAsync(ProjectRecord project, CancellationToken ct = default);
    Task<bool> UpdateNarrativeContextAsync(string projectId, string? narrativeContextJson, CancellationToken ct = default);
    Task<bool> UpdateDetectedEngineAsync(string projectId, string detectedEngine, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectRecord>> ListAsync(CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
