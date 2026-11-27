namespace Lokasync.Translator.RateLimiting;

public sealed class SystemDelayProvider : IDelayProvider
{
    public async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
