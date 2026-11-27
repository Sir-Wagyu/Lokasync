using System.IO;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lokasync.Adapters;
using Lokasync.App.Services;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;

namespace Lokasync.App.ViewModels;

public sealed partial class ProjectDashboardViewModel : ObservableObject
{
    private readonly IProjectRepository _projectRepository;
    private readonly IEngineFingerprinter _fingerprinter;
    private readonly ProjectSessionState _session;
    private readonly IFolderPickerService _folderPicker;
    private readonly IUserNotificationService _notifications;
    private readonly PipelineControlViewModel? _pipeline;
    private readonly RelayCommand _fallbackStartCommand;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadProjectCommand))]
    private string _gameRootPath = string.Empty;

    [ObservableProperty]
    private string _detectedEngine = string.Empty;

    [ObservableProperty]
    private string _gameTitle = string.Empty;

    [ObservableProperty]
    private string? _projectId;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasSupportedEngine;

    public ProjectDashboardViewModel(
        IProjectRepository projectRepository,
        IEngineFingerprinter? fingerprinter = null,
        IEnumerable<IEngineAdapter>? adapters = null,
        ProjectSessionState? session = null,
        IFolderPickerService? folderPicker = null,
        IUserNotificationService? notifications = null,
        PipelineControlViewModel? pipeline = null)
    {
        _projectRepository = projectRepository ?? throw new ArgumentNullException(nameof(projectRepository));
        _fingerprinter = fingerprinter ?? (adapters is not null
            ? new EngineFingerprinter(adapters)
            : new EngineFingerprinter());
        _session = session ?? new ProjectSessionState();
        _folderPicker = folderPicker ?? new WpfFolderPickerService();
        _notifications = notifications ?? new WpfUserNotificationService();
        _pipeline = pipeline;
        _fallbackStartCommand = new RelayCommand(
            () => { },
            () => HasSupportedEngine && !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(GameRootPath));
    }

    public ICommand StartTranslationCommand => (ICommand?)_pipeline?.StartTranslationCommand ?? _fallbackStartCommand;

    partial void OnGameRootPathChanged(string value)
    {
        _session.GameRootPath = value;
        _fallbackStartCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasSupportedEngineChanged(bool value)
    {
        _session.HasSupportedEngine = value;
        _fallbackStartCommand.NotifyCanExecuteChanged();
    }

    partial void OnProjectIdChanged(string? value)
    {
        _session.ProjectId = value;
        _fallbackStartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task BrowseFolderAsync(CancellationToken ct = default)
    {
        string? initialDir = !string.IsNullOrWhiteSpace(GameRootPath) && Directory.Exists(GameRootPath)
            ? GameRootPath
            : null;

        string? selected = _folderPicker.PickFolder(initialDir, "Select Game Root Folder");
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        GameRootPath = selected;
        await LoadProjectAsync(selected, ct);
    }

    [RelayCommand]
    private async Task LoadProjectAsync(string? path = null, CancellationToken ct = default)
    {
        string targetPath = string.IsNullOrWhiteSpace(path) ? GameRootPath : path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            GameRootPath = path;
        }

        if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
        {
            ResetProjectState("Selected game folder does not exist.");
            return;
        }

        string fullPath = Path.GetFullPath(targetPath);
        GameRootPath = fullPath;

        IEngineAdapter? adapter = _fingerprinter.DetectAdapter(fullPath);
        if (adapter is null)
        {
            DetectedEngine = "Unsupported / None";
            HasSupportedEngine = false;
            ProjectId = null;
            GameTitle = string.Empty;
            ErrorMessage = "No supported game engine detected in folder.";
            SyncSession();
            return;
        }

        DetectedEngine = adapter.EngineName;
        HasSupportedEngine = true;
        ErrorMessage = null;

        string computedId = SqliteConnectionFactory.ComputeProjectHash(fullPath);
        ProjectRecord? existing = await _projectRepository.GetByGameRootPathAsync(fullPath, ct)
            ?? await _projectRepository.GetByIdAsync(computedId, ct);

        ProjectRecord project;
        if (existing is null)
        {
            string title = new DirectoryInfo(fullPath).Name;
            string defaultContext = JsonSerializer.Serialize(new NarrativeContext(title, string.Empty, []));

            project = new ProjectRecord(
                Id: computedId,
                GameTitle: title,
                GameRootPath: fullPath,
                DetectedEngine: adapter.EngineName,
                NarrativeContextJson: defaultContext,
                CreatedAt: DateTimeOffset.UtcNow);

            await _projectRepository.UpsertAsync(project, ct);
        }
        else
        {
            project = existing;
            if (!string.Equals(existing.DetectedEngine, adapter.EngineName, StringComparison.Ordinal))
            {
                await _projectRepository.UpdateDetectedEngineAsync(existing.Id, adapter.EngineName, ct);
                project = existing with { DetectedEngine = adapter.EngineName };
            }
        }

        ProjectId = project.Id;
        GameTitle = project.GameTitle;
        SyncSession();
    }

    private void ResetProjectState(string errorMessage)
    {
        DetectedEngine = string.Empty;
        HasSupportedEngine = false;
        ProjectId = null;
        GameTitle = string.Empty;
        ErrorMessage = errorMessage;
        SyncSession();
    }

    private void SyncSession()
    {
        _session.ProjectId = ProjectId;
        _session.GameRootPath = GameRootPath;
        _session.GameTitle = GameTitle;
        _session.DetectedEngine = DetectedEngine;
        _session.HasSupportedEngine = HasSupportedEngine;
        _fallbackStartCommand.NotifyCanExecuteChanged();
    }
}
