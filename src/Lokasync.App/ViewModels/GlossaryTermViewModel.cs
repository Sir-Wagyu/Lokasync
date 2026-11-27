using CommunityToolkit.Mvvm.ComponentModel;

namespace Lokasync.App.ViewModels;

public sealed partial class GlossaryTermViewModel : ObservableObject
{
    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private string _note = string.Empty;

    public GlossaryTermViewModel()
    {
    }

    public GlossaryTermViewModel(string source, string target, string note)
    {
        _source = source;
        _target = target;
        _note = note;
    }
}
