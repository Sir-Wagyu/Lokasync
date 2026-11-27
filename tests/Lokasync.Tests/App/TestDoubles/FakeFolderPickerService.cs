using Lokasync.App.Services;

namespace Lokasync.Tests.App.TestDoubles;

internal sealed class FakeFolderPickerService : IFolderPickerService
{
    private readonly Queue<string?> _responses = new();

    public List<(string? InitialDirectory, string? Title)> Calls { get; } = [];

    public void EnqueueResult(string? folderPath) => _responses.Enqueue(folderPath);

    public string? PickFolder(string? initialDirectory = null, string? title = null)
    {
        Calls.Add((initialDirectory, title));
        return _responses.Count > 0 ? _responses.Dequeue() : null;
    }
}
