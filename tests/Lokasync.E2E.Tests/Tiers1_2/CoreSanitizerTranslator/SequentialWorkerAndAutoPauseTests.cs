using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.CoreSanitizerTranslator;

public sealed class SequentialWorkerAndAutoPauseTests
{
    private static List<SubtitleEntry> MakeMaskedEntries(int count, EntryStatus status = EntryStatus.Pending, int startIndex = 1)
    {
        ITextSanitizer sanitizer = new OpaqueContractBridge().CreateTextSanitizer();
        List<SubtitleEntry> list = [];
        for (int i = startIndex; i < startIndex + count; i++)
        {
            string raw = $"<b>Hero {i}</b> has {{gold_{i}}} gold.";
            (string masked, Dictionary<string, string> placeholders) = sanitizer.Mask(raw);
            list.Add(new SubtitleEntry(
                0,
                $"key_{i:D3}",
                "data/Map001.json",
                null,
                raw,
                masked,
                placeholders,
                status == EntryStatus.Pending ? null : $"[ID] {masked}",
                status == EntryStatus.Pending ? null : $"[ID] {raw}",
                status,
                false));
        }

        return list;
    }

    // =========================================================================
    // Feature F11: Sequential Queue Worker & Rate Limiter (6 Tier 1 + 6 Tier 2)
    // =========================================================================

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_MultipleBatches_EnforcesMaxObservedConcurrencyEqualsOne()
    {
        // Arrange
        using TempWorkspace ws = new("f11_seq");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(15));

        MockLlmRouterHandler mock = new()
        {
            SimulatedDelay = TimeSpan.FromMilliseconds(30),
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(maxConcurrency: 1, delayBetweenBatchesMs: 0, jitterMs: 0, batchSize: 5);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");

        // Assert
        Assert.Equal(3, mock.Requests.Count);
        E2EAssertions.AssertSequentialExecution(mock.Requests, mock.MaxObservedConcurrency, minDelayMs: 0);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_ProfileMaxConcurrencySetToFour_StillEnforcesSequentialConcurrencyOne()
    {
        // Arrange
        using TempWorkspace ws = new("f11_force_seq");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(15));

        MockLlmRouterHandler mock = new()
        {
            SimulatedDelay = TimeSpan.FromMilliseconds(25),
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(maxConcurrency: 4, delayBetweenBatchesMs: 0, jitterMs: 0, batchSize: 5);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");

        // Assert
        Assert.Equal(3, mock.Requests.Count);
        E2EAssertions.AssertSequentialExecution(mock.Requests, mock.MaxObservedConcurrency, minDelayMs: 0);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_ConfiguredDelayAndJitter_WaitsExpectedIntervalBetweenBatches()
    {
        // Arrange
        using TempWorkspace ws = new("f11_delay_jitter");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(maxConcurrency: 1, delayBetweenBatchesMs: 80, jitterMs: 20, batchSize: 2);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");

        // Assert
        Assert.Equal(3, mock.Requests.Count);
        E2EAssertions.AssertSequentialExecution(mock.Requests, mock.MaxObservedConcurrency, minDelayMs: 60);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_TotalEntriesAndBatchSize_DispatchesExactBatchSizes()
    {
        // Arrange
        using TempWorkspace ws = new("f11_batch_sizes");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(10));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 4);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");

        // Assert
        Assert.Equal(3, mock.Requests.Count);
        Assert.Equal(4, mock.Requests[0].ParsedInputBatch!.Count);
        Assert.Equal(4, mock.Requests[1].ParsedInputBatch!.Count);
        Assert.Equal(2, mock.Requests[2].ParsedInputBatch!.Count);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_MixedStatusesInSqlite_ProcessesOnlyPendingEntriesOnResume()
    {
        // Arrange
        using TempWorkspace ws = new("f11_resume_pending");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        List<SubtitleEntry> seed =
        [
            .. MakeMaskedEntries(4, EntryStatus.Translated, startIndex: 1),
            .. MakeMaskedEntries(1, EntryStatus.ManuallyEdited, startIndex: 5),
            .. MakeMaskedEntries(1, EntryStatus.FailedValidation, startIndex: 6),
            .. MakeMaskedEntries(4, EntryStatus.Pending, startIndex: 7)
        ];
        await db.InsertSubtitleEntriesAsync(seed);

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 10);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");

        // Assert
        Assert.Single(mock.Requests);
        IReadOnlyDictionary<string, string> sent = mock.Requests[0].ParsedInputBatch!;
        Assert.Equal(4, sent.Count);
        Assert.Contains("key_007", sent.Keys);
        Assert.Contains("key_008", sent.Keys);
        Assert.Contains("key_009", sent.Keys);
        Assert.Contains("key_010", sent.Keys);
        Assert.DoesNotContain("key_001", sent.Keys);
        Assert.DoesNotContain("key_005", sent.Keys);
        Assert.DoesNotContain("key_006", sent.Keys);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_ValidTranslations_CheckpointsTranslatedTextFinalTextAndStatusInSqlite()
    {
        // Arrange
        using TempWorkspace ws = new("f11_checkpoint_unmask");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(4));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"Terjemahan {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 4);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(4, all.Count);
        Assert.All(all, e =>
        {
            Assert.Equal(EntryStatus.Translated, e.Status);
            Assert.StartsWith("Terjemahan ", e.TranslatedText);
            Assert.StartsWith("Terjemahan <b>Hero ", e.FinalPatchedText);
        });
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_ZeroPendingEntries_MakesZeroHttpCallsAndCompletes()
    {
        // Arrange
        using TempWorkspace ws = new("f11_zero_pending");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync([
            .. MakeMaskedEntries(2, EntryStatus.Translated, 1),
            .. MakeMaskedEntries(1, EntryStatus.ManuallyEdited, 3)
        ]);

        MockLlmRouterHandler mock = new();
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile();

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");

        // Assert
        Assert.Empty(mock.Requests);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_JitterGreaterThanDelay_ClampsNegativeDelayToZeroWithoutException()
    {
        // Arrange
        using TempWorkspace ws = new("f11_jitter_clamp");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(4));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(delayBetweenBatchesMs: 10, jitterMs: 500, batchSize: 2);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(2, mock.Requests.Count);
        Assert.All(all, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_BatchSizeLargerThanPendingCount_SendsSinglePartialBatch()
    {
        // Arrange
        using TempWorkspace ws = new("f11_oversize_batch");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(3));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 25);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");

        // Assert
        Assert.Single(mock.Requests);
        Assert.Equal(3, mock.Requests[0].ParsedInputBatch!.Count);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_DelayAndJitterZero_ExecutesBatchesWithNoArtificialWait()
    {
        // Arrange
        using TempWorkspace ws = new("f11_zero_delay");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(delayBetweenBatchesMs: 0, jitterMs: 0, batchSize: 2);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");

        // Assert
        Assert.Equal(3, mock.Requests.Count);
        E2EAssertions.AssertSequentialExecution(mock.Requests, mock.MaxObservedConcurrency, minDelayMs: 0);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_CancellationMidQueue_CheckpointsCompletedBatchesAndLeavesRestPending()
    {
        // Arrange
        using TempWorkspace ws = new("f11_cancel_mid");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        using CancellationTokenSource cts = new();
        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mock.EnqueueCustom(_ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate", cts.Token));
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();
        IReadOnlyList<SubtitleEntry> pending = await db.GetPendingEntriesAsync();

        // Assert
        Assert.Equal(2, all.Count(e => e.Status == EntryStatus.Translated));
        Assert.Equal(4, pending.Count);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public async Task RunQueueAsync_LlmDropsPlaceholderToken_MarksRowFailedValidationInSqlite()
    {
        // Arrange
        using TempWorkspace ws = new("f11_drop_token");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        (string m1, Dictionary<string, string> p1) = sanitizer.Mask("Hello {player_name}!");
        (string m2, Dictionary<string, string> p2) = sanitizer.Mask("Plain sentence.");
        await db.InsertSubtitleEntriesAsync([
            new SubtitleEntry(0, "drop_1", "dialog.srt", null, "Hello {player_name}!", m1, p1, null, null, EntryStatus.Pending, false),
            new SubtitleEntry(0, "ok_2", "dialog.srt", null, "Plain sentence.", m2, p2, null, null, EntryStatus.Pending, false)
        ]);

        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(_ => new Dictionary<string, string>
        {
            ["drop_1"] = "Halo pahlawan!", // Deliberately dropped [VAR_0]
            ["ok_2"] = "Kalimat biasa."
        });

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        SubtitleEntry dropped = all.Single(e => e.EntryKey == "drop_1");
        SubtitleEntry valid = all.Single(e => e.EntryKey == "ok_2");
        Assert.Equal(EntryStatus.FailedValidation, dropped.Status);
        Assert.Equal(EntryStatus.Translated, valid.Status);
    }

    // =========================================================================
    // Feature F12: Auto-Pause on Auth/Rate Errors (6 Tier 1 + 6 Tier 2)
    // =========================================================================

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_Http401OnSecondBatch_HaltsImmediatelyFlushesBatch1AndEmitsPauseEvent()
    {
        // Arrange
        using TempWorkspace ws = new("f12_401");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mock.EnqueueHttpStatus(HttpStatusCode.Unauthorized);
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));

        List<string> pauseMessages = [];
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(
            bridge, translator, sanitizer, db, onAutoPause: msg => pauseMessages.Add(msg));
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Single(pauseMessages);
        Assert.Equal(2, mock.Requests.Count);
        Assert.Equal(2, all.Count(e => e.Status == EntryStatus.Translated));
        Assert.Equal(4, all.Count(e => e.Status == EntryStatus.Pending));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_Http403OnSecondBatch_HaltsImmediatelyFlushesBatch1AndEmitsPauseEvent()
    {
        // Arrange
        using TempWorkspace ws = new("f12_403");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mock.EnqueueHttpStatus(HttpStatusCode.Forbidden);
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));

        List<string> pauseMessages = [];
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(
            bridge, translator, sanitizer, db, onAutoPause: msg => pauseMessages.Add(msg));
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Single(pauseMessages);
        Assert.Equal(2, mock.Requests.Count);
        Assert.Equal(2, all.Count(e => e.Status == EntryStatus.Translated));
        Assert.Equal(4, all.Count(e => e.Status == EntryStatus.Pending));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_Http429OnSecondBatch_HaltsImmediatelyFlushesBatch1AndEmitsPauseEvent()
    {
        // Arrange
        using TempWorkspace ws = new("f12_429");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mock.EnqueueHttpStatus(HttpStatusCode.TooManyRequests);
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));

        List<string> pauseMessages = [];
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(
            bridge, translator, sanitizer, db, onAutoPause: msg => pauseMessages.Add(msg));
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Single(pauseMessages);
        Assert.Equal(2, mock.Requests.Count);
        Assert.Equal(2, all.Count(e => e.Status == EntryStatus.Translated));
        Assert.Equal(4, all.Count(e => e.Status == EntryStatus.Pending));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public async Task ResumeAsync_AfterAutoPauseWithUpdatedApiKey_ProcessesOnlyRemainingPendingBatches()
    {
        // Arrange
        using TempWorkspace ws = new("f12_resume_key");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mock.EnqueueHttpStatus(HttpStatusCode.Unauthorized);

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile initialProfile = MockLlmRouterHandler.CreateProfile(apiKey: "sk-expired", batchSize: 2);

        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, initialProfile, "Translate"));

        mock.DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}");
        RouterProfile updatedProfile = initialProfile with { ApiKey = "sk-fresh-valid" };

        // Act
        await CoreTranslatorTestHelpers.ResumeWorkerAsync(worker, db, updatedProfile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(4, mock.Requests.Count); // 2 from first run + 2 remaining batches
        Assert.Equal("Bearer sk-fresh-valid", mock.Requests[2].Authorization);
        Assert.Equal("Bearer sk-fresh-valid", mock.Requests[3].Authorization);
        Assert.DoesNotContain("key_001", mock.Requests[2].ParsedInputBatch!.Keys);
        Assert.DoesNotContain("key_002", mock.Requests[2].ParsedInputBatch!.Keys);
        Assert.All(all, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_Http429OnFirstBatch_PausesImmediatelyLeavingAllEntriesPending()
    {
        // Arrange
        using TempWorkspace ws = new("f12_429_first");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(4));

        MockLlmRouterHandler mock = new();
        mock.EnqueueHttpStatus(HttpStatusCode.TooManyRequests);
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));

        List<string> pauseMessages = [];
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(
            bridge, translator, sanitizer, db, onAutoPause: msg => pauseMessages.Add(msg));
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));
        IReadOnlyList<SubtitleEntry> pending = await db.GetPendingEntriesAsync();

        // Assert
        Assert.Single(pauseMessages);
        Assert.Single(mock.Requests);
        Assert.Equal(4, pending.Count);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_AutoPauseEventArgs_ExposesStatusCodeOrActionableReasonForUi()
    {
        // Arrange
        using TempWorkspace ws = new("f12_event_args");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(2));

        MockLlmRouterHandler mock = new();
        mock.EnqueueHttpStatus(HttpStatusCode.TooManyRequests);

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        List<AutoPauseNotification> details = CoreTranslatorTestHelpers.AttachDetailedAutoPauseListener(worker);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));

        // Assert
        Assert.Single(details);
        AutoPauseNotification info = details[0];
        bool hasActionableDetails = info.StatusCode == 429 ||
                                    (info.Reason?.Contains("429", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                    (info.Reason?.Contains("rate", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                    (info.Reason?.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                    info.RawMessage.Contains("429", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasActionableDetails, $"Expected 429/rate-limit reason in pause event, got StatusCode={info.StatusCode}, Reason='{info.Reason}', Raw='{info.RawMessage}'.");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_Http401OnBatch2WithFiveBatchesQueued_MakesZeroCallsForBatches3To5()
    {
        // Arrange
        using TempWorkspace ws = new("f12_five_batches");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(10));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mock.EnqueueHttpStatus(HttpStatusCode.Unauthorized);

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));

        // Assert
        Assert.Equal(2, mock.Requests.Count);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_Http429DoesNotTriggerSplitRetryLoop_PausesOnFirst429Response()
    {
        // Arrange
        using TempWorkspace ws = new("f12_no_split_429");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        MockLlmRouterHandler mock = new();
        mock.EnqueueHttpStatus(HttpStatusCode.TooManyRequests);
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));

        List<string> pauseEvents = [];
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(
            bridge, translator, sanitizer, db, onAutoPause: msg => pauseEvents.Add(msg));
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 6);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));

        // Assert
        Assert.Single(mock.Requests);
        Assert.Single(pauseEvents);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_Http500ServerError_DoesNotTriggerAuthRateAutoPauseEvent()
    {
        // Arrange
        using TempWorkspace ws = new("f12_500_no_autopause");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(4));

        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mock.EnqueueHttpStatus(HttpStatusCode.InternalServerError);

        List<AutoPauseNotification> pauseDetails = [];
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        pauseDetails = CoreTranslatorTestHelpers.AttachDetailedAutoPauseListener(worker);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));

        // Assert
        Assert.DoesNotContain(pauseDetails, d => d.StatusCode is 401 or 403 or 429);
        Assert.Empty(pauseDetails);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public async Task ResumeAsync_WhenUpdatedProfileStillReceives401_AutoPausesAgainWithoutDataLoss()
    {
        // Arrange
        using TempWorkspace ws = new("f12_double_401");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mock.EnqueueHttpStatus(HttpStatusCode.Unauthorized);
        mock.EnqueueHttpStatus(HttpStatusCode.Unauthorized);

        List<string> pauseEvents = [];
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(
            bridge, translator, sanitizer, db, onAutoPause: msg => pauseEvents.Add(msg));
        RouterProfile profile1 = MockLlmRouterHandler.CreateProfile(apiKey: "bad-1", batchSize: 2);

        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile1, "Translate"));

        // Act
        RouterProfile profile2 = profile1 with { ApiKey = "bad-2" };
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.ResumeWorkerAsync(worker, db, profile2, "Translate"));
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(2, pauseEvents.Count);
        Assert.Equal(2, all.Count(e => e.Status == EntryStatus.Translated));
        Assert.Equal(4, all.Count(e => e.Status == EntryStatus.Pending));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_ManualPauseRequestedMidQueue_StopsCleanlyAndCheckpointsCompletedBatches()
    {
        // Arrange
        using TempWorkspace ws = new("f12_manual_pause");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));

        SequentialWorkerHandle? workerRef = null;
        int batchCount = 0;
        bool pauseRequested = false;
        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(b =>
        {
            batchCount++;
            if (batchCount == 1 && workerRef is not null)
            {
                pauseRequested = CoreTranslatorTestHelpers.TryRequestWorkerPause(workerRef);
            }

            return b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}");
        });
        mock.DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}");

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle activeWorker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        workerRef = activeWorker;
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(delayBetweenBatchesMs: 20, batchSize: 2);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(activeWorker, db, profile, "Translate"));

        IReadOnlyList<SubtitleEntry> afterPause = await db.GetAllEntriesAsync();
        await CoreTranslatorTestHelpers.ResumeWorkerAsync(activeWorker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> afterResume = await db.GetAllEntriesAsync();

        // Assert
        Assert.True(pauseRequested, "Worker must expose a Pause method to support manual pause.");
        Assert.True(afterPause.Count(e => e.Status == EntryStatus.Translated) >= 2);
        Assert.All(afterResume, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public async Task RunQueueAsync_AutoPausePersistedToFileDbThenReopenedInNewWorker_ResumesRemainingPendingRows()
    {
        // Arrange
        using TempWorkspace ws = new("f12_reopen_file_db");
        OpaqueContractBridge bridge = new();
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        MockLlmRouterHandler mock1 = new();
        mock1.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mock1.EnqueueHttpStatus(HttpStatusCode.TooManyRequests);

        await using (DatabaseSession db1 = await bridge.OpenDatabaseAsync(ws.DatabasePath))
        {
            await db1.InsertSubtitleEntriesAsync(MakeMaskedEntries(6));
            SequentialWorkerHandle worker1 = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(
                bridge,
                bridge.CreateTranslatorProvider(mock1),
                bridge.CreateTextSanitizer(),
                db1);

            await Record.ExceptionAsync(() =>
                CoreTranslatorTestHelpers.RunWorkerAsync(worker1, db1, profile, "Translate"));
        }

        // Act
        MockLlmRouterHandler mock2 = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        await using DatabaseSession db2 = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        SequentialWorkerHandle worker2 = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(
            bridge,
            bridge.CreateTranslatorProvider(mock2),
            bridge.CreateTextSanitizer(),
            db2);
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker2, db2, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db2.GetAllEntriesAsync();

        // Assert
        Assert.Equal(2, mock2.Requests.Count); // Only batches 2 & 3 (4 remaining Pending rows)
        Assert.All(all, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }
}
