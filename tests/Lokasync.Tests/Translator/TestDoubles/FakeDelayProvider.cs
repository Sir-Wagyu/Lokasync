using Lokasync.Translator.RateLimiting;

namespace Lokasync.Tests.Translator.TestDoubles;

internal sealed class FakeDelayProvider : IDelayProvider
{
    private readonly List<TimeSpan> _recordedDelays = [];
    private readonly object _sync = new();

    public IReadOnlyList<TimeSpan> RecordedDelays
    {
        get
        {
            lock (_sync)
            {
                return [.. _recordedDelays];
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _recordedDelays.Add(delay);
        }

        return Task.CompletedTask;
    }
}
