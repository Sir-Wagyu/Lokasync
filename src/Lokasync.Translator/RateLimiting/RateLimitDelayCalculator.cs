namespace Lokasync.Translator.RateLimiting;

public static class RateLimitDelayCalculator
{
    public static TimeSpan ComputeDelay(int delayBetweenBatchesMs, int jitterMs, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        int baseMs = Math.Max(0, delayBetweenBatchesMs);
        int safeJitter = Math.Max(0, jitterMs);
        int offset = safeJitter == 0 ? 0 : random.Next(-safeJitter, safeJitter + 1);
        int totalMs = Math.Max(0, baseMs + offset);

        return TimeSpan.FromMilliseconds(totalMs);
    }
}
