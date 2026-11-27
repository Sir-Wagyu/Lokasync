using Lokasync.Core.Models;

namespace Lokasync.Core.Persistence;

public static class EntryStatusMapper
{
    public static string ToDbString(EntryStatus status) => status switch
    {
        EntryStatus.Pending => "PENDING",
        EntryStatus.Translated => "TRANSLATED",
        EntryStatus.FailedValidation => "FAILED_VALIDATION",
        EntryStatus.ManuallyEdited => "EDITED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported EntryStatus value.")
    };

    public static EntryStatus FromDbString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return value.Trim().ToUpperInvariant() switch
        {
            "PENDING" => EntryStatus.Pending,
            "TRANSLATED" => EntryStatus.Translated,
            "FAILED_VALIDATION" or "FAILEDVALIDATION" => EntryStatus.FailedValidation,
            "EDITED" or "MANUALLY_EDITED" or "MANUALLYEDITED" => EntryStatus.ManuallyEdited,
            _ => throw new InvalidOperationException($"Unknown EntryStatus value '{value}'.")
        };
    }
}
