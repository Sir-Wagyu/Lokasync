using System.Net;
using System.Text.RegularExpressions;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Exceptions;
using Lokasync.Translator.Providers;
using Lokasync.Translator.RateLimiting;

namespace Lokasync.Translator.Worker;

public sealed partial class TranslationQueueWorker(
    ITranslatorProvider translatorProvider,
    ISubtitleEntryRepository entryRepository,
    ITextSanitizer? sanitizer = null,
    IDelayProvider? delayProvider = null,
    PromptBuilder? promptBuilder = null,
    Random? random = null)
{
    private readonly ITranslatorProvider _translatorProvider = translatorProvider ?? throw new ArgumentNullException(nameof(translatorProvider));
    private readonly ISubtitleEntryRepository _entryRepository = entryRepository ?? throw new ArgumentNullException(nameof(entryRepository));
    private readonly ITextSanitizer? _sanitizer = sanitizer;
    private readonly IDelayProvider _delayProvider = delayProvider ?? new SystemDelayProvider();
    private readonly PromptBuilder _promptBuilder = promptBuilder ?? new PromptBuilder();
    private readonly Random _random = random ?? Random.Shared;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private volatile bool _pauseRequested;

    public event EventHandler<TranslationAutoPausedEventArgs>? AutoPaused;

    public event Action<int, int>? ProgressChanged;

    public event Action<string>? LogMessage;

    public void RequestPause()
    {
        _pauseRequested = true;
    }

    public Task<TranslationRunResult> TranslatePendingAsync(
        NarrativeContext? narrativeContext,
        RouterProfile profile,
        CancellationToken ct = default)
    {
        string systemPrompt = _promptBuilder.BuildSystemPrompt(narrativeContext);
        return TranslatePendingAsync(systemPrompt, profile, ct);
    }

    public async Task<TranslationRunResult> TranslatePendingAsync(
        string systemPrompt,
        RouterProfile profile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        RouterProfileValidator.Validate(profile);

        // Enforce strict sequential execution (MaxConcurrency = 1) per PRD FR-3.
        RouterProfile sequentialProfile = profile with { MaxConcurrency = 1 };

        await _runLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int totalCount = await _entryRepository.CountAsync(ct: ct).ConfigureAwait(false);
            IReadOnlyList<SubtitleEntry> pendingEntries = await _entryRepository.GetPendingAsync(ct).ConfigureAwait(false);

            int completedCount = Math.Max(0, totalCount - pendingEntries.Count);
            int processedThisRun = 0;
            int failedValidationCount = 0;

            if (pendingEntries.Count == 0)
            {
                LogMessage?.Invoke("No pending subtitle entries to translate.");
                return new TranslationRunResult(
                    Status: TranslationRunStatus.Completed,
                    TotalCount: totalCount,
                    CompletedCount: completedCount,
                    ProcessedThisRun: 0,
                    RemainingPendingCount: 0);
            }

            List<SubtitleEntry[]> batches = [.. pendingEntries.Chunk(sequentialProfile.BatchSize)];
            LogMessage?.Invoke($"Starting sequential translation of {pendingEntries.Count} pending entries in {batches.Count} batch(es).");

            for (int i = 0; i < batches.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (_pauseRequested)
                {
                    int remainingOnPause = Math.Max(0, pendingEntries.Count - processedThisRun);
                    LogMessage?.Invoke($"Translation paused by user. Remaining pending: {remainingOnPause}.");
                    return new TranslationRunResult(
                        Status: TranslationRunStatus.PausedByUser,
                        TotalCount: totalCount,
                        CompletedCount: completedCount,
                        ProcessedThisRun: processedThisRun,
                        RemainingPendingCount: remainingOnPause,
                        FailedValidationCount: failedValidationCount);
                }

                SubtitleEntry[] batch = batches[i];
                Dictionary<string, SubtitleEntry> entryLookup = batch.ToDictionary(e => e.EntryKey, StringComparer.Ordinal);
                Dictionary<string, string> maskedBatch = batch.ToDictionary(e => e.EntryKey, e => e.MaskedText, StringComparer.Ordinal);

                try
                {
                    Task<Dictionary<string, string>> SendChunkAsync(IReadOnlyDictionary<string, string> chunk, CancellationToken token)
                    {
                        if (_translatorProvider is OpenAiTranslatorProvider openAi)
                        {
                            return openAi.SendSingleBatchAsync(chunk, systemPrompt, sequentialProfile, token);
                        }

                        Dictionary<string, string> copy = new(chunk, StringComparer.Ordinal);
                        return _translatorProvider.TranslateBatchAsync(copy, systemPrompt, sequentialProfile, token);
                    }

                    async Task OnChunkSucceededAsync(IReadOnlyDictionary<string, string> chunkResult, CancellationToken _)
                    {
                        List<SubtitleEntry> updatedEntries = new(chunkResult.Count);
                        int chunkFailed = 0;

                        foreach ((string key, string translatedMasked) in chunkResult)
                        {
                            if (!entryLookup.TryGetValue(key, out SubtitleEntry? original))
                            {
                                continue;
                            }

                            SubtitleEntry updated = MapTranslatedEntry(original, translatedMasked);
                            if (updated.Status == EntryStatus.FailedValidation)
                            {
                                chunkFailed++;
                            }

                            updatedEntries.Add(updated);
                        }

                        if (updatedEntries.Count > 0)
                        {
                            // Commit completed SQLite checkpoints with CancellationToken.None so completed work is preserved.
                            await _entryRepository.BulkUpdateAsync(updatedEntries, CancellationToken.None).ConfigureAwait(false);
                            completedCount += updatedEntries.Count;
                            processedThisRun += updatedEntries.Count;
                            failedValidationCount += chunkFailed;

                            ProgressChanged?.Invoke(completedCount, totalCount);
                            LogMessage?.Invoke($"Checkpointed {updatedEntries.Count} entries ({completedCount}/{totalCount}).");
                        }
                    }

                    Task DelayBetweenSplitRetriesAsync(CancellationToken token)
                    {
                        TimeSpan retryDelay = RateLimitDelayCalculator.ComputeDelay(
                            sequentialProfile.DelayBetweenBatchesMs,
                            sequentialProfile.JitterMs,
                            _random);
                        return _delayProvider.DelayAsync(retryDelay, token);
                    }

                    BatchSplitRetryResult batchResult = await BatchSplitRetryExecutor.ExecuteAsync(
                        maskedBatch,
                        SendChunkAsync,
                        OnChunkSucceededAsync,
                        DelayBetweenSplitRetriesAsync,
                        ct).ConfigureAwait(false);

                    if (batchResult.FailedKeys.Count > 0)
                    {
                        List<SubtitleEntry> failedEntries = new(batchResult.FailedKeys.Count);
                        foreach (string failedKey in batchResult.FailedKeys)
                        {
                            if (!entryLookup.TryGetValue(failedKey, out SubtitleEntry? original))
                            {
                                continue;
                            }

                            failedEntries.Add(original with
                            {
                                FinalPatchedText = original.RawText,
                                Status = EntryStatus.FailedValidation,
                                HasOverflowWarning = false
                            });
                        }

                        if (failedEntries.Count > 0)
                        {
                            await _entryRepository.BulkUpdateAsync(failedEntries, CancellationToken.None).ConfigureAwait(false);
                            completedCount += failedEntries.Count;
                            processedThisRun += failedEntries.Count;
                            failedValidationCount += failedEntries.Count;

                            ProgressChanged?.Invoke(completedCount, totalCount);
                            LogMessage?.Invoke($"Marked {failedEntries.Count} entry/entries as FailedValidation after split-retry limit.");
                        }
                    }
                }
                catch (TranslatorPauseException ex)
                {
                    return HandleAutoPause(
                        ex.StatusCode,
                        ex.Message,
                        ex.RetryAfter,
                        totalCount,
                        completedCount,
                        processedThisRun,
                        pendingEntries.Count,
                        failedValidationCount);
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized
                                                    or HttpStatusCode.Forbidden
                                                    or HttpStatusCode.TooManyRequests)
                {
                    return HandleAutoPause(
                        ex.StatusCode.Value,
                        ex.Message,
                        retryAfter: null,
                        totalCount,
                        completedCount,
                        processedThisRun,
                        pendingEntries.Count,
                        failedValidationCount);
                }

                if (i < batches.Count - 1)
                {
                    if (_pauseRequested)
                    {
                        int remainingOnPause = Math.Max(0, pendingEntries.Count - processedThisRun);
                        LogMessage?.Invoke($"Translation paused by user. Remaining pending: {remainingOnPause}.");
                        return new TranslationRunResult(
                            Status: TranslationRunStatus.PausedByUser,
                            TotalCount: totalCount,
                            CompletedCount: completedCount,
                            ProcessedThisRun: processedThisRun,
                            RemainingPendingCount: remainingOnPause,
                            FailedValidationCount: failedValidationCount);
                    }

                    TimeSpan delay = RateLimitDelayCalculator.ComputeDelay(
                        sequentialProfile.DelayBetweenBatchesMs,
                        sequentialProfile.JitterMs,
                        _random);

                    await _delayProvider.DelayAsync(delay, ct).ConfigureAwait(false);
                }
            }

            LogMessage?.Invoke($"Translation run completed. Processed {processedThisRun} entries.");
            return new TranslationRunResult(
                Status: TranslationRunStatus.Completed,
                TotalCount: totalCount,
                CompletedCount: completedCount,
                ProcessedThisRun: processedThisRun,
                RemainingPendingCount: 0,
                FailedValidationCount: failedValidationCount);
        }
        finally
        {
            _pauseRequested = false;
            _runLock.Release();
        }
    }

    private SubtitleEntry MapTranslatedEntry(SubtitleEntry original, string translatedMasked)
    {
        if (_sanitizer is null)
        {
            return original with
            {
                TranslatedText = translatedMasked,
                Status = EntryStatus.Translated
            };
        }

        if (_sanitizer.TryUnmask(translatedMasked, original.Placeholders, out string finalPatched) &&
            !HasDuplicateOrUnexpectedPlaceholders(translatedMasked, original.Placeholders, original.RawText))
        {
            bool hasOverflow = original.RawText.Length > 0 &&
                               finalPatched.Length * 4 > original.RawText.Length * 5;

            return original with
            {
                TranslatedText = translatedMasked,
                FinalPatchedText = finalPatched,
                Status = EntryStatus.Translated,
                HasOverflowWarning = hasOverflow
            };
        }

        return original with
        {
            TranslatedText = translatedMasked,
            FinalPatchedText = original.RawText,
            Status = EntryStatus.FailedValidation,
            HasOverflowWarning = false
        };
    }

    private static bool HasDuplicateOrUnexpectedPlaceholders(
        string? translatedMaskedText,
        IReadOnlyDictionary<string, string>? placeholders,
        string? rawText)
    {
        if (string.IsNullOrEmpty(translatedMaskedText))
        {
            return false;
        }

        Dictionary<string, int>? literalCounts = null;
        if (!string.IsNullOrEmpty(rawText) && rawText.Contains('[', StringComparison.Ordinal))
        {
            foreach (Match rawMatch in PlaceholderTokenRegex().Matches(rawText))
            {
                string rawToken = rawMatch.Value;
                if (placeholders is not null && placeholders.ContainsKey(rawToken))
                {
                    continue;
                }

                literalCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                literalCounts.TryGetValue(rawToken, out int current);
                literalCounts[rawToken] = current + 1;
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in PlaceholderTokenRegex().Matches(translatedMaskedText))
        {
            string token = match.Value;
            int allowedCount = placeholders is not null && placeholders.ContainsKey(token) ? 1 : 0;
            if (literalCounts is not null && literalCounts.TryGetValue(token, out int literalCount))
            {
                allowedCount += literalCount;
            }

            if (allowedCount == 0)
            {
                return true;
            }

            counts.TryGetValue(token, out int existing);
            if (existing >= allowedCount)
            {
                return true;
            }

            counts[token] = existing + 1;
        }

        return false;
    }

    [GeneratedRegex(@"\[(?:TAG|VAR|NL|TAB)_\d+\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PlaceholderTokenRegex();

    private TranslationRunResult HandleAutoPause(
        HttpStatusCode statusCode,
        string reason,
        TimeSpan? retryAfter,
        int totalCount,
        int completedCount,
        int processedThisRun,
        int initialPendingCount,
        int failedValidationCount)
    {
        int remainingPending = Math.Max(0, initialPendingCount - processedThisRun);
        TranslationAutoPausedEventArgs args = new(
            statusCode: statusCode,
            reason: reason,
            completedCount: completedCount,
            totalCount: totalCount,
            remainingPendingCount: remainingPending,
            retryAfter: retryAfter);

        LogMessage?.Invoke(args.ToString());
        AutoPaused?.Invoke(this, args);

        return new TranslationRunResult(
            Status: TranslationRunStatus.AutoPaused,
            TotalCount: totalCount,
            CompletedCount: completedCount,
            ProcessedThisRun: processedThisRun,
            RemainingPendingCount: remainingPending,
            FailedValidationCount: failedValidationCount,
            AutoPauseStatusCode: statusCode,
            AutoPauseReason: reason);
    }
}
