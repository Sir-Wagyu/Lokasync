using System.Windows;
using System.Windows.Threading;

namespace Lokasync.App.Services;

public sealed class WpfDispatcherService : IDispatcherService
{
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}
