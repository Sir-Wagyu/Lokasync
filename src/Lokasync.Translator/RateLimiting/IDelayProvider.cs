namespace Lokasync.Translator.RateLimiting;

public interface IDelayProvider
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}
