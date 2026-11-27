using System.Windows;

namespace Lokasync.App.Services;

public sealed class WpfUserNotificationService : IUserNotificationService
{
    public void ShowInfo(string title, string message)
    {
        if (Application.Current is null)
        {
            return;
        }

        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void ShowError(string title, string message)
    {
        if (Application.Current is null)
        {
            return;
        }

        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public bool Confirm(string title, string message)
    {
        if (Application.Current is null)
        {
            return true;
        }

        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes;
    }
}
