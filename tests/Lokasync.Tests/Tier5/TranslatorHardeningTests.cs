using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Sanitizer;
using Lokasync.Tests.Translator.TestDoubles;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Exceptions;
using Lokasync.Translator.Providers;
using Lokasync.Translator.Worker;
using Xunit;

namespace Lokasync.Tests.Tier5;

public sealed class TranslatorHardeningTests
{
    [Fact]
    public void TolerantJsonExtractor_UnmatchedQuoteInSurroundingProseWithBraces_ExtractsValidJsonMap()
    {
        // Arrange
        const string raw = "Note {player_name} on 5\" screen:\n{\"k1\": \"halo {player_name}\"}";

        // Act
        Dictionary<string, string> map = TolerantJsonExtractor.ExtractStringMap(raw);

        // Assert
        KeyValuePair<string, string> entry = Assert.Single(map);
        Assert.Equal("k1", entry.Key);
        Assert.Equal("halo {player_name}", entry.Value);
    }

    [Fact]
    public void TolerantJsonExtractor_UnmatchedQuoteInsideProseBraceAndEscapedQuoteInJson_ExtractsValidJsonMap()
    {
        // Arrange
        const string unmatchedInBalancedProseBrace = "Note {player_name \"hello} and {\"k1\": \"say \\\"hi\\\"\"}";
        const string unclosedOuterAndNestedQuoteBraces = "Intro {outer {inner \"quote1} and \"quote2} then {bad \"quote3:\n{\"k1\":\"v1\",\"k2\":\"nested {brace} and \\\"quote\\\"\"}";

        // Act
        Dictionary<string, string> firstMap = TolerantJsonExtractor.ExtractStringMap(unmatchedInBalancedProseBrace);
        Dictionary<string, string> secondMap = TolerantJsonExtractor.ExtractStringMap(unclosedOuterAndNestedQuoteBraces);
        string firstSlice = TolerantJsonExtractor.ExtractJsonObjectSlice(unmatchedInBalancedProseBrace);
        string secondSlice = TolerantJsonExtractor.ExtractJsonObjectSlice(unclosedOuterAndNestedQuoteBraces);

        // Assert
        KeyValuePair<string, string> firstEntry = Assert.Single(firstMap);
        Assert.Equal("k1", firstEntry.Key);
        Assert.Equal("say \"hi\"", firstEntry.Value);
        Assert.Equal("{\"k1\": \"say \\\"hi\\\"\"}", firstSlice);

        Assert.Equal(2, secondMap.Count);
        Assert.Equal("v1", secondMap["k1"]);
        Assert.Equal("nested {brace} and \"quote\"", secondMap["k2"]);
        Assert.Equal("{\"k1\":\"v1\",\"k2\":\"nested {brace} and \\\"quote\\\"\"}", secondSlice);
    }

    [Fact]
    public void TolerantJsonExtractor_ExtractJsonObjectSlice_HandlesProseArrayRootsAndSemanticErrors()
    {
        // Arrange
        const string validWithProse = "Sure! Here is the JSON:\n{\"k\":\"v\"}\nDone.";
        const string arrayRoot = "[1, 2, 3]";
        const string primitiveRoot = "\"just a string\"";
        const string semanticNonString = "{bad} prose before {\"k\": 123}";
        const string semanticNestedEmptyBraces = "{bad \"quote} {\"k1\": \"{}\", \"k2\": 123}";
        string pathologicalBraces = "{ \"" + new string('{', 5000) + " invalid }";

        // Act
        string slice = TolerantJsonExtractor.ExtractJsonObjectSlice(validWithProse);

        // Assert
        Assert.Equal("{\"k\":\"v\"}", slice);
        Assert.Throws<TranslatorResponseFormatException>(() => TolerantJsonExtractor.ExtractJsonObjectSlice(arrayRoot));
        Assert.Throws<TranslatorResponseFormatException>(() => TolerantJsonExtractor.ExtractJsonObjectSlice(primitiveRoot));
        Assert.Throws<TranslatorResponseFormatException>(() => TolerantJsonExtractor.ExtractStringMap(semanticNonString));
        Assert.Throws<TranslatorResponseFormatException>(() => TolerantJsonExtractor.ExtractStringMap(semanticNestedEmptyBraces));
        Assert.Throws<TranslatorResponseFormatException>(() => TolerantJsonExtractor.ExtractStringMap(pathologicalBraces));
    }

    [Fact]
    public async Task OpenAiTranslatorProvider_RetryAfterHttpDateAndNegativeDelta_ClampsAndPreservesDelay()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueHandler((_, _, _) =>
        {
            HttpResponseMessage resp = new(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":"past"}""", Encoding.UTF8, "application/json")
            };
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5));
            return Task.FromResult(resp);
        });
        handler.EnqueueHandler((_, _, _) =>
        {
            HttpResponseMessage resp = new(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":"future"}""", Encoding.UTF8, "application/json")
            };
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30));
            return Task.FromResult(resp);
        });
        handler.EnqueueResponse(HttpStatusCode.OK, "not-valid-json");
        handler.EnqueueResponse(HttpStatusCode.OK, """{"choices":[]}""");

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            DelayBetweenBatchesMs = 0,
            JitterMs = 0
        };
        Dictionary<string, string> batch = new(StringComparer.Ordinal) { ["k1"] = "Hello" };

        // Act
        TranslatorPauseException pastPause = await Assert.ThrowsAsync<TranslatorPauseException>(
            () => provider.TranslateBatchAsync(batch, "System", profile, CancellationToken.None));
        TranslatorPauseException futurePause = await Assert.ThrowsAsync<TranslatorPauseException>(
            () => provider.TranslateBatchAsync(batch, "System", profile, CancellationToken.None));
        await Assert.ThrowsAsync<TranslatorResponseFormatException>(
            () => provider.TranslateBatchAsync(batch, "System", profile, CancellationToken.None));
        await Assert.ThrowsAsync<TranslatorResponseFormatException>(
            () => provider.TranslateBatchAsync(batch, "System", profile, CancellationToken.None));

        // Assert
        Assert.Equal(TimeSpan.Zero, pastPause.RetryAfter);
        Assert.NotNull(futurePause.RetryAfter);
        Assert.True(futurePause.RetryAfter!.Value > TimeSpan.Zero);
    }

    [Fact]
    public async Task RouterProfileStore_EnsureInitializedAndNormalizedFallbacks_HandlesMissingAndPartialDtos()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "Lokasync_Tier5_Profiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string missingPath = Path.Combine(tempDir, "init_profiles.json");
        string partialPath = Path.Combine(tempDir, "partial_profiles.json");

        try
        {
            await File.WriteAllTextAsync(partialPath, """
                {
                  "active_profile": "unknown_profile",
                  "profiles": {
                    "broken": {
                      "base_url": "ftp://bad",
                      "api_key": "",
                      "model": "   ",
                      "max_concurrency": 0,
                      "delay_between_batches_ms": -50,
                      "jitter_ms": -10,
                      "batch_size": 0
                    }
                  }
                }
                """);

            RouterProfileStore initStore = new(missingPath);
            RouterProfileStore partialStore = new(partialPath);

            // Act
            RouterProfilesConfig initialized = await initStore.EnsureInitializedAsync();
            RouterProfilesConfig loaded = await partialStore.LoadAsync();
            RouterProfile resolved = loaded.ResolveActiveProfile();

            // Assert
            Assert.True(File.Exists(missingPath));
            Assert.Equal(RouterProfileDefaults.DefaultProfileName, initialized.ActiveProfile);

            Assert.Equal("broken", loaded.ActiveProfile);
            Assert.Equal(RouterProfileDefaults.DefaultBaseUrl, resolved.BaseUrl);
            Assert.Equal(RouterProfileDefaults.DefaultModel, resolved.Model);
            Assert.Equal(1, resolved.MaxConcurrency);
            Assert.Equal(0, resolved.DelayBetweenBatchesMs);
            Assert.Equal(0, resolved.JitterMs);
            Assert.True(resolved.BatchSize >= 1);
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
    public async Task TranslationQueueWorker_RequestPauseBeforeRunStarts_PausesBeforeIssuingHttpRequest()
    {
        // Arrange
        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        SqliteSubtitleEntryRepository repo = new(factory);

        await repo.BulkUpsertAsync(
        [
            new SubtitleEntry(0, "k1", "dialog.json", null, "One", "One", new Dictionary<string, string>(), null, null, EntryStatus.Pending, false),
            new SubtitleEntry(0, "k2", "dialog.json", null, "Two", "Two", new Dictionary<string, string>(), null, null, EntryStatus.Pending, false)
        ]);

        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson("""{"k1":"Satu","k2":"Dua"}""");

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        TranslationQueueWorker worker = new(provider, repo, delayProvider: new FakeDelayProvider());
        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            BatchSize = 2,
            DelayBetweenBatchesMs = 0,
            JitterMs = 0
        };

        // Act
        worker.RequestPause();
        TranslationRunResult pausedRun = await worker.TranslatePendingAsync("System", profile);
        int httpRequestsAfterPause = handler.Requests.Count;
        TranslationRunResult resumedRun = await worker.TranslatePendingAsync("System", profile);

        // Assert
        Assert.Equal(TranslationRunStatus.PausedByUser, pausedRun.Status);
        Assert.Equal(0, pausedRun.ProcessedThisRun);
        Assert.Equal(2, pausedRun.RemainingPendingCount);
        Assert.Equal(0, httpRequestsAfterPause);

        Assert.Equal(TranslationRunStatus.Completed, resumedRun.Status);
        Assert.Equal(2, resumedRun.ProcessedThisRun);
        Assert.Single(handler.Requests);
        Assert.Empty(await repo.GetPendingAsync());
    }

    [Fact]
    public async Task TranslationQueueWorker_HallucinatedOrDuplicateTokenInLlmReply_MarksFailedValidationWithRawFallback()
    {
        // Arrange
        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        SqliteSubtitleEntryRepository repo = new(factory);
        TextSanitizer sanitizer = new();

        (string masked1, Dictionary<string, string> ph1) = sanitizer.Mask("<b>Hello</b>");
        (string masked2, Dictionary<string, string> ph2) = sanitizer.Mask("Plain");

        await repo.BulkUpsertAsync(
        [
            new SubtitleEntry(0, "k1", "dialog.json", null, "<b>Hello</b>", masked1, ph1, null, null, EntryStatus.Pending, false),
            new SubtitleEntry(0, "k2", "dialog.json", null, "Plain", masked2, ph2, null, null, EntryStatus.Pending, false)
        ]);

        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson("""
            {
              "k1": "[TAG_0]Halo[TAG_1] [TAG_99]",
              "k2": "Polos [VAR_0]"
            }
            """);

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        TranslationQueueWorker worker = new(provider, repo, sanitizer: sanitizer, delayProvider: new FakeDelayProvider());
        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            BatchSize = 10,
            DelayBetweenBatchesMs = 0,
            JitterMs = 0
        };

        // Act
        TranslationRunResult result = await worker.TranslatePendingAsync("System", profile);

        // Assert
        Assert.Equal(TranslationRunStatus.Completed, result.Status);
        Assert.Equal(2, result.FailedValidationCount);

        SubtitleEntry? row1 = await repo.GetByEntryKeyAsync("k1");
        SubtitleEntry? row2 = await repo.GetByEntryKeyAsync("k2");
        Assert.NotNull(row1);
        Assert.NotNull(row2);
        Assert.Equal(EntryStatus.FailedValidation, row1!.Status);
        Assert.Equal("<b>Hello</b>", row1.FinalPatchedText);
        Assert.Equal(EntryStatus.FailedValidation, row2!.Status);
        Assert.Equal("Plain", row2.FinalPatchedText);
    }

    [Fact]
    public async Task TranslationQueueWorker_CustomProviderThrows429HttpRequestException_TriggersAutoPause()
    {
        // Arrange
        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        SqliteSubtitleEntryRepository repo = new(factory);

        await repo.BulkUpsertAsync(
        [
            new SubtitleEntry(0, "k1", "dialog.json", null, "One", "One", new Dictionary<string, string>(), null, null, EntryStatus.Pending, false),
            new SubtitleEntry(0, "k2", "dialog.json", null, "Two", "Two", new Dictionary<string, string>(), null, null, EntryStatus.Pending, false)
        ]);

        ThrowingOnSecondBatchProvider provider = new();
        TranslationQueueWorker worker = new(provider, repo, delayProvider: new FakeDelayProvider());
        TranslationAutoPausedEventArgs? pauseArgs = null;
        worker.AutoPaused += (_, args) => pauseArgs = args;

        RouterProfile profile = RouterProfileDefaults.CreateDefaultProfile() with
        {
            BatchSize = 1,
            DelayBetweenBatchesMs = 0,
            JitterMs = 0
        };

        // Act
        TranslationRunResult result = await worker.TranslatePendingAsync("System", profile);

        // Assert
        Assert.Equal(TranslationRunStatus.AutoPaused, result.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, result.AutoPauseStatusCode);
        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(1, result.RemainingPendingCount);
        Assert.NotNull(pauseArgs);
        Assert.Equal(HttpStatusCode.TooManyRequests, pauseArgs!.StatusCode);
        Assert.Single(await repo.GetPendingAsync());
    }

    private sealed class ThrowingOnSecondBatchProvider : ITranslatorProvider
    {
        private int _calls;

        public Task<Dictionary<string, string>> TranslateBatchAsync(
            Dictionary<string, string> maskedBatch,
            string systemPrompt,
            RouterProfile profile,
            CancellationToken ct)
        {
            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(maskedBatch.ToDictionary(kv => kv.Key, kv => $"ID:{kv.Value}", StringComparer.Ordinal));
            }

            throw new HttpRequestException("limit", null, HttpStatusCode.TooManyRequests);
        }
    }
}
