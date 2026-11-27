namespace Lokasync.App.Services;

public interface IFolderPickerService
{
    string? PickFolder(string? initialDirectory = null, string? title = null);
}
