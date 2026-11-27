using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lokasync.App.Services;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Sanitizer;

namespace Lokasync.App.ViewModels;

public sealed partial class TranslationGridViewModel : ObservableObject
{
    private readonly ISubtitleEntryRepository _entryRepository;
    private readonly ITextSanitizer _sanitizer;
    private readonly LocalizationPipelineService _pipelineService;
    private readonly ProjectSessionState _session;
    private readonly IDispatcherService _dispatcher;
    private readonly List<SubtitleEntryRowViewModel> _allEntries = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RePatchCommand))]
    private string? _projectId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RePatchCommand))]
    private string _gameRootPath = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _filterFailedValidationOnly;

    [ObservableProperty]
    private bool _filterOverflowWarningOnly;

    [ObservableProperty]
    private SubtitleEntryRowViewModel? _selectedEntry;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public TranslationGridViewModel(
        ISubtitleEntryRepository entryRepository,
        ITextSanitizer sanitizer,
        LocalizationPipelineService pipelineService,
        ProjectSessionState? session = null,
        IDispatcherService? dispatcher = null)
    {
        _entryRepository = entryRepository ?? throw new ArgumentNullException(nameof(entryRepository));
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _pipelineService = pipelineService ?? throw new ArgumentNullException(nameof(pipelineService));
        _session = session ?? new ProjectSessionState();
        _dispatcher = dispatcher ?? new WpfDispatcherService();

        _projectId = _session.ProjectId;
        _gameRootPath = _session.GameRootPath;
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    public ObservableCollection<SubtitleEntryRowViewModel> FilteredEntries { get; } = [];

    public IReadOnlyList<SubtitleEntryRowViewModel> AllEntries => _allEntries;

    public IRelayCommand RefreshCommand => LoadEntriesCommand;

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnFilterFailedValidationOnlyChanged(bool value) => ApplyFilters();
    partial void OnFilterOverflowWarningOnlyChanged(bool value) => ApplyFilters();

    [RelayCommand]
    public async Task LoadEntriesAsync(string? projectId = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            ProjectId = projectId;
        }

        try
        {
            IReadOnlyList<SubtitleEntry> entries = await _entryRepository.GetAllAsync(ct);

            _dispatcher.Invoke(() =>
            {
                _allEntries.Clear();
                foreach (SubtitleEntry entry in entries)
                {
                    _allEntries.Add(new SubtitleEntryRowViewModel(entry));
                }

                ApplyFilters();
                ErrorMessage = null;
                StatusMessage = $"Loaded {_allEntries.Count} entries ({FilteredEntries.Count} shown).";
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                ErrorMessage = ex.Message;
                StatusMessage = "Failed to load entries.";
            });
        }
    }

    public async Task<SubtitleEntryRowViewModel> ApplyManualEditAsync(
        SubtitleEntryRowViewModel row,
        string newText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(newText);

        SubtitleEntry updated = SanitizerValidationHelper.ApplyValidation(
            _sanitizer,
            row.ToModel(),
            newText,
            isManualEdit: true,
            strictPlaceholders: true);

        await _entryRepository.UpdateEntryAsync(updated, ct);

        _dispatcher.Invoke(() =>
        {
            row.UpdateFrom(updated);
            ApplyFilters();
            ErrorMessage = updated.Status == EntryStatus.FailedValidation
                ? $"Validation failed for '{row.EntryKey}': missing or altered placeholder token. Reverted patch target to RawText."
                : null;
            StatusMessage = $"Saved '{row.EntryKey}' ({updated.Status}).";
        });

        return row;
    }

    [RelayCommand]
    private async Task SaveEditAsync(SubtitleEntryRowViewModel? row = null, CancellationToken ct = default)
    {
        SubtitleEntryRowViewModel? target = row ?? SelectedEntry;
        if (target is null)
        {
            return;
        }

        await ApplyManualEditAsync(target, target.EditedText, ct);
    }

    [RelayCommand(CanExecute = nameof(CanRePatch))]
    private async Task RePatchAsync(CancellationToken ct = default)
    {
        ErrorMessage = null;

        try
        {
            await _pipelineService.RePatchAsync(ProjectId!, GameRootPath, ct);
            StatusMessage = "Re-Patch completed successfully (0 LLM calls).";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Re-Patch blocked.";
        }
    }

    private bool CanRePatch()
    {
        return !string.IsNullOrWhiteSpace(ProjectId) &&
               !string.IsNullOrWhiteSpace(GameRootPath);
    }

    private void ApplyFilters()
    {
        string? query = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();

        FilteredEntries.Clear();
        foreach (SubtitleEntryRowViewModel row in _allEntries)
        {
            if (FilterFailedValidationOnly && row.Status != EntryStatus.FailedValidation)
            {
                continue;
            }

            if (FilterOverflowWarningOnly && !row.HasOverflowWarning)
            {
                continue;
            }

            if (query is not null && !MatchesSearch(row, query))
            {
                continue;
            }

            FilteredEntries.Add(row);
        }
    }

    private static bool MatchesSearch(SubtitleEntryRowViewModel row, string query)
    {
        return row.EntryKey.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.SourceFile.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.RawText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (row.TranslatedText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (row.FinalPatchedText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectSessionState.ProjectId))
        {
            ProjectId = _session.ProjectId;
        }
        else if (e.PropertyName == nameof(ProjectSessionState.GameRootPath))
        {
            GameRootPath = _session.GameRootPath;
        }
    }
}
