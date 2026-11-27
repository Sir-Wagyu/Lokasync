using System.Net;
using System.Text.Json;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers3_4;

public sealed class PairwiseCoreSanitizerTranslatorTests
{
    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F1,F2,F3,F5")]
    public async Task SqliteFactoryAndRepos_CreateProjectAndBulkInsertMaskedEntries_PersistsSchemaAndQueriesPending()
    {
        // Arrange
        using TempWorkspace ws = new("t3_01_sqlite_mask_bulk");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        const string projectId = "proj-t3-01";
        await Tiers3_4TestHelper.SeedProjectAsync(
            db,
            projectId,
            "Expedition Zero",
            ws.GameRootPath,
            "Loose",
            """{"game_title":"Expedition Zero","genre_tone":"Sci-Fi","glossary":[]}""");

        List<SubtitleEntry> entries = Enumerable.Range(1, 150)
            .Select(i =>
            {
                string raw = $"<color=#FFD700><b>Hero</b></color> #{i}: Welcome {{player_name}}, sector %s\r\n\tReady?";
                (string masked, Dictionary<string, string> placeholders) = sanitizer.Mask(raw);
                return Tiers3_4TestHelper.CreateEntry(
                    0,
                    $"k_{i:D3}",
                    $"dialog_{i % 3}.json",
                    raw,
                    maskedText: masked,
                    placeholders: placeholders,
                    status: EntryStatus.Pending);
            })
            .ToList();

        // Act
        await db.InsertSubtitleEntriesAsync(entries);
        IReadOnlyList<SubtitleEntry> pending = await db.GetPendingEntriesAsync();

        // Assert
        E2EAssertions.AssertSqliteSchemaValid(db.Connection);
        await using var projCmd = db.Connection.CreateCommand();
        projCmd.CommandText = "SELECT game_title FROM projects WHERE id = @id LIMIT 1;";
        projCmd.Parameters.AddWithValue("@id", projectId);
        Assert.Equal("Expedition Zero", Convert.ToString(await projCmd.ExecuteScalarAsync()));

        Assert.Equal(150, pending.Count);
        Assert.All(pending, e =>
        {
            Assert.Equal(EntryStatus.Pending, e.Status);
            Assert.Contains("[TAG_0]", e.MaskedText, StringComparison.Ordinal);
            Assert.Contains("[VAR_0]", e.MaskedText, StringComparison.Ordinal);
            Assert.Contains("[NL_0]", e.MaskedText, StringComparison.Ordinal);
            Assert.Contains("[TAB_0]", e.MaskedText, StringComparison.Ordinal);
            Assert.NotEmpty(e.Placeholders);
            Assert.True(sanitizer.TryUnmask(e.MaskedText, e.Placeholders, out string roundTrip));
            Assert.Equal(e.RawText, roundTrip);
        });
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F5,F6,F7,F8")]
    public async Task SanitizerAndPromptTranslator_MaskedGlossaryDialogThroughHttpProvider_PreservesTokensAndAppliesGlossary()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        const string rawText = "<color=#FFD700>The Warden</color> guards {0}.\nUse Aether Core.";
        (string maskedText, Dictionary<string, string> placeholders) = sanitizer.Mask(rawText);

        string systemPrompt = bridge.BuildSystemPrompt(
            "Expedition Zero",
            "Sci-Fi Thriller",
            [
                ("The Warden", "Sang Sipir", "Antagonis utama"),
                ("Aether Core", "Inti Aether", "Sumber energi")
            ]);

        MockLlmRouterHandler mockRouter = new();
        mockRouter.EnqueueBatchTranslator(batch =>
        {
            string val = batch["line_01"];
            // Translate while preserving every placeholder token exactly
            string translated = val
                .Replace("The Warden", "Sang Sipir", StringComparison.Ordinal)
                .Replace("guards", "menjaga", StringComparison.Ordinal)
                .Replace("Use Aether Core.", "Gunakan Inti Aether.", StringComparison.Ordinal);
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["line_01"] = translated };
        });

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(
            baseUrl: "http://127.0.0.1:8045/v1/chat/completions",
            apiKey: "sk-t3-02",
            model: "gemini-pro");

        // Act
        Dictionary<string, string> response = await translator.TranslateBatchAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["line_01"] = maskedText },
            systemPrompt,
            profile,
            CancellationToken.None);

        bool unmaskedOk = sanitizer.TryUnmask(response["line_01"], placeholders, out string finalPatched);

        // Assert
        CapturedRequest captured = Assert.Single(mockRouter.Requests);
        Assert.Contains("Expedition Zero", captured.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Sci-Fi Thriller", captured.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("The Warden", captured.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Sang Sipir", captured.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Aether Core", captured.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Inti Aether", captured.SystemPrompt, StringComparison.Ordinal);

        Assert.NotNull(captured.ParsedInputBatch);
        string sentMasked = captured.ParsedInputBatch!["line_01"];
        Assert.Contains("[TAG_0]", sentMasked, StringComparison.Ordinal);
        Assert.Contains("[TAG_1]", sentMasked, StringComparison.Ordinal);
        Assert.Contains("[VAR_0]", sentMasked, StringComparison.Ordinal);
        Assert.Contains("[NL_0]", sentMasked, StringComparison.Ordinal);
        Assert.DoesNotContain("<color=", sentMasked, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", sentMasked, StringComparison.Ordinal);

        Assert.True(unmaskedOk);
        Assert.Equal("<color=#FFD700>Sang Sipir</color> menjaga {0}.\nGunakan Inti Aether.", finalPatched);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F6,F8,F9,F10")]
    public async Task TranslatorJsonAndSplitRetry_MarkdownFenceAndMissingKeyThenRecovery_UnmasksAllEntriesCleanly()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        (string Key, string Raw)[] sourceItems =
        [
            ("k1", "<b>One</b> {0}"),
            ("k2", "<i>Two</i> {1}"),
            ("k3", "Three\nLine"),
            ("k4", "Four\tTab")
        ];

        Dictionary<string, (string Masked, Dictionary<string, string> Placeholders)> maskedMap =
            sourceItems.ToDictionary(
                x => x.Key,
                x => sanitizer.Mask(x.Raw),
                StringComparer.Ordinal);

        Dictionary<string, string> inputBatch = maskedMap.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Masked,
            StringComparer.Ordinal);

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        // Initial 4-key batch drops 1 key -> triggers split-and-retry; split chunks use markdown & prose wrappers
        mockRouter.EnqueueMissingKeyResponse(dropCount: 1);
        mockRouter.EnqueueMarkdownFencedBatch(batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        mockRouter.EnqueueProseWrappedBatch(batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 4);

        // Act
        Dictionary<string, string> translated = await translator.TranslateBatchAsync(
            inputBatch,
            "Translate preserving tokens.",
            profile,
            CancellationToken.None);

        // Assert
        Assert.True(mockRouter.Requests.Count >= 2, $"Expected split-and-retry >= 2 HTTP requests, got {mockRouter.Requests.Count}.");
        Assert.Equal(4, translated.Count);
        foreach ((string key, string raw) in sourceItems)
        {
            Assert.True(translated.TryGetValue(key, out string? translatedMasked));
            Assert.True(sanitizer.TryUnmask(translatedMasked!, maskedMap[key].Placeholders, out string unmasked));
            Assert.Equal($"[ID] {raw}", unmasked);
        }
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F5,F6,F9,F10,F19")]
    public async Task SanitizerAndSplitRetry_ProseWrappedJsonCorruptsOneToken_MarksOnlyCorruptRowFailedValidation()
    {
        // Arrange
        using TempWorkspace ws = new("t3_04_sanitizer_split_retry");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        await Tiers3_4TestHelper.SeedProjectAsync(db, "proj-t3-04", "Split Retry", ws.GameRootPath, "Loose");

        List<SubtitleEntry> entries = Tiers3_4TestHelper.MaskEntries(sanitizer, [
            Tiers3_4TestHelper.CreateEntry(0, "k1", "a.json", "<b>Hi</b>"),
            Tiers3_4TestHelper.CreateEntry(0, "k2", "a.json", "Gold: {gold}"),
            Tiers3_4TestHelper.CreateEntry(0, "k3", "a.json", "Run!"), // len 4 -> 8 chars is >25%
            Tiers3_4TestHelper.CreateEntry(0, "k4", "a.json", "Ok")
        ]);
        await db.InsertSubtitleEntriesAsync(entries);

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => MapCorruptBatchValue(kv.Value), StringComparer.Ordinal)
        };
        // Trigger split-and-retry once, then return prose-wrapped JSON with corrupt token on k2 & overflow on k3
        mockRouter.EnqueueMissingKeyResponse(dropCount: 1);
        mockRouter.EnqueueProseWrappedBatch(b => b.ToDictionary(kv => kv.Key, kv => MapCorruptBatchValue(kv.Value), StringComparer.Ordinal));
        mockRouter.EnqueueProseWrappedBatch(b => b.ToDictionary(kv => kv.Key, kv => MapCorruptBatchValue(kv.Value), StringComparer.Ordinal));

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        SequentialWorkerHandle worker = bridge.CreateSequentialWorker(translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 4);

        // Act
        await Tiers3_4TestHelper.RunWorkerAsync(worker, db, profile, "Translate.", CancellationToken.None);
        IReadOnlyList<SubtitleEntry> validated = await db.GetAllEntriesAsync();

        // Assert
        Assert.True(mockRouter.Requests.Count >= 2);
        Assert.Equal(4, validated.Count);

        SubtitleEntry e1 = validated.Single(e => e.EntryKey == "k1");
        SubtitleEntry e2 = validated.Single(e => e.EntryKey == "k2");
        SubtitleEntry e3 = validated.Single(e => e.EntryKey == "k3");
        SubtitleEntry e4 = validated.Single(e => e.EntryKey == "k4");

        Assert.Equal(EntryStatus.Translated, e1.Status);
        Assert.Equal("<b>Hai</b>", e1.FinalPatchedText);
        Assert.False(e1.HasOverflowWarning);

        Assert.Equal(EntryStatus.FailedValidation, e2.Status);
        Assert.Equal(e2.RawText, e2.FinalPatchedText);
        Assert.False(e2.HasOverflowWarning);

        Assert.Equal(EntryStatus.Translated, e3.Status);
        Assert.Equal("Lari skg", e3.FinalPatchedText);
        Assert.True(e3.HasOverflowWarning);

        Assert.Equal(EntryStatus.Translated, e4.Status);
        Assert.Equal("Ya", e4.FinalPatchedText);
        Assert.False(e4.HasOverflowWarning);

        Assert.All(validated, E2EAssertions.AssertOverflowWarningRule);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F1,F3,F11,F12")]
    public async Task SequentialWorkerAndSqlite_Http429AutoPauseThenResume_CheckpointsCompletedAndResumesOnlyPending()
    {
        // Arrange
        using TempWorkspace ws = new("t3_05_worker_429_resume");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        const string projectId = "proj-t3-05";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "Worker 429", ws.GameRootPath, "Loose");

        List<SubtitleEntry> seedEntries = Enumerable.Range(1, 6)
            .Select(i => Tiers3_4TestHelper.CreateEntry(0, $"k{i}", "a.json", $"Line {i}", maskedText: $"Line {i}"))
            .ToList();
        await db.InsertSubtitleEntriesAsync(seedEntries);

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mockRouter.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")); // Batch 1 -> 200
        mockRouter.EnqueueHttpStatus(HttpStatusCode.TooManyRequests); // Batch 2 -> 429

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        List<string> pauseEvents = [];
        SequentialWorkerHandle worker = bridge.CreateSequentialWorker(
            translator,
            sanitizer,
            db,
            onAutoPause: msg => pauseEvents.Add(msg));
        List<AutoPauseNotification> detailedPauses = Tiers3_4TestHelper.AttachDetailedAutoPauseListener(worker);

        RouterProfile profile = MockLlmRouterHandler.CreateProfile(
            maxConcurrency: 4,
            delayBetweenBatchesMs: 60,
            jitterMs: 10,
            batchSize: 2);

        // Act — Run until Auto-Pause on Batch 2
        try
        {
            await Tiers3_4TestHelper.RunWorkerAsync(worker, db, profile, "Translate", CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Some worker designs raise on pause after checkpointing, others return normally; event & SQLite verify state.
        }

        int callsAfterPause = mockRouter.Requests.Count;
        IReadOnlyList<SubtitleEntry> midAll = await db.GetAllEntriesAsync();
        IReadOnlyList<SubtitleEntry> midPending = await db.GetPendingEntriesAsync();

        // Resume remaining Pending entries
        await Tiers3_4TestHelper.ResumeWorkerAsync(worker, db, profile, "Translate", CancellationToken.None);

        // Assert
        Assert.Equal(2, callsAfterPause);
        Assert.True(pauseEvents.Count + detailedPauses.Count > 0, "Expected Auto-Pause event on HTTP 429.");
        Assert.Equal(2, midAll.Count(e => e.Status == EntryStatus.Translated));
        Assert.Equal(4, midPending.Count);

        IReadOnlyList<SubtitleEntry> finalAll = await db.GetAllEntriesAsync();
        Assert.Equal(6, finalAll.Count);
        Assert.All(finalAll, e => Assert.Equal(EntryStatus.Translated, e.Status));

        // Total HTTP calls: Batch 1 + failed Batch 2 + resumed Batch 2 + resumed Batch 3 = 4
        Assert.Equal(4, mockRouter.Requests.Count);
        IReadOnlyList<string> postResumeKeys = mockRouter.Requests.Skip(2)
            .SelectMany(r => r.ParsedInputBatch?.Keys ?? Enumerable.Empty<string>())
            .ToList();
        Assert.Equal(4, postResumeKeys.Count);
        Assert.DoesNotContain("k1", postResumeKeys);
        Assert.DoesNotContain("k2", postResumeKeys);
        Assert.Equal(1, mockRouter.MaxObservedConcurrency);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F2,F3,F11,F12")]
    public async Task SequentialWorkerAndProjectRepo_Http401And403PauseAndProjectContextUpdate_PreservesStateAcrossReload()
    {
        // Arrange
        foreach (HttpStatusCode authCode in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            using TempWorkspace ws = new($"t3_06_auth_{(int)authCode}");
            OpaqueContractBridge bridge = new();
            const string projectId = "proj-t3-06";
            MockLlmRouterHandler mockRouter = new()
            {
                DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
            };
            mockRouter.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
            mockRouter.EnqueueHttpStatus(authCode);

            RouterProfile initialProfile = MockLlmRouterHandler.CreateProfile(apiKey: "sk-expired", batchSize: 2);

            await using (DatabaseSession db1 = await bridge.OpenDatabaseAsync(ws.ConnectionString))
            {
                await Tiers3_4TestHelper.SeedProjectAsync(
                    db1,
                    projectId,
                    "Auth Test",
                    ws.GameRootPath,
                    "Loose",
                    """{"game_title":"Auth Test","genre_tone":"Initial","glossary":[]}""");

                List<SubtitleEntry> rows = Enumerable.Range(1, 4)
                    .Select(i => Tiers3_4TestHelper.CreateEntry(0, $"k{i}", "a.json", $"Text {i}", maskedText: $"Text {i}"))
                    .ToList();
                await db1.InsertSubtitleEntriesAsync(rows);

                ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
                ITranslatorProvider translator1 = bridge.CreateTranslatorProvider(mockRouter);
                SequentialWorkerHandle worker1 = bridge.CreateSequentialWorker(translator1, sanitizer, db1);

                try
                {
                    await Tiers3_4TestHelper.RunWorkerAsync(worker1, db1, initialProfile, "Translate", CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Auto-Pause via event or exception
                }
            }

            // Act — Reopen DB session, update narrative_context_json, hot-swap ApiKey, and resume
            await using DatabaseSession db2 = await bridge.OpenDatabaseAsync(ws.ConnectionString);
            const string updatedContext = """{"game_title":"Auth Test","genre_tone":"Updated Tone","glossary":[{"source":"A","target":"B","note":""}]}""";
            await Tiers3_4TestHelper.SeedProjectAsync(db2, projectId, "Auth Test", ws.GameRootPath, "Loose", updatedContext);

            IReadOnlyList<SubtitleEntry> reloadedMid = await db2.GetAllEntriesAsync();
            Assert.Equal(2, reloadedMid.Count(e => e.Status == EntryStatus.Translated));
            Assert.Equal(2, reloadedMid.Count(e => e.Status == EntryStatus.Pending));

            ITranslatorProvider translator2 = bridge.CreateTranslatorProvider(mockRouter);
            SequentialWorkerHandle worker2 = bridge.CreateSequentialWorker(translator2, bridge.CreateTextSanitizer(), db2);
            RouterProfile updatedProfile = initialProfile with { ApiKey = "sk-fresh-token" };
            await Tiers3_4TestHelper.RunWorkerAsync(worker2, db2, updatedProfile, "Translate", CancellationToken.None);

            // Assert
            Assert.Equal(updatedContext, await Tiers3_4TestHelper.QueryProjectNarrativeContextJsonAsync(db2.Connection, projectId));
            IReadOnlyList<SubtitleEntry> finalEntries = await db2.GetAllEntriesAsync();
            Assert.Equal(4, finalEntries.Count);
            Assert.All(finalEntries, e => Assert.Equal(EntryStatus.Translated, e.Status));
            Assert.Equal("Bearer sk-fresh-token", mockRouter.Requests.Last().Authorization);
            Assert.Equal(3, mockRouter.Requests.Count);
        }
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F3,F6,F19")]
    public async Task SqliteRepository_ManuallyEditedAndFailedValidationAndOverflow_RoundTripsStatusAndFlagsDeterministically()
    {
        // Arrange
        using TempWorkspace ws = new("t3_07_status_roundtrip");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        await Tiers3_4TestHelper.SeedProjectAsync(db, "proj-t3-07", "RoundTrip", ws.GameRootPath, "Loose");

        (string m2, Dictionary<string, string> p2) = sanitizer.Mask("<b>Ok</b>");
        Assert.False(sanitizer.TryUnmask("Broken without tags", p2, out _));

        (string m4, Dictionary<string, string> p4) = sanitizer.Mask("<i>Key</i>");
        Assert.True(sanitizer.TryUnmask("[TAG_0]Kunci Utama[TAG_1]", p4, out string unmasked4));

        await db.InsertSubtitleEntriesAsync([
            Tiers3_4TestHelper.CreateEntry(0, "normal", "a.json", "Hello", translatedText: "Halo", finalPatchedText: "Halo", status: EntryStatus.Translated, hasOverflowWarning: false),
            Tiers3_4TestHelper.CreateEntry(0, "overflow", "a.json", "1234", translatedText: "123456", finalPatchedText: "123456", status: EntryStatus.Translated, hasOverflowWarning: true),
            Tiers3_4TestHelper.CreateEntry(0, "failed", "a.json", "<b>Ok</b>", maskedText: m2, placeholders: p2, translatedText: "Broken without tags", finalPatchedText: "<b>Ok</b>", status: EntryStatus.FailedValidation, hasOverflowWarning: false),
            Tiers3_4TestHelper.CreateEntry(0, "edited", "a.json", "<i>Key</i>", maskedText: m4, placeholders: p4, translatedText: "[TAG_0]Kunci Utama[TAG_1]", finalPatchedText: unmasked4, status: EntryStatus.ManuallyEdited, hasOverflowWarning: true)
        ]);

        // Also insert raw SQL row with legacy 'EDITED' token to verify deterministic read compatibility
        await using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subtitle_entries (entry_key, source_file, asset_path, raw_text, masked_text, placeholders_json, translated_text, final_patched_text, status, has_overflow_warning)
                VALUES ('legacy_edited', 'a.json', NULL, 'Start', 'Start', '{}', 'Mulai', 'Mulai', 'EDITED', 0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Act
        IReadOnlyList<SubtitleEntry> reloaded = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(5, reloaded.Count);
        SubtitleEntry rNormal = reloaded.Single(e => e.EntryKey == "normal");
        SubtitleEntry rOverflow = reloaded.Single(e => e.EntryKey == "overflow");
        SubtitleEntry rFailed = reloaded.Single(e => e.EntryKey == "failed");
        SubtitleEntry rEdited = reloaded.Single(e => e.EntryKey == "edited");
        SubtitleEntry rLegacy = reloaded.Single(e => e.EntryKey == "legacy_edited");

        Assert.Equal(EntryStatus.Translated, rNormal.Status);
        Assert.False(rNormal.HasOverflowWarning);

        Assert.Equal(EntryStatus.Translated, rOverflow.Status);
        Assert.True(rOverflow.HasOverflowWarning);

        Assert.Equal(EntryStatus.FailedValidation, rFailed.Status);
        Assert.False(rFailed.HasOverflowWarning);

        Assert.Equal(EntryStatus.ManuallyEdited, rEdited.Status);
        Assert.True(rEdited.HasOverflowWarning);

        Assert.Equal(EntryStatus.ManuallyEdited, rLegacy.Status);

        E2EAssertions.AssertOverflowWarningRule(rNormal);
        E2EAssertions.AssertOverflowWarningRule(rOverflow);
        E2EAssertions.AssertOverflowWarningRule(rFailed);
        E2EAssertions.AssertOverflowWarningRule(rEdited);
        E2EAssertions.AssertOverflowWarningRule(rLegacy);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F7,F8,F11,F20")]
    public async Task ProfilesConfigAndPromptTranslator_LoadSavedProfileAndBuildPrompt_AppliesCustomEndpointAndRateLimits()
    {
        // Arrange
        using TempWorkspace ws = new("t3_08_profiles_worker");
        using TempWorkspace dbWs = new("t3_08_db");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(dbWs.ConnectionString);
        await Tiers3_4TestHelper.SeedProjectAsync(db, "proj-t3-08", "Ollama Game", ws.GameRootPath, "Loose");

        RouterProfile savedProfile = new(
            BaseUrl: "http://127.0.0.1:11434/v1/chat/completions",
            ApiKey: "sk-custom",
            Model: "llama3.1",
            MaxConcurrency: 3,
            DelayBetweenBatchesMs: 80,
            JitterMs: 10,
            BatchSize: 2);

        await bridge.SaveProfilesAsync(
            ws.ProfilesJsonPath,
            "ollama_local",
            new Dictionary<string, RouterProfile>(StringComparer.Ordinal)
            {
                ["ollama_local"] = savedProfile
            });

        (string activeName, IReadOnlyDictionary<string, RouterProfile> loadedMap) =
            await bridge.LoadProfilesAsync(ws.ProfilesJsonPath);
        RouterProfile activeProfile = loadedMap[activeName];

        List<SubtitleEntry> entries = Enumerable.Range(1, 4)
            .Select(i => Tiers3_4TestHelper.CreateEntry(0, $"k{i}", "a.json", $"Dialog {i}", maskedText: $"Dialog {i}"))
            .ToList();
        await db.InsertSubtitleEntriesAsync(entries);

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        SequentialWorkerHandle worker = bridge.CreateSequentialWorker(translator, sanitizer, db);

        string systemPrompt = bridge.BuildSystemPrompt(
            "Ollama Game",
            "Casual",
            [("Core", "Inti", "Tech term")]);

        // Act
        await Tiers3_4TestHelper.RunWorkerAsync(worker, db, activeProfile, systemPrompt, CancellationToken.None);

        // Assert
        Assert.Equal("ollama_local", activeName);
        Assert.Equal(2, mockRouter.Requests.Count);
        Assert.All(mockRouter.Requests, r =>
        {
            Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", r.RequestUri?.ToString());
            Assert.Equal("Bearer sk-custom", r.Authorization);
            Assert.Equal("llama3.1", r.Model);
            Assert.Contains("Core", r.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("Inti", r.SystemPrompt, StringComparison.Ordinal);
        });
        E2EAssertions.AssertSequentialExecution(mockRouter.Requests, mockRouter.MaxObservedConcurrency, minDelayMs: 60);
    }

    private static string MapCorruptBatchValue(string maskedValue)
    {
        if (maskedValue.Contains("Hi", StringComparison.Ordinal))
        {
            return maskedValue.Replace("Hi", "Hai", StringComparison.Ordinal);
        }

        if (maskedValue.Contains("[VAR_0]", StringComparison.Ordinal))
        {
            return maskedValue.Replace("[VAR_0]", "[var_0]", StringComparison.Ordinal);
        }

        if (string.Equals(maskedValue, "Run!", StringComparison.Ordinal))
        {
            return "Lari skg";
        }

        return "Ya";
    }
}
