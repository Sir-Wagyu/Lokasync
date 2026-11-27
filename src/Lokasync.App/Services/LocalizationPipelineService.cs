using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lokasync.Adapters;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Sanitizer;
using Lokasync.Translator;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.RateLimiting;
using Lokasync.Translator.Worker;

namespace Lokasync.App.Services;

public sealed record PipelineProjectState(
    ProjectRecord Project,
    IReadOnlyList<SubtitleEntry> Entries,
    bool HasVerifiedBackup);

public sealed class LocalizationPipelineService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions NarrativeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IEngineFingerprinter _fingerprinter;
    private readonly ITextSanitizer _sanitizer;
    private readonly ITranslatorProvider _translatorProvider;
    private readonly IRouterProfileStore? _profileStore;
    private readonly IProjectSessionFactory? _sessionFactory;
    private readonly ProjectSession? _fixedSession;
    private readonly IDelayProvider? _delayProvider;
    private readonly PromptBuilder? _promptBuilder;
    private readonly TranslationQueueWorker? _injectedWorker;
    private readonly SemaphoreSlim _workerLock = new(1, 1);

    private ProjectSession? _activeSession;
    private ProjectRecord? _activeProject;
    private TranslationQueueWorker? _activeWorker;
    private volatile bool _pauseRequested;

    public LocalizationPipelineService(
        IEngineFingerprinter fingerprinter,
        ITextSanitizer sanitizer,
        ITranslatorProvider translatorProvider,
        IRouterProfileStore profileStore,
        IProjectSessionFactory sessionFactory,
        IDelayProvider? delayProvider = null,
        PromptBuilder? promptBuilder = null)
    {
        _fingerprinter = fingerprinter ?? throw new ArgumentNullException(nameof(fingerprinter));
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _translatorProvider = translatorProvider ?? throw new ArgumentNullException(nameof(translatorProvider));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _delayProvider = delayProvider;
        _promptBuilder = promptBuilder;
    }

    public LocalizationPipelineService(
        IProjectRepository projectRepository,
        ISubtitleEntryRepository entryRepository,
        IBackupManifestRepository manifestRepository,
        IBackupService backupService,
        ITextSanitizer sanitizer,
        ITranslatorProvider translatorProvider,
        IEnumerable<IEngineAdapter>? adapters = null,
        IEngineFingerprinter? fingerprinter = null,
        IRouterProfileStore? profileStore = null,
        ISqliteConnectionFactory? connectionFactory = null,
        IDelayProvider? delayProvider = null,
        PromptBuilder? promptBuilder = null,
        TranslationQueueWorker? queueWorker = null)
    {
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(entryRepository);
        ArgumentNullException.ThrowIfNull(manifestRepository);
        ArgumentNullException.ThrowIfNull(backupService);

        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _translatorProvider = translatorProvider ?? throw new ArgumentNullException(nameof(translatorProvider));
        _fingerprinter = fingerprinter ?? (adapters is not null
            ? new EngineFingerprinter(adapters)
            : new EngineFingerprinter());
        _profileStore = profileStore;
        _delayProvider = delayProvider;
        _promptBuilder = promptBuilder;
        _injectedWorker = queueWorker;

        IAppBackupService appBackup = backupService as IAppBackupService
            ?? new BackupServiceGateAdapter(backupService, manifestRepository);

        _fixedSession = new ProjectSession(
            gameRootPath: string.Empty,
            connectionFactory: connectionFactory ?? new NoOpConnectionFactory(),
            projects: projectRepository,
            entries: entryRepository,
            backupManifests: manifestRepository,
            backupService: appBackup,
            ownsConnectionFactory: false);
        _activeSession = _fixedSession;
    }

    public event EventHandler<TranslationAutoPausedEventArgs>? AutoPaused;

    public event Action<int, int>? ProgressChanged;

    public event Action<string>? LogMessage;

    public ProjectRecord? ActiveProject => _activeProject;

    public string? ActiveGameRootPath => _activeProject?.GameRootPath;

    public async Task<PipelineProjectState> OpenProjectAsync(string gameRootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);

        string fullRoot = Path.GetFullPath(gameRootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Game root directory not found: '{fullRoot}'.");
        }

        ProjectSession session = await EnsureSessionForRootAsync(fullRoot, ct).ConfigureAwait(false);
        string detectedEngine = _fingerprinter.DetectEngineName(fullRoot) ?? "Unknown";
        string computedId = SqliteConnectionFactory.ComputeProjectHash(fullRoot);

        ProjectRecord? existing = await session.Projects.GetByGameRootPathAsync(fullRoot, ct).ConfigureAwait(false)
            ?? await session.Projects.GetByIdAsync(computedId, ct).ConfigureAwait(false);

        ProjectRecord project;
        if (existing is null)
        {
            string folderTitle = new DirectoryInfo(fullRoot).Name;
            NarrativeContext defaultNarrative = new(folderTitle, string.Empty, []);
            string defaultJson = JsonSerializer.Serialize(defaultNarrative, NarrativeJsonOptions);

            project = new ProjectRecord(
                Id: computedId,
                GameTitle: folderTitle,
                GameRootPath: fullRoot,
                DetectedEngine: detectedEngine,
                NarrativeContextJson: defaultJson,
                CreatedAt: DateTimeOffset.UtcNow);

            await session.Projects.UpsertAsync(project, ct).ConfigureAwait(false);
        }
        else
        {
            project = existing;
            if (!string.Equals(detectedEngine, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(existing.DetectedEngine, detectedEngine, StringComparison.Ordinal))
            {
                await session.Projects.UpdateDetectedEngineAsync(existing.Id, detectedEngine, ct).ConfigureAwait(false);
                project = existing with { DetectedEngine = detectedEngine };
            }
        }

        _activeProject = project;
        IReadOnlyList<SubtitleEntry> entries = await session.Entries.GetAllAsync(ct).ConfigureAwait(false);
        bool hasBackup = await session.BackupService.HasVerifiedBackupAsync(project.Id, [], ct).ConfigureAwait(false);

        EmitLog($"Opened project '{project.GameTitle}' (Engine: {project.DetectedEngine}, Entries: {entries.Count}).");
        return new PipelineProjectState(project, entries, hasBackup);
    }

    public async Task SaveNarrativeContextAsync(NarrativeContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProjectRecord project = RequireActiveProject();
        await SaveNarrativeContextAsync(project.Id, context, ct).ConfigureAwait(false);
    }

    public async Task SaveNarrativeContextAsync(string projectId, NarrativeContext context, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(context);

        ProjectSession session = RequireActiveSession();
        string json = JsonSerializer.Serialize(context, NarrativeJsonOptions);

        ProjectRecord? existing = await session.Projects.GetByIdAsync(projectId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            ProjectRecord updated = existing with
            {
                GameTitle = string.IsNullOrWhiteSpace(context.GameTitle) ? existing.GameTitle : context.GameTitle.Trim(),
                NarrativeContextJson = json
            };
            await session.Projects.UpdateAsync(updated, ct).ConfigureAwait(false);
            if (_activeProject?.Id == projectId)
            {
                _activeProject = updated;
            }
        }
        else
        {
            await session.Projects.UpdateNarrativeContextAsync(projectId, json, ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SubtitleEntry>> ExtractAsync(string gameRootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        await EnsureProjectOpenedAsync(gameRootPath, ct).ConfigureAwait(false);
        return await ExtractAndPreparePendingAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubtitleEntry>> ExtractAndPreparePendingAsync(CancellationToken ct = default)
    {
        ProjectSession session = RequireActiveSession();
        ProjectRecord project = RequireActiveProject();
        IEngineAdapter adapter = ResolveAdapter(project.GameRootPath, project.DetectedEngine);

        EmitLog($"Extracting entries using {adapter.EngineName} adapter from '{project.GameRootPath}'...");
        List<SubtitleEntry> extracted = await adapter.ExtractAsync(project.GameRootPath, ct).ConfigureAwait(false);

        List<SubtitleEntry> prepared = new(extracted.Count);
        foreach (SubtitleEntry entry in extracted)
        {
            ct.ThrowIfCancellationRequested();
            (string maskedText, Dictionary<string, string> placeholders) = _sanitizer.Mask(entry.RawText);

            prepared.Add(entry with
            {
                MaskedText = maskedText,
                Placeholders = placeholders,
                TranslatedText = null,
                FinalPatchedText = null,
                Status = EntryStatus.Pending,
                HasOverflowWarning = false
            });
        }

        IReadOnlyList<SubtitleEntry> persisted = await session.Entries.BulkUpsertAsync(prepared, ct).ConfigureAwait(false);
        EmitLog($"Extracted and masked {persisted.Count} subtitle entries as Pending.");
        return persisted;
    }

    public Task<TranslationRunResult> TranslateAsync(
        RouterProfile? overrideProfile = null,
        CancellationToken ct = default)
    {
        return RunTranslationAsync(overrideProfile, ct);
    }

    public async Task<TranslationRunResult> RunTranslationAsync(
        RouterProfile? overrideProfile = null,
        CancellationToken ct = default)
    {
        ProjectSession session = RequireActiveSession();
        ProjectRecord project = RequireActiveProject();

        await _workerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _pauseRequested = false;
        }
        finally
        {
            _workerLock.Release();
        }

        int existingCount = await session.Entries.CountAsync(ct: ct).ConfigureAwait(false);
        if (existingCount == 0)
        {
            await ExtractAndPreparePendingAsync(ct).ConfigureAwait(false);
        }

        RouterProfile profile = overrideProfile ?? await LoadActiveProfileAsync(ct).ConfigureAwait(false);

        ProjectRecord? latestProject = await session.Projects.GetByIdAsync(project.Id, ct).ConfigureAwait(false);
        if (latestProject is not null)
        {
            _activeProject = latestProject;
            project = latestProject;
        }

        NarrativeContext? narrativeContext = DeserializeNarrativeContext(project.NarrativeContextJson);

        TranslationQueueWorker? worker = null;
        bool pausedBeforeStart = false;

        await _workerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pauseRequested)
            {
                pausedBeforeStart = true;
            }
            else
            {
                worker = _injectedWorker ?? new TranslationQueueWorker(
                    _translatorProvider,
                    session.Entries,
                    _sanitizer,
                    _delayProvider,
                    _promptBuilder);

                worker.AutoPaused += OnWorkerAutoPaused;
                worker.ProgressChanged += OnWorkerProgressChanged;
                worker.LogMessage += OnWorkerLogMessage;

                _activeWorker = worker;
            }
        }
        finally
        {
            _workerLock.Release();
        }

        if (pausedBeforeStart)
        {
            int totalCount = await session.Entries.CountAsync(ct: ct).ConfigureAwait(false);
            int pendingCount = (await session.Entries.GetPendingAsync(ct).ConfigureAwait(false)).Count;
            int completedCount = Math.Max(0, totalCount - pendingCount);
            EmitLog($"Translation paused by user before starting batches. Remaining pending: {pendingCount}.");
            return new TranslationRunResult(
                Status: TranslationRunStatus.PausedByUser,
                TotalCount: totalCount,
                CompletedCount: completedCount,
                ProcessedThisRun: 0,
                RemainingPendingCount: pendingCount,
                FailedValidationCount: 0);
        }

        try
        {
            return await worker!.TranslatePendingAsync(narrativeContext, profile, ct).ConfigureAwait(false);
        }
        finally
        {
            await _workerLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_activeWorker, worker))
                {
                    _activeWorker = null;
                }
            }
            finally
            {
                _workerLock.Release();
            }

            worker!.AutoPaused -= OnWorkerAutoPaused;
            worker.ProgressChanged -= OnWorkerProgressChanged;
            worker.LogMessage -= OnWorkerLogMessage;
        }
    }

    public void RequestPause()
    {
        _pauseRequested = true;
        _activeWorker?.RequestPause();
    }

    public async Task PatchGameAsync(string gameRootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        await EnsureProjectOpenedAsync(gameRootPath, ct).ConfigureAwait(false);
        await PatchGameAsync(ct).ConfigureAwait(false);
    }

    public async Task PatchGameAsync(string projectId, string gameRootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);

        await EnsureProjectContextAsync(projectId, gameRootPath, ct).ConfigureAwait(false);
        await PatchGameAsync(ct).ConfigureAwait(false);
    }

    public Task PatchGameAsync(CancellationToken ct = default)
    {
        return PatchGameInternalAsync(requireExistingBackupOnly: false, ct);
    }

    private async Task PatchGameInternalAsync(bool requireExistingBackupOnly, CancellationToken ct)
    {
        ProjectSession session = RequireActiveSession();
        ProjectRecord project = RequireActiveProject();
        IEngineAdapter adapter = ResolveAdapter(project.GameRootPath, project.DetectedEngine);

        IReadOnlyList<SubtitleEntry> currentEntries = await session.Entries.GetAllAsync(ct).ConfigureAwait(false);
        List<SubtitleEntry> validatedEntries = new(currentEntries.Count);
        List<SubtitleEntry> entriesToPersist = [];

        foreach (SubtitleEntry entry in currentEntries)
        {
            SubtitleEntry normalized = NormalizeEntryForPatch(entry);
            validatedEntries.Add(normalized);
            if (!Equals(normalized, entry))
            {
                entriesToPersist.Add(normalized);
            }
        }

        if (entriesToPersist.Count > 0)
        {
            await session.Entries.BulkUpdateAsync(entriesToPersist, ct).ConfigureAwait(false);
        }

        List<string> targetRelativeFiles = validatedEntries
            .Select(e => e.SourceFile)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();

        if (!requireExistingBackupOnly && targetRelativeFiles.Count > 0)
        {
            IReadOnlyList<BackupManifestRecord> existingManifests = await session.BackupService
                .GetManifestsAsync(project.Id, ct)
                .ConfigureAwait(false);

            HashSet<string> backedUpKeys = existingManifests
                .Select(m => m.OriginalRelativePath.Replace('\\', '/').TrimStart('/'))
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            List<string> missingFromManifest = targetRelativeFiles
                .Where(f => !backedUpKeys.Contains(f.Replace('\\', '/').TrimStart('/')))
                .ToList();

            if (missingFromManifest.Count > 0)
            {
                EmitLog($"Creating verified SHA-256 backup for {missingFromManifest.Count} file(s)...");
                await session.BackupService
                    .CreateBackupAsync(project.Id, project.GameRootPath, missingFromManifest, ct)
                    .ConfigureAwait(false);
            }
        }

        await session.BackupService
            .EnsureBackupVerifiedAsync(project.Id, targetRelativeFiles, ct)
            .ConfigureAwait(false);

        EmitLog($"Patching {validatedEntries.Count} entries using {adapter.EngineName} adapter...");
        await adapter.PatchAsync(project.GameRootPath, validatedEntries, ct).ConfigureAwait(false);
        EmitLog("Patching completed successfully.");
    }

    public Task<SubtitleEntry> SaveManualEditAsync(
        long entryId,
        string editedText,
        CancellationToken ct = default)
    {
        return SaveManualEditAsync(entryId, editedText, editedAsMasked: true, ct);
    }

    public async Task<SubtitleEntry> SaveManualEditAsync(
        long entryId,
        string editedText,
        bool editedAsMasked,
        CancellationToken ct = default)
    {
        ProjectSession session = RequireActiveSession();
        SubtitleEntry? existing = await session.Entries.GetByIdAsync(entryId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            throw new KeyNotFoundException($"Subtitle entry with Id={entryId} was not found.");
        }

        return await ApplyManualEditAsync(existing, editedText, editedAsMasked, ct).ConfigureAwait(false);
    }

    public async Task<SubtitleEntry> ApplyManualEditAsync(
        SubtitleEntry entry,
        string editedText,
        bool editedAsMasked = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(editedText);

        ProjectSession session = RequireActiveSession();
        SubtitleEntry updated = ValidateManualEdit(entry, editedText, editedAsMasked);

        await session.Entries.UpdateEntryAsync(updated, ct).ConfigureAwait(false);
        EmitLog($"Saved manual edit for '{updated.EntryKey}' (Status: {updated.Status}, Overflow: {updated.HasOverflowWarning}).");
        return updated;
    }

    public Task RePatchAsync(CancellationToken ct = default)
    {
        EmitLog("Executing instant Re-Patch (preserving existing verified backups, 0 LLM calls)...");
        return PatchGameInternalAsync(requireExistingBackupOnly: true, ct);
    }

    public async Task RePatchAsync(string projectId, string gameRootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);

        await EnsureProjectContextAsync(projectId, gameRootPath, ct).ConfigureAwait(false);
        await RePatchAsync(ct).ConfigureAwait(false);
    }

    public async Task RestoreOriginalAsync(CancellationToken ct = default)
    {
        ProjectSession session = RequireActiveSession();
        ProjectRecord project = RequireActiveProject();

        EmitLog($"Restoring original files for '{project.GameTitle}'...");
        await session.BackupService.RestoreBackupAsync(project.Id, project.GameRootPath, ct).ConfigureAwait(false);
        EmitLog("Original game files restored successfully.");
    }

    public async Task RestoreOriginalAsync(string projectId, string gameRootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);

        ProjectSession session = await EnsureSessionForRootAsync(Path.GetFullPath(gameRootPath), ct).ConfigureAwait(false);
        EmitLog($"Restoring original files for project '{projectId}'...");
        await session.BackupService.RestoreBackupAsync(projectId, Path.GetFullPath(gameRootPath), ct).ConfigureAwait(false);
        EmitLog("Original game files restored successfully.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_activeSession is not null && !ReferenceEquals(_activeSession, _fixedSession))
        {
            await _activeSession.DisposeAsync().ConfigureAwait(false);
            _activeSession = null;
        }

        _workerLock.Dispose();
    }

    private SubtitleEntry ValidateManualEdit(SubtitleEntry entry, string editedText, bool editedAsMasked)
    {
        SubtitleEntry maskedAttempt = SanitizerValidationHelper.ApplyValidation(
            _sanitizer,
            entry,
            editedText,
            isManualEdit: true,
            strictPlaceholders: true);

        if (maskedAttempt.Status == EntryStatus.ManuallyEdited)
        {
            return maskedAttempt;
        }

        if (!editedAsMasked &&
            entry.Placeholders.Count > 0 &&
            !SanitizerValidationHelper.HasDuplicateOrUnexpectedPlaceholders(editedText, null, entry.RawText) &&
            AllOriginalPlaceholderValuesPresent(editedText, entry.Placeholders.Values))
        {
            bool hasOverflow = SanitizerValidationHelper.IsOverflow(entry.RawText, editedText);
            return entry with
            {
                TranslatedText = editedText,
                FinalPatchedText = editedText,
                Status = EntryStatus.ManuallyEdited,
                HasOverflowWarning = hasOverflow
            };
        }

        return maskedAttempt;
    }

    private static bool AllOriginalPlaceholderValuesPresent(string text, IEnumerable<string> values)
    {
        Dictionary<string, int> requiredCounts = new(StringComparer.Ordinal);
        foreach (string val in values)
        {
            if (string.IsNullOrEmpty(val))
            {
                continue;
            }

            requiredCounts.TryGetValue(val, out int current);
            requiredCounts[val] = current + 1;
        }

        foreach ((string val, int requiredCount) in requiredCounts)
        {
            if (CountOccurrences(text, val) < requiredCount)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private SubtitleEntry NormalizeEntryForPatch(SubtitleEntry entry)
    {
        return entry.Status switch
        {
            EntryStatus.Pending or EntryStatus.FailedValidation => entry with
            {
                FinalPatchedText = entry.RawText,
                HasOverflowWarning = false
            },
            EntryStatus.Translated when entry.TranslatedText is null => entry with
            {
                FinalPatchedText = entry.RawText,
                Status = EntryStatus.FailedValidation,
                HasOverflowWarning = false
            },
            EntryStatus.Translated when string.IsNullOrEmpty(entry.FinalPatchedText) =>
                SanitizerValidationHelper.ApplyValidation(_sanitizer, entry, entry.TranslatedText, isManualEdit: false, strictPlaceholders: true),
            EntryStatus.Translated => entry with
            {
                HasOverflowWarning = SanitizerValidationHelper.IsOverflow(entry.RawText, entry.FinalPatchedText)
            },
            EntryStatus.ManuallyEdited => entry with
            {
                FinalPatchedText = entry.FinalPatchedText ?? entry.RawText,
                HasOverflowWarning = SanitizerValidationHelper.IsOverflow(
                    entry.RawText,
                    entry.FinalPatchedText ?? entry.RawText)
            },
            _ => entry with
            {
                FinalPatchedText = entry.RawText,
                HasOverflowWarning = false
            }
        };
    }

    private async Task EnsureProjectOpenedAsync(string gameRootPath, CancellationToken ct)
    {
        string normalized = SqliteConnectionFactory.NormalizeGameRootPath(gameRootPath);
        if (_activeProject is not null &&
            string.Equals(
                SqliteConnectionFactory.NormalizeGameRootPath(_activeProject.GameRootPath),
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        await OpenProjectAsync(gameRootPath, ct).ConfigureAwait(false);
    }

    private async Task EnsureProjectContextAsync(string projectId, string gameRootPath, CancellationToken ct)
    {
        string fullRoot = Path.GetFullPath(gameRootPath);
        ProjectSession session = await EnsureSessionForRootAsync(fullRoot, ct).ConfigureAwait(false);

        ProjectRecord? existing = await session.Projects.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? await session.Projects.GetByGameRootPathAsync(fullRoot, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            _activeProject = existing;
            return;
        }

        await OpenProjectAsync(fullRoot, ct).ConfigureAwait(false);
    }

    private async Task<ProjectSession> EnsureSessionForRootAsync(string fullRoot, CancellationToken ct)
    {
        if (_fixedSession is not null)
        {
            _activeSession = _fixedSession;
            return _fixedSession;
        }

        if (_sessionFactory is null)
        {
            throw new InvalidOperationException("No project session factory is configured.");
        }

        if (_activeSession is not null &&
            string.Equals(
                SqliteConnectionFactory.NormalizeGameRootPath(_activeSession.GameRootPath),
                SqliteConnectionFactory.NormalizeGameRootPath(fullRoot),
                StringComparison.Ordinal))
        {
            return _activeSession;
        }

        if (_activeSession is not null)
        {
            await _activeSession.DisposeAsync().ConfigureAwait(false);
            _activeSession = null;
        }

        _activeSession = await _sessionFactory.OpenAsync(fullRoot, ct).ConfigureAwait(false);
        return _activeSession;
    }

    private ProjectSession RequireActiveSession()
    {
        return _activeSession ?? throw new InvalidOperationException("No active project session. Call OpenProjectAsync first.");
    }

    private ProjectRecord RequireActiveProject()
    {
        return _activeProject ?? throw new InvalidOperationException("No active project loaded. Call OpenProjectAsync first.");
    }

    private IEngineAdapter ResolveAdapter(string gameRootPath, string? detectedEngine)
    {
        if (!string.IsNullOrWhiteSpace(detectedEngine) &&
            _fingerprinter.TryGetAdapterByName(detectedEngine, out IEngineAdapter? namedAdapter) &&
            namedAdapter.CanHandle(gameRootPath))
        {
            return namedAdapter;
        }

        IEngineAdapter? detected = _fingerprinter.DetectAdapter(gameRootPath);
        return detected ?? throw new InvalidOperationException(
            $"No supported game engine adapter found for '{gameRootPath}'.");
    }

    private async Task<RouterProfile> LoadActiveProfileAsync(CancellationToken ct)
    {
        if (_profileStore is null)
        {
            return RouterProfileDefaults.CreateDefaultProfile();
        }

        RouterProfilesConfig config = await _profileStore.LoadAsync(ct).ConfigureAwait(false);
        return config.ResolveActiveProfile();
    }

    private static NarrativeContext? DeserializeNarrativeContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NarrativeContext>(json, NarrativeJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void OnWorkerAutoPaused(object? sender, TranslationAutoPausedEventArgs e)
    {
        AutoPaused?.Invoke(this, e);
    }

    private void OnWorkerProgressChanged(int completed, int total)
    {
        ProgressChanged?.Invoke(completed, total);
    }

    private void OnWorkerLogMessage(string message)
    {
        EmitLog(message);
    }

    private void EmitLog(string message)
    {
        LogMessage?.Invoke(message);
    }

    private sealed class BackupServiceGateAdapter(
        IBackupService inner,
        IBackupManifestRepository manifestRepository) : IAppBackupService
    {
        public Task CreateBackupAsync(
            string projectId,
            string gameRootPath,
            IEnumerable<string> relativeFilePaths,
            CancellationToken ct)
        {
            return inner.CreateBackupAsync(projectId, gameRootPath, relativeFilePaths, ct);
        }

        public Task RestoreBackupAsync(string projectId, string gameRootPath, CancellationToken ct)
        {
            return inner.RestoreBackupAsync(projectId, gameRootPath, ct);
        }

        public Task<IReadOnlyList<BackupManifestRecord>> GetManifestsAsync(
            string projectId,
            CancellationToken ct = default)
        {
            return manifestRepository.GetByProjectIdAsync(projectId, ct);
        }

        public async Task<bool> HasVerifiedBackupAsync(
            string projectId,
            IEnumerable<string> relativeFilePaths,
            CancellationToken ct = default)
        {
            IReadOnlyList<BackupManifestRecord> manifests = await manifestRepository
                .GetByProjectIdAsync(projectId, ct)
                .ConfigureAwait(false);

            if (manifests.Count == 0)
            {
                return false;
            }

            Dictionary<string, BackupManifestRecord> map = manifests.ToDictionary(
                m => m.OriginalRelativePath.Replace('\\', '/').TrimStart('/'),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            List<string> required = relativeFilePaths
                .Select(p => p.Replace('\\', '/').TrimStart('/'))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToList();

            if (required.Count == 0)
            {
                required = map.Keys.ToList();
            }

            foreach (string rel in required)
            {
                if (!map.TryGetValue(rel, out BackupManifestRecord? manifest))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(manifest.BackupFilePath) || !File.Exists(manifest.BackupFilePath))
                {
                    return false;
                }

                await using FileStream stream = new(
                    manifest.BackupFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    useAsync: true);
                byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
                string hex = Convert.ToHexString(hash).ToLowerInvariant();
                if (!string.Equals(hex, manifest.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        public async Task EnsureBackupVerifiedAsync(
            string projectId,
            IEnumerable<string> relativeFilePaths,
            CancellationToken ct = default)
        {
            if (!await HasVerifiedBackupAsync(projectId, relativeFilePaths, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Backup verification failed for project '{projectId}'. CreateBackupAsync must complete before PatchAsync.");
            }
        }
    }

    private sealed class NoOpConnectionFactory : ISqliteConnectionFactory
    {
        public string ConnectionString => string.Empty;
        public bool IsInMemory => true;

        public Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("Connection factory was not supplied.");

        public Task InitializeSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
