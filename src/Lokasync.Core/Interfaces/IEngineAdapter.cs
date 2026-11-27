namespace Lokasync.Core.Interfaces
{
    using Lokasync.Core.Models;

    public interface IEngineAdapter
    {
        string EngineName { get; }
        bool CanHandle(string gameRootPath);
        Task<List<SubtitleEntry>> ExtractAsync(string gameRootPath, CancellationToken ct);
        Task PatchAsync(string gameRootPath, IReadOnlyList<SubtitleEntry> entries, CancellationToken ct);
    }
}
