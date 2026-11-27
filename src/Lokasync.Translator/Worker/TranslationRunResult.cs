using System.Net;

namespace Lokasync.Translator.Worker;

public enum TranslationRunStatus
{
    Completed,
    AutoPaused,
    PausedByUser
}

public sealed record TranslationRunResult(
    TranslationRunStatus Status,
    int TotalCount,
    int CompletedCount,
    int ProcessedThisRun,
    int RemainingPendingCount,
    int FailedValidationCount = 0,
    HttpStatusCode? AutoPauseStatusCode = null,
    string? AutoPauseReason = null);
