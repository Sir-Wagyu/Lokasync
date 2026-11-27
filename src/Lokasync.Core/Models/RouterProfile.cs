namespace Lokasync.Core.Models
{
    public record RouterProfile(
        string BaseUrl,
        string ApiKey,
        string Model,
        int MaxConcurrency,
        int DelayBetweenBatchesMs,
        int JitterMs,
        int BatchSize
    );
}
