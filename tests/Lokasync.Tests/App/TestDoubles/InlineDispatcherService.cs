using Lokasync.App.Services;

namespace Lokasync.Tests.App.TestDoubles;

internal sealed class InlineDispatcherService : IDispatcherService
{
    public int InvokeCount { get; private set; }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        InvokeCount++;
        action();
    }

    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        InvokeCount++;
        return action();
    }
}
