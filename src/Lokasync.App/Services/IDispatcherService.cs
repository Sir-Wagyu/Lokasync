namespace Lokasync.App.Services;

public interface IDispatcherService
{
    void Invoke(Action action);
    Task InvokeAsync(Func<Task> action);
}
