using System.IO;
using Microsoft.Win32;

namespace Lokasync.App.Services;

public sealed class WpfFolderPickerService : IFolderPickerService
{
    public string? PickFolder(string? initialDirectory = null, string? title = null)
    {
        OpenFolderDialog dialog = new()
        {
            Title = title ?? "Select Game Root Folder",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
