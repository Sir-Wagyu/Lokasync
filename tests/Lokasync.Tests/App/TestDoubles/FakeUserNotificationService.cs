using Lokasync.App.Services;

namespace Lokasync.Tests.App.TestDoubles;

internal sealed class FakeUserNotificationService : IUserNotificationService
{
    public List<(string Title, string Message)> InfoMessages { get; } = [];
    public List<(string Title, string Message)> ErrorMessages { get; } = [];
    public List<(string Title, string Message)> ConfirmPrompts { get; } = [];
    public bool NextConfirmResult { get; set; } = true;

    public void ShowInfo(string title, string message) => InfoMessages.Add((title, message));

    public void ShowError(string title, string message) => ErrorMessages.Add((title, message));

    public bool Confirm(string title, string message)
    {
        ConfirmPrompts.Add((title, message));
        return NextConfirmResult;
    }
}
