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

public sealed class TolerantJsonAndBatchRetryTests
{
    private static List<SubtitleEntry> MakePendingEntries(params string[] keys) =>
        keys.Select((k, idx) => new SubtitleEntry(
            0,
            k,
            "dialog.srt",
            null,
            $"Line {idx + 1}",
            $"Line {idx + 1}",
            new Dictionary<string, string>(),
            null,
            null,
            EntryStatus.Pending,
            false)).ToList();

    // =========================================================================
    // Feature F9: Tolerant JSON Extraction (6 Tier 1 + 6 Tier 2)
    // =========================================================================

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_CleanJsonObject_ParsesAllEntries()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent("""{"1": "Halo", "2": "Dunia"}""");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        Dictionary<string, string> batch = new() { ["1"] = "Hello", ["2"] = "World" };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch, "Translate", MockLlmRouterHandler.CreateProfile(), CancellationToken.None);

        // Assert
        Assert.Equal("Halo", result["1"]);
        Assert.Equal("Dunia", result["2"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_MarkdownJsonFence_StripsFenceAndParsesEntries()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueMarkdownFencedBatch(_ => new Dictionary<string, string>
        {
            ["1"] = "Halo",
            ["2"] = "Dunia"
        }, "json");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        Dictionary<string, string> batch = new() { ["1"] = "Hello", ["2"] = "World" };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch, "Translate", MockLlmRouterHandler.CreateProfile(), CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Halo", result["1"]);
        Assert.Equal("Dunia", result["2"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_PlainMarkdownFenceWithoutLanguageTag_ParsesEntries()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent("```\n{\"1\": \"Mulai\"}\n```");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        Dictionary<string, string> batch = new() { ["1"] = "Start" };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch, "Translate", MockLlmRouterHandler.CreateProfile(), CancellationToken.None);

        // Assert
        Assert.Equal("Mulai", result["1"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_LeadingAndTrailingConversationalProse_ExtractsFirstToLastBrace()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent(
            "Sure! Here is your translation:\n{\"1\": \"Selamat datang\"}\nLet me know if you need more!");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        Dictionary<string, string> batch = new() { ["1"] = "Welcome" };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch, "Translate", MockLlmRouterHandler.CreateProfile(), CancellationToken.None);

        // Assert
        Assert.Equal("Selamat datang", result["1"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_NestedBracesInsideTranslatedStringValues_PreservesBracesAndParses()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent(
            "Here:\n```json\n{\"1\": \"Halo {player_name}, item {0}\"}\n```\nDone.");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        Dictionary<string, string> batch = new() { ["1"] = "Hello {player_name}, item {0}" };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch, "Translate", MockLlmRouterHandler.CreateProfile(), CancellationToken.None);

        // Assert
        Assert.Equal("Halo {player_name}, item {0}", result["1"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_EscapedQuotesAndNewlinesInsideValues_ParsesValidJson()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent("""{"1": "Dia berkata, \"Lari!\"\nCepat!"}""");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        Dictionary<string, string> batch = new() { ["1"] = "He said, \"Run!\"\nFast!" };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch, "Translate", MockLlmRouterHandler.CreateProfile(), CancellationToken.None);

        // Assert
        Assert.Equal("Dia berkata, \"Lari!\"\nCepat!", result["1"]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_MissingOpeningBrace_ThrowsJsonOrFormatException()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent("\"1\": \"Halo\"}");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act
        Exception? ex = await Record.ExceptionAsync(() => provider.TranslateBatchAsync(
            new Dictionary<string, string> { ["1"] = "Hello" },
            "Translate",
            MockLlmRouterHandler.CreateProfile(),
            CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        bool isFormatEx = ex is JsonException or FormatException or InvalidOperationException ||
                          ex!.GetType().Name.Contains("Format", StringComparison.OrdinalIgnoreCase) ||
                          ex.GetType().Name.Contains("Json", StringComparison.OrdinalIgnoreCase);
        Assert.True(isFormatEx, $"Unexpected exception: {ex!.GetType().FullName}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_MissingClosingBraceTruncatedResponse_ThrowsJsonException()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueMalformedJson("```json\n{\"1\": \"Halo\", \"2\": \"Dun");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act
        Exception? ex = await Record.ExceptionAsync(() => provider.TranslateBatchAsync(
            new Dictionary<string, string> { ["1"] = "Hello", ["2"] = "World" },
            "Translate",
            MockLlmRouterHandler.CreateProfile(),
            CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        bool isFormatEx = ex is JsonException or FormatException or InvalidOperationException ||
                          ex!.GetType().Name.Contains("Format", StringComparison.OrdinalIgnoreCase) ||
                          ex.GetType().Name.Contains("Json", StringComparison.OrdinalIgnoreCase);
        Assert.True(isFormatEx, $"Unexpected exception: {ex!.GetType().FullName}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_EmptyOrWhitespaceContent_ThrowsException(string emptyContent)
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent(emptyContent);
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act
        Exception? ex = await Record.ExceptionAsync(() => provider.TranslateBatchAsync(
            new Dictionary<string, string> { ["1"] = "Hello" },
            "Translate",
            MockLlmRouterHandler.CreateProfile(),
            CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_ClosingBraceBeforeOpeningBrace_ThrowsException()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent("} invalid {");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act
        Exception? ex = await Record.ExceptionAsync(() => provider.TranslateBatchAsync(
            new Dictionary<string, string> { ["1"] = "Hello" },
            "Translate",
            MockLlmRouterHandler.CreateProfile(),
            CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        Assert.IsNotType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_SyntacticallyInvalidContentBetweenBraces_ThrowsJsonException()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent("Here: { not: valid json at all } end");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act
        Exception? ex = await Record.ExceptionAsync(() => provider.TranslateBatchAsync(
            new Dictionary<string, string> { ["1"] = "Hello" },
            "Translate",
            MockLlmRouterHandler.CreateProfile(),
            CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        bool isFormatEx = ex is JsonException or FormatException or InvalidOperationException ||
                          ex!.GetType().Name.Contains("Format", StringComparison.OrdinalIgnoreCase) ||
                          ex.GetType().Name.Contains("Json", StringComparison.OrdinalIgnoreCase);
        Assert.True(isFormatEx, $"Unexpected exception: {ex!.GetType().FullName}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F9")]
    public async Task ExtractJson_TrailingCommaOrExtraProseWithBracesOutsideJson_FailsOrHandlesPredictably()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueRawAssistantContent("Note: use {json} -> {\"1\": \"Halo\"}");
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act
        Dictionary<string, string>? result = null;
        Exception? ex = await Record.ExceptionAsync(async () =>
        {
            result = await provider.TranslateBatchAsync(
                new Dictionary<string, string> { ["1"] = "Hello" },
                "Translate",
                MockLlmRouterHandler.CreateProfile(),
                CancellationToken.None);
        });

        // Assert
        if (ex is not null)
        {
            Assert.True(ex is JsonException or FormatException or InvalidOperationException,
                $"Unexpected exception type: {ex.GetType().FullName}");
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal("Halo", result!["1"]);
        }
    }

    // =========================================================================
    // Feature F10: Batch Integrity Check & Split-and-Retry (6 Tier 1 + 6 Tier 2)
    // =========================================================================

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_ExactKeyMatchOnFirstCall_ReturnsAllTranslationsWithOneHttpRequest()
    {
        // Arrange
        using TempWorkspace ws = new("f10_exact");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("k1", "k2", "k3", "k4", "k5", "k6"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 6);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Single(mock.Requests);
        Assert.Equal(6, all.Count);
        Assert.All(all, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_MissingKeyInBatchOfSix_SplitsIntoSmallerSubBatchesAndSucceeds()
    {
        // Arrange
        using TempWorkspace ws = new("f10_missing_split");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("k1", "k2", "k3", "k4", "k5", "k6"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mock.EnqueueMissingKeyResponse(dropCount: 1);

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 6);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.True(mock.Requests.Count >= 2, $"Expected split retry requests, got {mock.Requests.Count}");
        Assert.All(all, e =>
        {
            Assert.Equal(EntryStatus.Translated, e.Status);
            Assert.StartsWith("[ID] ", e.TranslatedText);
        });
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_CountMatchesButKeyNameWrong_DetectsMismatchSplitsAndSucceeds()
    {
        // Arrange
        using TempWorkspace ws = new("f10_wrong_key");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("k1", "k2", "k3", "k4"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mock.EnqueueWrongKeyNameResponse("WRONG_KEY");

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 4);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.True(mock.Requests.Count >= 2);
        Assert.All(all, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_Retry1StillMismatchesThenRetry2Succeeds_CompletesWithinTwoRetries()
    {
        // Arrange
        using TempWorkspace ws = new("f10_retry2_ok");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries(
            "k1", "k2", "k3", "k4", "k5", "k6", "k7", "k8", "k9", "k10"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mock.EnqueueMissingKeyResponse(dropCount: 1); // Initial 10-key call fails
        mock.EnqueueMissingKeyResponse(dropCount: 1); // First split chunk fails on retry 1

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 10);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(10, all.Count);
        Assert.All(all, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_ExtraHallucinatedKeyReturned_TriggersSplitAndResolvesCleanly()
    {
        // Arrange
        using TempWorkspace ws = new("f10_extra_key");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("k1", "k2", "k3", "k4"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mock.EnqueueExtraKeyResponse("extra_hallucinated", "Tambahan");

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 4);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(4, all.Count);
        Assert.All(all, e => Assert.Equal(EntryStatus.Translated, e.Status));
        Assert.DoesNotContain(all, e => e.EntryKey == "extra_hallucinated");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_SingleKeyBatchExactMatch_SucceedsWithoutSplitting()
    {
        // Arrange
        using TempWorkspace ws = new("f10_single_key");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("only_one"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 1);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Single(mock.Requests);
        Assert.Single(all);
        Assert.Equal(EntryStatus.Translated, all[0].Status);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_PersistentMissingKeyAcrossAllAttempts_StopsAfterMaxTwoRetries()
    {
        // Arrange
        using TempWorkspace ws = new("f10_persistent_missing");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("k1", "k2", "k3", "k4"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = batch =>
            {
                Dictionary<string, string> partial = new();
                foreach (KeyValuePair<string, string> kv in batch.Take(Math.Max(0, batch.Count - 1)))
                {
                    partial[kv.Key] = $"[ID] {kv.Value}";
                }

                return partial;
            }
        };

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 4);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.InRange(mock.Requests.Count, 2, 15);
        Assert.Contains(all, e => e.Status != EntryStatus.Translated);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_SingleKeyBatchAlwaysMismatches_StopsAfterMaxTwoRetries()
    {
        // Arrange
        using TempWorkspace ws = new("f10_single_mismatch");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("k1"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = _ => new Dictionary<string, string> { ["wrong_key"] = "x" }
        };

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 1);

        // Act
        await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate"));
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.InRange(mock.Requests.Count, 1, 3);
        Assert.NotEqual(EntryStatus.Translated, all[0].Status);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_EmptyResponseObject_TriggersSplitAndRetry()
    {
        // Arrange
        using TempWorkspace ws = new("f10_empty_obj");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("k1", "k2", "k3", "k4"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mock.EnqueueRawAssistantContent("{}");

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 4);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.True(mock.Requests.Count >= 2);
        Assert.All(all, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_DuplicateOrCaseMismatchedKeysInResponse_TreatedAsKeyMismatch()
    {
        // Arrange
        using TempWorkspace ws = new("f10_case_mismatch");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("KeyA", "KeyB"));

        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mock.EnqueueBatchTranslator(_ => new Dictionary<string, string>
        {
            ["keya"] = "[ID] A",
            ["keyb"] = "[ID] B"
        });

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 2);

        // Act
        await CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate");
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.True(mock.Requests.Count >= 2);
        Assert.All(all, e => Assert.Equal(EntryStatus.Translated, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_Http401DuringSplitRetry_HaltsImmediatelyWithoutContinuingSiblingChunks()
    {
        // Arrange
        using TempWorkspace ws = new("f10_401_split");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("k1", "k2", "k3", "k4", "k5", "k6"));

        MockLlmRouterHandler mock = new();
        mock.EnqueueMissingKeyResponse(dropCount: 1);
        mock.EnqueueHttpStatus(HttpStatusCode.Unauthorized);
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
        Assert.Equal(2, mock.Requests.Count);
        Assert.NotEmpty(pauseEvents);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public async Task TranslateWithIntegrity_CancelledTokenDuringRetry_ThrowsOperationCanceledException()
    {
        // Arrange
        using TempWorkspace ws = new("f10_cancel_retry");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync(MakePendingEntries("k1", "k2", "k3", "k4"));

        using CancellationTokenSource cts = new();
        MockLlmRouterHandler mock = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mock.EnqueueCustom(req =>
        {
            cts.Cancel();
            string partial = JsonSerializer.Serialize(new Dictionary<string, string> { ["k1"] = "[ID] Line 1" });
            string envelope = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-cancel",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = partial } } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json")
            };
        });

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mock);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = CoreTranslatorTestHelpers.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 4);

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoreTranslatorTestHelpers.RunWorkerAsync(worker, db, profile, "Translate", cts.Token));
        Assert.Single(mock.Requests);
    }
}
