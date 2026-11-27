using System.Net;

namespace Lokasync.Translator.Worker;

public sealed class TranslationAutoPausedEventArgs(
    HttpStatusCode statusCode,
    string reason,
    int completedCount,
    int totalCount,
    int remainingPendingCount,
    TimeSpan? retryAfter = null) : EventArgs
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Reason { get; } = reason;
    public int CompletedCount { get; } = completedCount;
    public int TotalCount { get; } = totalCount;
    public int RemainingPendingCount { get; } = remainingPendingCount;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public override string ToString()
    {
        return $"Auto-paused (HTTP {(int)StatusCode}): {Reason}";
    }
}
