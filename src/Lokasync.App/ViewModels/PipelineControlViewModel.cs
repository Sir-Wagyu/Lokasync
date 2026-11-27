using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lokasync.Adapters;
using Lokasync.App.Services;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Translator.Worker;

namespace Lokasync.App.ViewModels;

public sealed partial class PipelineControlViewModel : ObservableObject
{
    private readonly LocalizationPipelineService _pipelineService;
    private readonly IProjectRepository? _projectRepository;
    private readonly ISubtitleEntryRepository? _subtitleEntryRepository;
    private readonly IEngineFingerprinter _fingerprinter;
    private readonly ProjectSessionState _session;
    private readonly IDispatcherService _dispatcher;
    private readonly IUserNotificationService _notifications;
    private bool _isSyncingSession;
    private string? _cachedSupportedPath;
    private bool _cachedSupportedResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(StartTranslationCommand),
        nameof(PauseTranslationCommand),
        nameof(ResumeTranslationCommand),
        nameof(PatchGameCommand),
        nameof(RestoreOriginalCommand))]
    private string? _projectId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(StartTranslationCommand),
        nameof(PatchGameCommand),
        nameof(RestoreOriginalCommand))]
    private string _gameRootPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(StartTranslationCommand),
        nameof(PauseTranslationCommand),
        nameof(ResumeTranslationCommand),
        nameof(PatchGameCommand),
        nameof(RestoreOriginalCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeTranslationCommand))]
    private bool _isPaused;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeTranslationCommand))]
    private bool _isAutoPaused;

    [ObservableProperty]
    private string? _autoPauseBanner;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusMessage = "Idle";

    public PipelineControlViewModel(
        LocalizationPipelineService pipelineService,
        IProjectRepository? projectRepository = null,
        ISubtitleEntryRepository? subtitleEntryRepository = null,
        IEngineFingerprinter? fingerprinter = null,
        IEnumerable<IEngineAdapter>? adapters = null,
        ProjectSessionState? session = null,
        IDispatcherService? dispatcher = null,
        IUserNotificationService? notifications = null)
    {
        _pipelineService = pipelineService ?? throw new ArgumentNullException(nameof(pipelineService));
        _projectRepository = projectRepository;
        _subtitleEntryRepository = subtitleEntryRepository;
        _fingerprinter = fingerprinter ?? (adapters is not null
            ? new EngineFingerprinter(adapters)
            : new EngineFingerprinter());
        _session = session ?? new ProjectSessionState();
        _dispatcher = dispatcher ?? new WpfDispatcherService();
        _notifications = notifications ?? new WpfUserNotificationService();

        _projectId = _session.ProjectId;
        _gameRootPath = _session.GameRootPath;

        _session.PropertyChanged += OnSessionPropertyChanged;
        _pipelineService.ProgressChanged += OnPipelineProgressChanged;
        _pipelineService.LogMessage += OnPipelineLogMessage;
        _pipelineService.AutoPaused += OnPipelineAutoPaused;
    }

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public RouterProfile ActiveRouterProfile
    {
        get => _session.ActiveRouterProfile;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _session.ActiveRouterProfile = value;
            NotifyProfilePropertiesChanged();
        }
    }

    public string BaseUrl
    {
        get => _session.ActiveRouterProfile.BaseUrl;
        set
        {
            _session.ActiveRouterProfile = _session.ActiveRouterProfile with { BaseUrl = value ?? string.Empty };
            NotifyProfilePropertiesChanged();
        }
    }

    public string ApiKey
    {
        get => _session.ActiveRouterProfile.ApiKey;
        set
        {
            _session.ActiveRouterProfile = _session.ActiveRouterProfile with { ApiKey = value ?? string.Empty };
            NotifyProfilePropertiesChanged();
        }
    }

    public string Model
    {
        get => _session.ActiveRouterProfile.Model;
        set
        {
            _session.ActiveRouterProfile = _session.ActiveRouterProfile with { Model = value ?? string.Empty };
            NotifyProfilePropertiesChanged();
        }
    }

    public int BatchSize
    {
        get => _session.ActiveRouterProfile.BatchSize;
        set
        {
            _session.ActiveRouterProfile = _session.ActiveRouterProfile with { BatchSize = value };
            NotifyProfilePropertiesChanged();
        }
    }

    public event EventHandler? PipelineWorkCompleted;

    partial void OnProjectIdChanged(string? value)
    {
        if (_isSyncingSession)
        {
            return;
        }

        _isSyncingSession = true;
        try
        {
            _session.ProjectId = value;
        }
        finally
        {
            _isSyncingSession = false;
        }
    }

    partial void OnGameRootPathChanged(string value)
    {
        _cachedSupportedPath = null;

        if (_isSyncingSession)
        {
            return;
        }

        _isSyncingSession = true;
        try
        {
            _session.GameRootPath = value;
        }
        finally
        {
            _isSyncingSession = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartTranslationAsync(CancellationToken ct = default)
    {
        await RunOrResumeTranslationAsync(isResume: false, ct);
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void PauseTranslation()
    {
        _pipelineService.RequestPause();
        AppendLog("Pause requested by user.");
        StatusMessage = "Pausing after current batch...";
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeTranslationAsync(CancellationToken ct = default)
    {
        await RunOrResumeTranslationAsync(isResume: true, ct);
    }

    [RelayCommand(CanExecute = nameof(CanPatchGame))]
    private async Task PatchGameAsync(CancellationToken ct = default)
    {
        IsRunning = true;
        ErrorMessage = null;
        StatusMessage = "Backing up & patching game files...";

        try
        {
            await _pipelineService.PatchGameAsync(ProjectId!, GameRootPath, ct);
            StatusMessage = "Patched";
            AppendLog("Game files patched successfully.");
            PipelineWorkCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Patch failed";
            AppendLog($"Patch failed: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestoreOriginal))]
    private async Task RestoreOriginalAsync(CancellationToken ct = default)
    {
        IsRunning = true;
        ErrorMessage = null;
        StatusMessage = "Restoring original game files...";

        try
        {
            await _pipelineService.RestoreOriginalAsync(ProjectId!, GameRootPath, ct);
            StatusMessage = "Restored original files";
            AppendLog("Original game files restored successfully.");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Restore failed";
            AppendLog($"Restore failed: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task RunOrResumeTranslationAsync(bool isResume, CancellationToken ct)
    {
        IsRunning = true;
        IsAutoPaused = false;
        AutoPauseBanner = null;
        IsPaused = false;
        ErrorMessage = null;
        StatusMessage = isResume ? "Resuming translation..." : "Extracting & translating...";

        try
        {
            if (!string.IsNullOrWhiteSpace(GameRootPath))
            {
                await _pipelineService.OpenProjectAsync(GameRootPath, ct);
            }

            TranslationRunResult result = await _pipelineService.RunTranslationAsync(ActiveRouterProfile, ct);
            ApplyRunResult(result);
            PipelineWorkCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            IsPaused = true;
            StatusMessage = "Cancelled";
            AppendLog("Translation run cancelled.");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Error";
            AppendLog($"Translation error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void ApplyRunResult(TranslationRunResult result)
    {
        TotalCount = result.TotalCount;
        CompletedCount = result.CompletedCount;
        ProgressPercentage = ComputeProgressPercentage(result.CompletedCount, result.TotalCount);

        switch (result.Status)
        {
            case TranslationRunStatus.Completed:
                IsPaused = false;
                IsAutoPaused = false;
                AutoPauseBanner = null;
                StatusMessage = result.TotalCount == 0
                    ? "Completed (0 entries found)"
                    : $"Completed ({result.CompletedCount}/{result.TotalCount})";
                break;

            case TranslationRunStatus.AutoPaused:
                IsAutoPaused = true;
                IsPaused = false;
                AutoPauseBanner = $"Auto-Paused (HTTP {(int?)result.AutoPauseStatusCode}): {result.AutoPauseReason ?? "Update router settings/API key and click Resume."}";
                StatusMessage = "Auto-Paused";
                break;

            case TranslationRunStatus.PausedByUser:
                IsPaused = true;
                IsAutoPaused = false;
                StatusMessage = $"Paused ({result.RemainingPendingCount} pending)";
                break;
        }
    }

    private bool CanStart()
    {
        if (IsRunning ||
            string.IsNullOrWhiteSpace(ProjectId) ||
            string.IsNullOrWhiteSpace(GameRootPath))
        {
            return false;
        }

        if (_session.HasSupportedEngine && AreSameGameRootPath(GameRootPath, _session.GameRootPath))
        {
            return true;
        }

        string normalized = NormalizePathForCache(GameRootPath);
        if (!string.Equals(_cachedSupportedPath, normalized, StringComparison.Ordinal))
        {
            _cachedSupportedResult = _fingerprinter.DetectAdapter(GameRootPath) is not null;
            _cachedSupportedPath = normalized;
        }

        return _cachedSupportedResult;
    }

    private bool CanPause() => IsRunning;

    private bool CanResume()
    {
        return !IsRunning &&
               !string.IsNullOrWhiteSpace(ProjectId) &&
               (IsAutoPaused || IsPaused);
    }

    private bool CanPatchGame()
    {
        return !IsRunning &&
               !string.IsNullOrWhiteSpace(ProjectId) &&
               !string.IsNullOrWhiteSpace(GameRootPath);
    }

    private bool CanRestoreOriginal()
    {
        return !IsRunning &&
               !string.IsNullOrWhiteSpace(ProjectId) &&
               !string.IsNullOrWhiteSpace(GameRootPath);
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSyncingSession)
        {
            return;
        }

        _isSyncingSession = true;
        try
        {
            if (e.PropertyName == nameof(ProjectSessionState.ProjectId))
            {
                ProjectId = _session.ProjectId;
            }
            else if (e.PropertyName == nameof(ProjectSessionState.GameRootPath))
            {
                _cachedSupportedPath = null;
                GameRootPath = _session.GameRootPath;
            }
            else if (e.PropertyName == nameof(ProjectSessionState.HasSupportedEngine))
            {
                _cachedSupportedPath = null;
                StartTranslationCommand.NotifyCanExecuteChanged();
            }
            else if (e.PropertyName == nameof(ProjectSessionState.ActiveRouterProfile))
            {
                NotifyProfilePropertiesChanged();
            }
        }
        finally
        {
            _isSyncingSession = false;
        }
    }

    private void OnPipelineProgressChanged(int completed, int total)
    {
        _dispatcher.Invoke(() =>
        {
            CompletedCount = completed;
            TotalCount = total;
            ProgressPercentage = ComputeProgressPercentage(completed, total);
        });
    }

    private void OnPipelineLogMessage(string message)
    {
        AppendLog(message);
    }

    private void OnPipelineAutoPaused(object? sender, TranslationAutoPausedEventArgs e)
    {
        _dispatcher.Invoke(() =>
        {
            IsAutoPaused = true;
            AutoPauseBanner = $"Auto-Paused (HTTP {(int)e.StatusCode}): {e.Reason}";
            CompletedCount = e.CompletedCount;
            TotalCount = e.TotalCount;
            ProgressPercentage = ComputeProgressPercentage(e.CompletedCount, e.TotalCount);
        });
    }

    private void AppendLog(string message)
    {
        _dispatcher.Invoke(() =>
        {
            ActivityLogs.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
        });
    }

    private void NotifyProfilePropertiesChanged()
    {
        OnPropertyChanged(nameof(ActiveRouterProfile));
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(BatchSize));
    }

    private static bool AreSameGameRootPath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            NormalizePathForCache(left),
            NormalizePathForCache(right),
            StringComparison.Ordinal);
    }

    private static string NormalizePathForCache(string path)
    {
        try
        {
            return SqliteConnectionFactory.NormalizeGameRootPath(path);
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }

    private static double ComputeProgressPercentage(int completed, int total)
    {
        if (total <= 0)
        {
            return 0.0;
        }

        double pct = completed * 100.0 / total;
        return double.IsFinite(pct) ? Math.Clamp(pct, 0.0, 100.0) : 0.0;
    }
}
