using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Providers;

namespace Lokasync.App.ViewModels;

public sealed partial class GlossaryViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IProjectRepository _projectRepository;
    private readonly ProjectSessionState _session;
    private readonly ISubtitleEntryRepository? _entryRepository;
    private readonly IRouterProfileStore? _profileStore;
    private readonly INarrativeContextGenerator? _contextGenerator;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SubtitleEntry>>>? _extractEntriesFallback;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGlossaryCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoGenerateCommand))]
    private string? _projectId;

    [ObservableProperty]
    private string _gameTitle = string.Empty;

    [ObservableProperty]
    private string _genreTone = string.Empty;

    [ObservableProperty]
    private string _newTermSource = string.Empty;

    [ObservableProperty]
    private string _newTermTarget = string.Empty;

    [ObservableProperty]
    private string _newTermNote = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGlossaryCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoGenerateCommand))]
    private bool _isGenerating;

    public GlossaryViewModel(
        IProjectRepository projectRepository,
        ProjectSessionState? session = null,
        ISubtitleEntryRepository? entryRepository = null,
        IRouterProfileStore? profileStore = null,
        INarrativeContextGenerator? contextGenerator = null,
        Func<CancellationToken, Task<IReadOnlyList<SubtitleEntry>>>? extractEntriesFallback = null)
    {
        _projectRepository = projectRepository ?? throw new ArgumentNullException(nameof(projectRepository));
        _session = session ?? new ProjectSessionState();
        _entryRepository = entryRepository;
        _profileStore = profileStore;
        _contextGenerator = contextGenerator;
        _extractEntriesFallback = extractEntriesFallback;
        _session.PropertyChanged += OnSessionPropertyChanged;

        if (!string.IsNullOrWhiteSpace(_session.ProjectId))
        {
            _projectId = _session.ProjectId;
            _gameTitle = _session.GameTitle;
        }
    }

    public ObservableCollection<GlossaryTermViewModel> GlossaryTerms { get; } = [];

    [RelayCommand]
    public async Task LoadProjectAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            ProjectId = null;
            GameTitle = string.Empty;
            GenreTone = string.Empty;
            GlossaryTerms.Clear();
            return;
        }

        try
        {
            ProjectId = projectId;
            ProjectRecord? project = await _projectRepository.GetByIdAsync(projectId, ct);
            if (project is null)
            {
                ErrorMessage = $"Project '{projectId}' not found.";
                return;
            }

            NarrativeContext? context = TryDeserializeContext(project.NarrativeContextJson);
            GameTitle = !string.IsNullOrWhiteSpace(context?.GameTitle)
                ? context.GameTitle
                : project.GameTitle;
            GenreTone = context?.GenreTone ?? string.Empty;

            GlossaryTerms.Clear();
            if (context?.Glossary is not null)
            {
                foreach (GlossaryTerm term in context.Glossary)
                {
                    if (term is null)
                    {
                        continue;
                    }

                    GlossaryTerms.Add(new GlossaryTermViewModel(term.Source, term.Target, term.Note));
                }
            }

            ErrorMessage = null;
            StatusMessage = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void AddTerm()
    {
        if (string.IsNullOrWhiteSpace(NewTermSource) || string.IsNullOrWhiteSpace(NewTermTarget))
        {
            ErrorMessage = "Both Source and Target terms are required.";
            return;
        }

        string source = NewTermSource.Trim();
        string target = NewTermTarget.Trim();
        string note = NewTermNote?.Trim() ?? string.Empty;

        if (GlossaryTerms.Any(t => string.Equals(t.Source, source, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = $"A glossary term for '{source}' already exists.";
            return;
        }

        GlossaryTerms.Add(new GlossaryTermViewModel(source, target, note));
        NewTermSource = string.Empty;
        NewTermTarget = string.Empty;
        NewTermNote = string.Empty;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void RemoveTerm(GlossaryTermViewModel? term)
    {
        if (term is null)
        {
            return;
        }

        GlossaryTerms.Remove(term);
        ErrorMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanSaveGlossary))]
    private async Task SaveGlossaryAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ProjectId))
        {
            return;
        }

        NarrativeContext context = new(
            GameTitle: GameTitle?.Trim() ?? string.Empty,
            GenreTone: GenreTone?.Trim() ?? string.Empty,
            Glossary: GlossaryTerms
                .Select(t => new GlossaryTerm(t.Source.Trim(), t.Target.Trim(), t.Note?.Trim() ?? string.Empty))
                .ToArray());

        string json = JsonSerializer.Serialize(context, JsonOptions);

        ProjectRecord? existing = await _projectRepository.GetByIdAsync(ProjectId, ct);
        if (existing is not null)
        {
            ProjectRecord updated = existing with
            {
                GameTitle = string.IsNullOrWhiteSpace(context.GameTitle) ? existing.GameTitle : context.GameTitle,
                NarrativeContextJson = json
            };
            await _projectRepository.UpdateAsync(updated, ct);
        }
        else
        {
            await _projectRepository.UpdateNarrativeContextAsync(ProjectId, json, ct);
        }

        ErrorMessage = null;
        StatusMessage = "Narrative context and glossary saved.";
    }

    [RelayCommand(CanExecute = nameof(CanAutoGenerate))]
    private async Task AutoGenerateAsync(CancellationToken ct = default)
    {
        if (!CanAutoGenerate())
        {
            return;
        }

        IsGenerating = true;
        ErrorMessage = null;
        StatusMessage = "Sampling game text and analyzing tone & glossary with LLM...";

        try
        {
            IReadOnlyList<SubtitleEntry> entries = await _entryRepository!.GetAllAsync(ct);
            if (entries.Count == 0 && _extractEntriesFallback is not null)
            {
                StatusMessage = "Extracting game text first before analyzing tone & glossary...";
                entries = await _extractEntriesFallback(ct);
                StatusMessage = "Analyzing extracted text samples with LLM...";
            }

            IReadOnlyList<string> samples = NarrativeSampleSelector.SelectRepresentativeSamples(entries);
            if (samples.Count == 0)
            {
                ErrorMessage = "No dialogue entries available to analyze. Extract the game first.";
                StatusMessage = null;
                return;
            }

            RouterProfilesConfig config = await _profileStore!.LoadAsync(ct);
            RouterProfile profile = config.ResolveActiveProfile();

            string effectiveTitle = !string.IsNullOrWhiteSpace(GameTitle)
                ? GameTitle.Trim()
                : _session.GameTitle;

            NarrativeContext generated = await _contextGenerator!.GenerateAsync(
                effectiveTitle,
                samples,
                profile,
                ct);

            if (!string.IsNullOrWhiteSpace(generated.GenreTone))
            {
                GenreTone = generated.GenreTone.Trim();
            }

            HashSet<string> existingSources = new(
                GlossaryTerms.Select(t => t.Source.Trim()),
                StringComparer.OrdinalIgnoreCase);

            int addedCount = 0;
            foreach (GlossaryTerm term in generated.Glossary)
            {
                if (string.IsNullOrWhiteSpace(term.Source) || string.IsNullOrWhiteSpace(term.Target))
                {
                    continue;
                }

                string source = term.Source.Trim();
                if (!existingSources.Add(source))
                {
                    continue;
                }

                GlossaryTerms.Add(new GlossaryTermViewModel(source, term.Target.Trim(), term.Note?.Trim() ?? string.Empty));
                addedCount++;
            }

            StatusMessage = $"Auto-generated tone and added {addedCount} glossary term(s) from {samples.Count} sampled lines. Review and click 'Save Narrative Context & Glossary' to apply.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
            throw;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Auto-generate failed: {ex.Message}";
            StatusMessage = null;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private bool CanSaveGlossary() => !IsGenerating && !string.IsNullOrWhiteSpace(ProjectId);

    private bool CanAutoGenerate() =>
        !IsGenerating &&
        !string.IsNullOrWhiteSpace(ProjectId) &&
        _entryRepository is not null &&
        _profileStore is not null &&
        _contextGenerator is not null;

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectSessionState.ProjectId))
        {
            ProjectId = _session.ProjectId;
            if (string.IsNullOrWhiteSpace(_session.ProjectId))
            {
                GameTitle = string.Empty;
                GenreTone = string.Empty;
                GlossaryTerms.Clear();
            }
        }
        else if (e.PropertyName == nameof(ProjectSessionState.GameTitle) && string.IsNullOrWhiteSpace(GameTitle))
        {
            GameTitle = _session.GameTitle;
        }
    }

    private static NarrativeContext? TryDeserializeContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NarrativeContext>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
