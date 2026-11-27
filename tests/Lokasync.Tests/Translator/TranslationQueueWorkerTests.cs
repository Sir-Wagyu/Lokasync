using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Tests.Translator.TestDoubles;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.RateLimiting;
using Lokasync.Translator.Providers;
using Lokasync.Translator.Worker;
using Xunit;

namespace Lokasync.Tests.Translator;

public sealed class TranslationQueueWorkerTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _factory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteSubtitleEntryRepository _repository;

    public TranslationQueueWorkerTests()
    {
        _repository = new SqliteSubtitleEntryRepository(_factory);
    }

    public Task InitializeAsync() => _factory.InitializeSchemaAsync();

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private static List<SubtitleEntry> CreatePendingEntries(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new SubtitleEntry(
                Id: 0,
                EntryKey: $"k{i:D2}",
                SourceFile: "Map001.json",
                AssetPath: null,
                RawText: $"Line {i}",
                MaskedText: $"Line {i}",
                Placeholders: new Dictionary<string, string>(StringComparer.Ordinal),
                TranslatedText: null,
                FinalPatchedText: null,
                Status: EntryStatus.Pending,
                HasOverflowWarning: false))
            .ToList();
    }

    private static string BuildEchoJson(params string[] keys)
    {
        Dictionary<string, string> map = keys.ToDictionary(k => k, k => $"Terjemahan {k}", StringComparer.Ordinal);
        return JsonSerializer.Serialize(map);
    }

    [Fact]
    public async Task TranslatePendingAsync_ProfileMaxConcurrencyAboveOne_ExecutesStrictlySequentially()
    {
        // Arrange
        await _repository.BulkUpsertAsync(CreatePendingEntries(6));
        MockHttpMessageHandler handler = new();
        for (int i = 0; i < 3; i++)
        {
            int start = (i * 2) + 1;
            handler.EnqueueAssistantJson(
                BuildEchoJson($"k{start:D2}", $"k{start + 1:D2}"),
                simulatedDelay: TimeSpan.FromMilliseconds(20));
        }

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        FakeDelayProvider delayProvider = new();
        TranslationQueueWorker worker = new(provider, _repository, delayProvider: delayProvider);

        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            MaxConcurrency = 8,
            DelayBetweenBatchesMs = 100,
            JitterMs = 0,
            BatchSize = 2
        };

        // Act
        Task<TranslationRunResult> run1 = worker.TranslatePendingAsync("System", profile);
        Task<TranslationRunResult> run2 = worker.TranslatePendingAsync("System", profile);
        TranslationRunResult[] results = await Task.WhenAll(run1, run2);

        // Assert
        Assert.Equal(1, handler.MaxObservedConcurrency);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains(results, r => r.Status == TranslationRunStatus.Completed && r.ProcessedThisRun == 6);
        Assert.Contains(results, r => r.Status == TranslationRunStatus.Completed && r.ProcessedThisRun == 0);
        Assert.Empty(await _repository.GetPendingAsync());
    }

    [Fact]
    public async Task TranslatePendingAsync_MultipleBatches_AppliesDelayPlusMinusJitterBetweenBatchesOnly()
    {
        // Arrange
        await _repository.BulkUpsertAsync(CreatePendingEntries(5));
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson(BuildEchoJson("k01", "k02"));
        handler.EnqueueAssistantJson(BuildEchoJson("k03", "k04"));
        handler.EnqueueAssistantJson(BuildEchoJson("k05"));

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        FakeDelayProvider delayProvider = new();
        TranslationQueueWorker worker = new(provider, _repository, delayProvider: delayProvider, random: new Random(42));

        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            DelayBetweenBatchesMs = 3500,
            JitterMs = 500,
            BatchSize = 2
        };

        // Act
        TranslationRunResult result = await worker.TranslatePendingAsync("System", profile);

        // Assert
        Assert.Equal(TranslationRunStatus.Completed, result.Status);
        Assert.Equal(2, delayProvider.RecordedDelays.Count);
        Assert.All(delayProvider.RecordedDelays, delay =>
        {
            Assert.InRange(delay.TotalMilliseconds, 3000, 4000);
        });
    }

    [Fact]
    public void RateLimitDelayCalculator_ComputeDelay_ClampsNegativeDelayToZero()
    {
        // Arrange
        Random fixedSeed = new(123);

        // Act
        TimeSpan delay = RateLimitDelayCalculator.ComputeDelay(delayBetweenBatchesMs: 100, jitterMs: 500, fixedSeed);

        // Assert
        Assert.InRange(delay.TotalMilliseconds, 0, 600);
    }

    [Fact]
    public async Task TranslatePendingAsync_SingleBatch_DoesNotInvokeDelayProvider()
    {
        // Arrange
        await _repository.BulkUpsertAsync(CreatePendingEntries(2));
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson(BuildEchoJson("k01", "k02"));

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        FakeDelayProvider delayProvider = new();
        TranslationQueueWorker worker = new(provider, _repository, delayProvider: delayProvider);
        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with { BatchSize = 10 };

        // Act
        TranslationRunResult result = await worker.TranslatePendingAsync("System", profile);

        // Assert
        Assert.Equal(TranslationRunStatus.Completed, result.Status);
        Assert.Empty(delayProvider.RecordedDelays);
    }

    [Fact]
    public async Task TranslatePendingAsync_NoPendingEntries_CompletesWithoutHttpRequests()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        FakeDelayProvider delayProvider = new();
        TranslationQueueWorker worker = new(provider, _repository, delayProvider: delayProvider);

        // Act
        TranslationRunResult result = await worker.TranslatePendingAsync("System", RouterProfileDefaults.CreateDefaultProfile());

        // Assert
        Assert.Equal(TranslationRunStatus.Completed, result.Status);
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(handler.Requests);
        Assert.Empty(delayProvider.RecordedDelays);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TranslatePendingAsync_AuthOrRateLimitAfterFirstBatch_AutoPausesFlushesCheckpointAndRaisesEvent(
        HttpStatusCode pauseStatusCode)
    {
        // Arrange
        await _repository.BulkUpsertAsync(CreatePendingEntries(4));
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson(BuildEchoJson("k01", "k02"));
        handler.EnqueueResponse(
            pauseStatusCode,
            """{"error":"limit"}""",
            retryAfter: pauseStatusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromSeconds(30) : null);

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        FakeDelayProvider delayProvider = new();
        TranslationQueueWorker worker = new(provider, _repository, delayProvider: delayProvider);

        TranslationAutoPausedEventArgs? capturedPauseArgs = null;
        List<(int Completed, int Total)> progressUpdates = [];
        worker.AutoPaused += (_, args) => capturedPauseArgs = args;
        worker.ProgressChanged += (completed, total) => progressUpdates.Add((completed, total));

        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            BatchSize = 2,
            DelayBetweenBatchesMs = 50,
            JitterMs = 0
        };

        // Act
        TranslationRunResult result = await worker.TranslatePendingAsync("System", profile);

        // Assert
        Assert.Equal(TranslationRunStatus.AutoPaused, result.Status);
        Assert.Equal(pauseStatusCode, result.AutoPauseStatusCode);
        Assert.Equal(4, result.TotalCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(2, result.ProcessedThisRun);
        Assert.Equal(2, result.RemainingPendingCount);

        Assert.NotNull(capturedPauseArgs);
        Assert.Equal(pauseStatusCode, capturedPauseArgs!.StatusCode);
        Assert.Equal(2, capturedPauseArgs.CompletedCount);
        Assert.Equal(2, capturedPauseArgs.RemainingPendingCount);
        Assert.Single(progressUpdates);
        Assert.Equal((2, 4), progressUpdates[0]);

        IReadOnlyList<SubtitleEntry> remainingPending = await _repository.GetPendingAsync();
        Assert.Equal(["k03", "k04"], remainingPending.Select(e => e.EntryKey).ToArray());
    }

    [Fact]
    public async Task TranslatePendingAsync_Http429AfterFirstBatch_AutoPausesThenResumeProcessesOnlyRemainingPending()
    {
        // Arrange
        await _repository.BulkUpsertAsync(CreatePendingEntries(4));
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson(BuildEchoJson("k01", "k02"));
        handler.EnqueueResponse(HttpStatusCode.TooManyRequests, "rate limit");
        // Resume request expects ONLY k03 & k04
        handler.EnqueueAssistantJson(BuildEchoJson("k03", "k04"));

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        FakeDelayProvider delayProvider = new();
        TranslationQueueWorker worker = new(provider, _repository, delayProvider: delayProvider);

        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            BatchSize = 2,
            DelayBetweenBatchesMs = 100,
            JitterMs = 0
        };

        // Act
        TranslationRunResult pausedRun = await worker.TranslatePendingAsync("System", profile);
        TranslationRunResult resumedRun = await worker.TranslatePendingAsync("System", profile);

        // Assert
        Assert.Equal(TranslationRunStatus.AutoPaused, pausedRun.Status);
        Assert.Equal(TranslationRunStatus.Completed, resumedRun.Status);
        Assert.Equal(4, resumedRun.CompletedCount);
        Assert.Equal(2, resumedRun.ProcessedThisRun);
        Assert.Equal(0, resumedRun.RemainingPendingCount);

        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("k01", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("k02", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("k03", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("k04", handler.Requests[2].Body, StringComparison.Ordinal);

        Assert.Empty(await _repository.GetPendingAsync());
        Assert.Equal(4, await _repository.CountAsync(EntryStatus.Translated));
    }

    [Fact]
    public async Task TranslatePendingAsync_FileBackedDbReopenedAfterAutoPause_ResumesOnlyRemainingPendingRows()
    {
        // Arrange
        string tempDir = Path.Combine(
            AppContext.BaseDirectory,
            "_test_tmp",
            nameof(TranslationQueueWorkerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "checkpoint.db");

        try
        {
            await using (SqliteConnectionFactory firstFactory = SqliteConnectionFactory.ForFile(dbPath, pooling: false))
            {
                await firstFactory.InitializeSchemaAsync();
                SqliteSubtitleEntryRepository firstRepo = new(firstFactory);
                await firstRepo.BulkUpsertAsync(CreatePendingEntries(4));

                MockHttpMessageHandler firstHandler = new();
                firstHandler.EnqueueAssistantJson(BuildEchoJson("k01", "k02"));
                firstHandler.EnqueueResponse(HttpStatusCode.Unauthorized, "bad token");

                using HttpClient firstClient = new(firstHandler);
                TranslationQueueWorker firstWorker = new(
                    new OpenAiTranslatorProvider(firstClient),
                    firstRepo,
                    delayProvider: new FakeDelayProvider());

                RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
                {
                    BatchSize = 2,
                    DelayBetweenBatchesMs = 0,
                    JitterMs = 0
                };

                TranslationRunResult paused = await firstWorker.TranslatePendingAsync("System", profile);
                Assert.Equal(TranslationRunStatus.AutoPaused, paused.Status);
            }

            // Act
            await using SqliteConnectionFactory reopenedFactory = SqliteConnectionFactory.ForFile(dbPath, pooling: false);
            SqliteSubtitleEntryRepository reopenedRepo = new(reopenedFactory);
            MockHttpMessageHandler secondHandler = new();
            secondHandler.EnqueueAssistantJson(BuildEchoJson("k03", "k04"));

            using HttpClient secondClient = new(secondHandler);
            TranslationQueueWorker secondWorker = new(
                new OpenAiTranslatorProvider(secondClient),
                reopenedRepo,
                delayProvider: new FakeDelayProvider());

            TranslationRunResult resumed = await secondWorker.TranslatePendingAsync(
                "System",
                RouterProfileDefaults.CreateDefaultProfile() with { BatchSize = 2, DelayBetweenBatchesMs = 0, JitterMs = 0 });

            // Assert
            Assert.Equal(TranslationRunStatus.Completed, resumed.Status);
            Assert.Equal(2, resumed.ProcessedThisRun);
            Assert.Single(secondHandler.Requests);
            Assert.DoesNotContain("k01", secondHandler.Requests[0].Body, StringComparison.Ordinal);
            Assert.Contains("k03", secondHandler.Requests[0].Body, StringComparison.Ordinal);
            Assert.Empty(await reopenedRepo.GetPendingAsync());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TranslatePendingAsync_BatchMismatchExceedingTwoRetries_MarksFailedKeysFailedValidationAndKeepsSucceeded()
    {
        // Arrange
        await _repository.BulkUpsertAsync(CreatePendingEntries(2));
        MockHttpMessageHandler handler = new();
        // Attempt 0 (2 keys) -> mismatch
        handler.EnqueueAssistantJson("""{"k01": "Satu"}""");
        // Retry 1 (split to 1 + 1): k01 succeeds, k02 mismatches
        handler.EnqueueAssistantJson("""{"k01": "Satu"}""");
        handler.EnqueueAssistantJson("""{"wrong": "Dua"}""");
        // Retry 2: k02 still mismatches
        handler.EnqueueAssistantJson("""{"still_wrong": "Dua"}""");

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        FakeDelayProvider delayProvider = new();
        TranslationQueueWorker worker = new(provider, _repository, delayProvider: delayProvider);

        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            BatchSize = 2,
            DelayBetweenBatchesMs = 10,
            JitterMs = 0
        };

        // Act
        TranslationRunResult result = await worker.TranslatePendingAsync("System", profile);

        // Assert
        Assert.Equal(TranslationRunStatus.Completed, result.Status);
        Assert.Equal(1, result.FailedValidationCount);
        SubtitleEntry? k01 = await _repository.GetByEntryKeyAsync("k01");
        SubtitleEntry? k02 = await _repository.GetByEntryKeyAsync("k02");
        Assert.NotNull(k01);
        Assert.NotNull(k02);
        Assert.Equal(EntryStatus.Translated, k01!.Status);
        Assert.Equal(EntryStatus.FailedValidation, k02!.Status);
        Assert.Equal(k02.RawText, k02.FinalPatchedText);
    }

    [Fact]
    public async Task TranslatePendingAsync_WithSanitizer_FlagsFailedValidationWithRawFallbackAndOverflowAbove25Percent()
    {
        // Arrange
        await _repository.BulkUpsertAsync(
        [
            new SubtitleEntry(
                0,
                "k_valid_overflow",
                "Map001.json",
                null,
                "Hi!",
                "Hi!",
                new Dictionary<string, string>(StringComparer.Ordinal),
                null,
                null,
                EntryStatus.Pending,
                false),
            new SubtitleEntry(
                0,
                "k_broken_tag",
                "Map001.json",
                null,
                "<b>Hero</b>",
                "[TAG_0]Hero[TAG_1]",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["[TAG_0]"] = "<b>",
                    ["[TAG_1]"] = "</b>"
                },
                null,
                null,
                EntryStatus.Pending,
                false)
        ]);

        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson("""
            {
              "k_valid_overflow": "Halo semuanya apa kabar!",
              "k_broken_tag": "[TAG_0]Pahlawan"
            }
            """);

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        FakeTextSanitizer sanitizer = new();
        TranslationQueueWorker worker = new(provider, _repository, sanitizer: sanitizer, delayProvider: new FakeDelayProvider());

        // Act
        TranslationRunResult result = await worker.TranslatePendingAsync(
            new NarrativeContext("Test", "RPG", []),
            RouterProfileDefaults.CreateDefaultProfile() with { DelayBetweenBatchesMs = 0, JitterMs = 0 });

        // Assert
        Assert.Equal(TranslationRunStatus.Completed, result.Status);
        Assert.Equal(1, result.FailedValidationCount);

        SubtitleEntry? valid = await _repository.GetByEntryKeyAsync("k_valid_overflow");
        SubtitleEntry? broken = await _repository.GetByEntryKeyAsync("k_broken_tag");
        Assert.NotNull(valid);
        Assert.NotNull(broken);

        Assert.Equal(EntryStatus.Translated, valid!.Status);
        Assert.True(valid.HasOverflowWarning);
        Assert.Equal("Halo semuanya apa kabar!", valid.FinalPatchedText);

        Assert.Equal(EntryStatus.FailedValidation, broken!.Status);
        Assert.False(broken.HasOverflowWarning);
        Assert.Equal("<b>Hero</b>", broken.FinalPatchedText);
    }

    [Fact]
    public async Task TranslatePendingAsync_RequestPauseAfterFirstBatch_ReturnsPausedByUserWithCheckpointSaved()
    {
        // Arrange
        await _repository.BulkUpsertAsync(CreatePendingEntries(4));
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson(BuildEchoJson("k01", "k02"));

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        FakeDelayProvider delayProvider = new();
        TranslationQueueWorker worker = new(provider, _repository, delayProvider: delayProvider);
        worker.ProgressChanged += (_, _) => worker.RequestPause();

        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            BatchSize = 2,
            DelayBetweenBatchesMs = 100,
            JitterMs = 0
        };

        // Act
        TranslationRunResult result = await worker.TranslatePendingAsync("System", profile);

        // Assert
        Assert.Equal(TranslationRunStatus.PausedByUser, result.Status);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(2, result.RemainingPendingCount);
        Assert.Single(handler.Requests);
        Assert.Empty(delayProvider.RecordedDelays);
        Assert.Equal(2, (await _repository.GetPendingAsync()).Count);
    }
}
