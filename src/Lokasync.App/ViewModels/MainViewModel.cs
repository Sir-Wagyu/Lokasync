using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lokasync.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(
        ProjectDashboardViewModel dashboard,
        RouterProfileViewModel routerProfile,
        GlossaryViewModel glossary,
        PipelineControlViewModel pipeline,
        TranslationGridViewModel translationGrid,
        ProjectSessionState? session = null)
    {
        Dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        RouterProfile = routerProfile ?? throw new ArgumentNullException(nameof(routerProfile));
        Glossary = glossary ?? throw new ArgumentNullException(nameof(glossary));
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        TranslationGrid = translationGrid ?? throw new ArgumentNullException(nameof(translationGrid));
        Session = session ?? new ProjectSessionState();

        Session.PropertyChanged += OnSessionPropertyChanged;
        Pipeline.PipelineWorkCompleted += OnPipelineWorkCompleted;
    }

    public ProjectSessionState Session { get; }
    public ProjectDashboardViewModel Dashboard { get; }
    public RouterProfileViewModel RouterProfile { get; }
    public GlossaryViewModel Glossary { get; }
    public PipelineControlViewModel Pipeline { get; }
    public TranslationGridViewModel TranslationGrid { get; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await RouterProfile.LoadProfilesCommand.ExecuteAsync(null);
    }

    private async void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectSessionState.ProjectId))
        {
            return;
        }

        string? projectId = Session.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        try
        {
            await Glossary.LoadProjectAsync(projectId);
        }
        catch (Exception ex)
        {
            Glossary.ErrorMessage = ex.Message;
        }

        try
        {
            await TranslationGrid.LoadEntriesAsync(projectId);
        }
        catch (Exception ex)
        {
            TranslationGrid.ErrorMessage = ex.Message;
        }
    }

    private async void OnPipelineWorkCompleted(object? sender, EventArgs e)
    {
        string? projectId = Session.ProjectId ?? Pipeline.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        try
        {
            await TranslationGrid.LoadEntriesAsync(projectId);
        }
        catch (Exception ex)
        {
            TranslationGrid.ErrorMessage = ex.Message;
        }
    }
}
