using System.IO;
using System.Net.Http;
using System.Text.Json;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F19_SafeFallbackAndOverflowTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public async Task Pipeline_ValidTranslationWithinLengthLimit_SetsTranslatedAndNoOverflowWarning()
    {
        // Arrange
        using TempWorkspace ws = new("f19_valid_no_overflow");
        const string rawText = "Hello, {0}! Welcome."; // len 20 -> 25% max = 25
        const string translatedText = "Halo, {0}! Selamat dtg"; // len 22 -> 22*4=88 <= 20*5=100
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new { line_1 = rawText }));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f19-t1-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Overflow Test", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                _ => "Halo, [VAR_0]! Selamat dtg")
        };

        // Act
        List<SubtitleEntry> persisted = await RunSanitizeTranslateAndFinalizeAsync(
            bridge, db, ws, mockRouter, projectId);

        // Assert
        SubtitleEntry entry = Assert.Single(persisted);
        Assert.Equal(EntryStatus.Translated, entry.Status);
        Assert.Equal(translatedText, entry.FinalPatchedText);
        Assert.False(entry.HasOverflowWarning);
        E2EAssertions.AssertOverflowWarningRule(entry);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public async Task Pipeline_TranslationExceeding25Percent_FlagsHasOverflowWarningTrue()
    {
        // Arrange
        using TempWorkspace ws = new("f19_overflow_true");
        const string rawText = "Start Game"; // len 10 -> 10*5=50
        const string longTranslation = "Mulai Permainan..."; // len 18 -> 18*4=72 > 50
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new { ui_start = rawText }));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f19-t1-2";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Overflow True", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, _ => longTranslation)
        };

        // Act
        List<SubtitleEntry> persisted = await RunSanitizeTranslateAndFinalizeAsync(
            bridge, db, ws, mockRouter, projectId, patchAfter: true);

        // Assert
        SubtitleEntry entry = Assert.Single(persisted);
        Assert.Equal(EntryStatus.Translated, entry.Status);
        Assert.True(entry.HasOverflowWarning);
        E2EAssertions.AssertOverflowWarningRule(entry);
        Assert.Contains(longTranslation, ws.ReadTextFile("dialog.json"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public async Task Pipeline_FailedValidationDroppedPlaceholder_MarksFailedAndFallsBackToRawText()
    {
        // Arrange
        using TempWorkspace ws = new("f19_failed_drop_var");
        const string rawText = "<b>Welcome</b>, {player_name}!";
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new { greeting = rawText }));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f19-t1-3";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Fallback", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            // Drops [VAR_0] intentionally
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, _ => "[TAG_0]Selamat datang[TAG_1]!")
        };

        // Act
        List<SubtitleEntry> persisted = await RunSanitizeTranslateAndFinalizeAsync(
            bridge, db, ws, mockRouter, projectId, patchAfter: true);

        // Assert
        SubtitleEntry entry = Assert.Single(persisted);
        string patchedFile = ws.ReadTextFile("dialog.json");
        E2EAssertions.AssertFailedValidationFallback(entry, patchedFile);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public async Task Pipeline_MixedValidAndInvalidEntriesInSameBatch_PatchesValidAndFallbacksInvalid()
    {
        // Arrange
        using TempWorkspace ws = new("f19_mixed_batch");
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new
        {
            ok_1 = "Hello",
            ok_2 = "Save Game",
            bad_case = "<b>Hero</b>",
            long_ok = "Run!"
        }));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f19-t1-4";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Mixed", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch =>
            {
                Dictionary<string, string> result = new(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> kv in batch)
                {
                    if (kv.Value.Contains("[TAG_0]", StringComparison.Ordinal))
                    {
                        // Mutates case [TAG_0] -> [tag_0]
                        result[kv.Key] = kv.Value.Replace("[TAG_0]", "[tag_0]", StringComparison.Ordinal);
                    }
                    else if (kv.Value == "Run!")
                    {
                        result[kv.Key] = "Berlari sekarang!"; // >25%
                    }
                    else
                    {
                        result[kv.Key] = $"[ID] {kv.Value}";
                    }
                }

                return result;
            }
        };

        // Act
        List<SubtitleEntry> persisted = await RunSanitizeTranslateAndFinalizeAsync(
            bridge, db, ws, mockRouter, projectId, patchAfter: true);

        // Assert
        Assert.Equal(4, persisted.Count);
        SubtitleEntry badEntry = Assert.Single(persisted, e => e.RawText == "<b>Hero</b>");
        SubtitleEntry longEntry = Assert.Single(persisted, e => e.RawText == "Run!");
        Assert.Equal(EntryStatus.FailedValidation, badEntry.Status);
        Assert.Equal(EntryStatus.Translated, longEntry.Status);
        Assert.True(longEntry.HasOverflowWarning);

        string patchedDisk = ws.ReadTextFile("dialog.json");
        E2EAssertions.AssertFailedValidationFallback(badEntry, patchedDisk);
        Assert.Contains("Berlari sekarang!", patchedDisk, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public async Task Pipeline_FailedValidationPersistedToSqlite_RetainsStatusAndSafeStateAfterReload()
    {
        // Arrange
        using TempWorkspace ws = new("f19_sqlite_reload");
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new
        {
            broken = "<i>Item</i>: {0}",
            overflow = "Go"
        }));

        OpaqueContractBridge bridge = new();
        const string projectId = "proj-f19-t1-5";
        await using (DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString))
        {
            await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Reload", ws.GameRootPath, "Loose");
            MockLlmRouterHandler mockRouter = new()
            {
                DefaultBatchTranslator = batch => batch.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Contains("[TAG_0]", StringComparison.Ordinal)
                        ? "Barang rusak" // missing tags/var
                        : "Berangkat sekarang!") // >25%
            };

            await RunSanitizeTranslateAndFinalizeAsync(bridge, db, ws, mockRouter, projectId);
        }

        // Act
        await using DatabaseSession reloadedDb = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        IReadOnlyList<SubtitleEntry> reloaded = await reloadedDb.GetAllEntriesAsync();

        // Assert
        SubtitleEntry broken = Assert.Single(reloaded, e => e.RawText == "<i>Item</i>: {0}");
        SubtitleEntry overflow = Assert.Single(reloaded, e => e.RawText == "Go");
        Assert.Equal(EntryStatus.FailedValidation, broken.Status);
        Assert.False(broken.HasOverflowWarning);
        Assert.Equal(EntryStatus.Translated, overflow.Status);
        Assert.True(overflow.HasOverflowWarning);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public async Task Pipeline_ManuallyEditedEntryFixed_ClearsFailedValidationAndAppliesNewOverflowCheck()
    {
        // Arrange
        using TempWorkspace ws = new("f19_manual_fix");
        const string rawText = "<b>Welcome</b>, {player_name}!";
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new { line_1 = rawText }));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f19-t1-6";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Manual Fix", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, _ => "Broken without tokens")
        };

        List<SubtitleEntry> initial = await RunSanitizeTranslateAndFinalizeAsync(
            bridge, db, ws, mockRouter, projectId);
        SubtitleEntry failed = Assert.Single(initial);
        Assert.Equal(EntryStatus.FailedValidation, failed.Status);

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(new MockLlmRouterHandler());
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        IEngineAdapter looseAdapter = bridge.GetEngineAdapter("Loose");
        PipelineOrchestratorHandle orchestrator = AdaptersBackupUiTestHelper.CreatePipelineOrchestratorSafe(
            bridge, db, sanitizer, translator, backupService, [looseAdapter], ws.BackupRootPath);

        // Act
        // Verify TryUnmask + overflow rule on concise manual fix ("<b>Hai</b>, {player_name}!" len 26 <= 30*1.25=37.5)
        bool unmasked = sanitizer.TryUnmask("[TAG_0]Hai[TAG_1], [VAR_0]!", failed.Placeholders, out string unmaskedText);
        bool overflow = AdaptersBackupUiTestHelper.ComputeExpectedOverflowWarning(failed.RawText, unmaskedText);
        SubtitleEntry edited = failed with
        {
            TranslatedText = "[TAG_0]Hai[TAG_1], [VAR_0]!",
            FinalPatchedText = unmaskedText,
            Status = unmasked ? EntryStatus.ManuallyEdited : EntryStatus.FailedValidation,
            HasOverflowWarning = overflow
        };
        await AdaptersBackupUiTestHelper.UpdateSubtitleEntriesAsync(db, [edited]);
        await AdaptersBackupUiTestHelper.TryInvokePipelineAsync(
            orchestrator,
            ["OpenProject"],
            projectId: projectId,
            gameRootPath: ws.GameRootPath,
            adapter: looseAdapter,
            entries: [edited]);
        await AdaptersBackupUiTestHelper.InvokePipelineAsync(
            orchestrator,
            ["PatchGame", "Patch", "RePatch"],
            projectId: projectId,
            gameRootPath: ws.GameRootPath,
            adapter: looseAdapter,
            entries: [edited]);

        // Assert
        IReadOnlyList<SubtitleEntry> reloaded = await db.GetAllEntriesAsync();
        SubtitleEntry fixedEntry = Assert.Single(reloaded);
        Assert.Equal(EntryStatus.ManuallyEdited, fixedEntry.Status);
        Assert.False(fixedEntry.HasOverflowWarning);
        Assert.Equal("<b>Hai</b>, {player_name}!", fixedEntry.FinalPatchedText);
        Assert.Contains("<b>Hai</b>, {player_name}!", ws.ReadTextFile("dialog.json"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public async Task OverflowCheck_Exact25PercentBoundary_4To5Chars_ReturnsFalse()
    {
        // Arrange
        using TempWorkspace ws = new("f19_bva_4to5");
        ws.WriteTextFile("dialog.json", """{"k":"1234"}""");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f19-t2-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "BVA 4-5", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, _ => "12345") // 5 * 4 = 20 <= 4 * 5 = 20
        };

        // Act
        List<SubtitleEntry> persisted = await RunSanitizeTranslateAndFinalizeAsync(
            bridge, db, ws, mockRouter, projectId);

        // Assert
        SubtitleEntry entry = Assert.Single(persisted);
        Assert.False(entry.HasOverflowWarning);
        E2EAssertions.AssertOverflowWarningRule(entry);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public async Task OverflowCheck_JustOver25PercentBoundary_4To6Chars_ReturnsTrue()
    {
        // Arrange
        using TempWorkspace ws = new("f19_bva_4to6");
        ws.WriteTextFile("dialog.json", """{"k":"1234"}""");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f19-t2-2";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "BVA 4-6", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, _ => "123456") // 6 * 4 = 24 > 4 * 5 = 20
        };

        // Act
        List<SubtitleEntry> persisted = await RunSanitizeTranslateAndFinalizeAsync(
            bridge, db, ws, mockRouter, projectId);

        // Assert
        SubtitleEntry entry = Assert.Single(persisted);
        Assert.True(entry.HasOverflowWarning);
        E2EAssertions.AssertOverflowWarningRule(entry);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public async Task OverflowCheck_Exact100To125And126Boundary_EvaluatesStrictGreaterThan()
    {
        // Arrange
        using TempWorkspace ws = new("f19_bva_100");
        string raw100 = new('A', 100);
        string trans125 = new('B', 125);
        string trans126 = new('C', 126);
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new { a = raw100, b = raw100 + " " }));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        const string projectId = "proj-f19-t2-3";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "BVA 100", ws.GameRootPath, "Loose");

        // Seed exact 100-char pairs through worker directly
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch =>
            {
                Dictionary<string, string> res = new(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> kv in batch)
                {
                    res[kv.Key] = kv.Key == "exact_125" ? trans125 : trans126;
                }

                return res;
            }
        };

        List<SubtitleEntry> seed =
        [
            AdaptersBackupUiTestHelper.CreateEntry(0, "exact_125", "dialog.json", rawText: raw100, maskedText: raw100),
            AdaptersBackupUiTestHelper.CreateEntry(0, "over_126", "dialog.json", rawText: raw100, maskedText: raw100)
        ];
        await db.InsertSubtitleEntriesAsync(seed);

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        SequentialWorkerHandle worker = AdaptersBackupUiTestHelper.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 10);

        // Act
        await AdaptersBackupUiTestHelper.InvokeWorkerAsync(
            worker,
            ["Run", "Translate", "Process", "Execute"],
            systemPrompt: "Translate",
            profile: profile,
            entries: seed,
            projectId: projectId,
            db: db,
            ct: CancellationToken.None);

        // Assert
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();
        SubtitleEntry entry125 = Assert.Single(all, e => e.EntryKey == "exact_125");
        SubtitleEntry entry126 = Assert.Single(all, e => e.EntryKey == "over_126");
        Assert.False(entry125.HasOverflowWarning);
        Assert.True(entry126.HasOverflowWarning);
        E2EAssertions.AssertOverflowWarningRule(entry125);
        E2EAssertions.AssertOverflowWarningRule(entry126);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public async Task OverflowCheck_EmptyRawTextGuard_ReturnsFalseWithoutDivideByZero()
    {
        // Arrange
        using TempWorkspace ws = new("f19_empty_raw_guard");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        const string projectId = "proj-f19-t2-4";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Empty Raw Guard", ws.GameRootPath, "Loose");
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, _ => "Teks tambahan")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        SequentialWorkerHandle worker = AdaptersBackupUiTestHelper.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);

        SubtitleEntry emptyEntry = AdaptersBackupUiTestHelper.CreateEntry(
            0, "empty_raw", "dialog.json", rawText: string.Empty, maskedText: string.Empty);
        await db.InsertSubtitleEntriesAsync([emptyEntry]);

        // Act
        await AdaptersBackupUiTestHelper.InvokeWorkerAsync(
            worker,
            ["Run", "Translate", "Process", "Execute"],
            systemPrompt: "Translate",
            profile: MockLlmRouterHandler.CreateProfile(),
            entries: [emptyEntry],
            projectId: projectId,
            db: db,
            ct: CancellationToken.None);

        // Assert
        SubtitleEntry entry = Assert.Single(await db.GetAllEntriesAsync());
        Assert.False(entry.HasOverflowWarning);
        E2EAssertions.AssertOverflowWarningRule(entry);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public async Task Pipeline_FailedValidationWhoseBadLlmTextIsLongerThan25Percent_FallsBackAndDoesNotOverflow()
    {
        // Arrange
        using TempWorkspace ws = new("f19_failed_long_no_overflow");
        const string rawText = "Hi <b>!</b>";
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new { line = rawText }));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f19-t2-5";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Failed Long", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                _ => new string('X', 80)) // >25% AND drops [TAG_0]/[TAG_1]
        };

        // Act
        List<SubtitleEntry> persisted = await RunSanitizeTranslateAndFinalizeAsync(
            bridge, db, ws, mockRouter, projectId, patchAfter: true);

        // Assert
        SubtitleEntry entry = Assert.Single(persisted);
        Assert.Equal(EntryStatus.FailedValidation, entry.Status);
        Assert.False(entry.HasOverflowWarning);
        E2EAssertions.AssertFailedValidationFallback(entry, ws.ReadTextFile("dialog.json"));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public async Task Pipeline_WhitespaceMutatedPlaceholder_TriggersFailedValidationFallback()
    {
        // Arrange
        using TempWorkspace ws = new("f19_whitespace_token");
        const string rawText = "<b>Hi</b>";
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(new { line = rawText }));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f19-t2-6";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Whitespace Token", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                _ => "[TAG_ 0]Hai[TAG_1]")
        };

        // Act
        List<SubtitleEntry> persisted = await RunSanitizeTranslateAndFinalizeAsync(
            bridge, db, ws, mockRouter, projectId, patchAfter: true);

        // Assert
        SubtitleEntry entry = Assert.Single(persisted);
        string patchedContent = ws.ReadTextFile("dialog.json");
        Assert.Equal(EntryStatus.FailedValidation, entry.Status);
        Assert.DoesNotContain("[TAG_ 0]", patchedContent, StringComparison.Ordinal);
        E2EAssertions.AssertFailedValidationFallback(entry, patchedContent);
    }

    private static async Task<List<SubtitleEntry>> RunSanitizeTranslateAndFinalizeAsync(
        OpaqueContractBridge bridge,
        DatabaseSession db,
        TempWorkspace ws,
        MockLlmRouterHandler mockRouter,
        string projectId,
        bool patchAfter = false)
    {
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IEngineAdapter looseAdapter = bridge.GetEngineAdapter("Loose");
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 10);

        List<SubtitleEntry> extracted = await looseAdapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> masked = extracted.Select(e =>
        {
            (string maskedText, Dictionary<string, string> placeholders) = sanitizer.Mask(e.RawText);
            return e with { MaskedText = maskedText, Placeholders = placeholders };
        }).ToList();

        await db.InsertSubtitleEntriesAsync(masked);
        SequentialWorkerHandle worker = AdaptersBackupUiTestHelper.CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        await AdaptersBackupUiTestHelper.InvokeWorkerAsync(
            worker,
            ["Run", "Translate", "Process", "Execute"],
            systemPrompt: "Translate",
            profile: profile,
            entries: masked,
            projectId: projectId,
            db: db,
            ct: CancellationToken.None);

        List<SubtitleEntry> persisted = (await db.GetAllEntriesAsync()).ToList();

        if (patchAfter)
        {
            IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
            PipelineOrchestratorHandle orchestrator = AdaptersBackupUiTestHelper.CreatePipelineOrchestratorSafe(
                bridge, db, sanitizer, translator, backupService, [looseAdapter], ws.BackupRootPath);
            await AdaptersBackupUiTestHelper.TryInvokePipelineAsync(
                orchestrator,
                ["OpenProject"],
                projectId: projectId,
                gameRootPath: ws.GameRootPath,
                adapter: looseAdapter,
                profile: profile,
                entries: persisted);
            await AdaptersBackupUiTestHelper.InvokePipelineAsync(
                orchestrator,
                ["PatchGame", "Patch", "ApplyPatch"],
                excludedMethodSubstrings: ["RePatch"],
                projectId: projectId,
                gameRootPath: ws.GameRootPath,
                adapter: looseAdapter,
                profile: profile,
                entries: persisted);
        }

        return (await db.GetAllEntriesAsync()).ToList();
    }
}
