namespace Lokasync.App.Services;

public interface IUserNotificationService
{
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
    bool Confirm(string title, string message);
}
