using Lokasync.Core.Models;

namespace Lokasync.Core.Persistence;

public interface ISubtitleEntryRepository
{
    Task<IReadOnlyList<SubtitleEntry>> BulkUpsertAsync(IEnumerable<SubtitleEntry> entries, CancellationToken ct = default);
    Task<int> BulkUpdateAsync(IEnumerable<SubtitleEntry> entries, CancellationToken ct = default);
    Task<bool> UpdateTranslationAsync(
        long id,
        string? translatedText,
        string? finalPatchedText,
        EntryStatus status,
        bool hasOverflowWarning,
        CancellationToken ct = default);
    Task<bool> UpdateEntryAsync(SubtitleEntry entry, CancellationToken ct = default);
    Task<SubtitleEntry?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<SubtitleEntry?> GetByEntryKeyAsync(string entryKey, CancellationToken ct = default);
    Task<IReadOnlyList<SubtitleEntry>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SubtitleEntry>> GetPendingAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SubtitleEntry>> QueryAsync(
        EntryStatus? status = null,
        bool? hasOverflowWarning = null,
        string? searchText = null,
        CancellationToken ct = default);
    Task<int> CountAsync(EntryStatus? status = null, CancellationToken ct = default);
    Task<int> DeleteAllAsync(CancellationToken ct = default);
}
