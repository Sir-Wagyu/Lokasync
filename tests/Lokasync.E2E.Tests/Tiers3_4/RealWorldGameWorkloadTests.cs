using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using System.Xml.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers3_4;

public sealed class RealWorldGameWorkloadTests
{
    [Fact]
    [Trait("Tier", "Tier4")]
    [Trait("Scenario", "S1")]
    public async Task FullGameWorkload_LooseFormatPipelineAndRestore_TranslatesPatchesAndRestoresOriginals()
    {
        // Arrange
        using TempWorkspace ws = new("t4_01_loose_full_game");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t4-01";
        await Tiers3_4TestHelper.SeedProjectAsync(
            db,
            projectId,
            "Arcadia Chronicles",
            ws.GameRootPath,
            "Loose",
            """{"game_title":"Arcadia Chronicles","genre_tone":"Epic Sci-Fi","glossary":[{"source":"The Warden","target":"Sang Sipir","note":"Antagonis"},{"source":"Aether Core","target":"Inti Aether","note":"Energi"}]}""");

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                kv => kv.Value
                    .Replace("The Warden", "Sang Sipir", StringComparison.Ordinal)
                    .Replace("Aether Core", "Inti Aether", StringComparison.Ordinal)
                    .Replace("Welcome", "Selamat datang", StringComparison.Ordinal)
                    .Replace("Start Expedition", "Mulai Ekspedisi", StringComparison.Ordinal),
                StringComparer.Ordinal)
        };
        mockRouter.EnqueueMarkdownFencedBatch(b => mockRouter.DefaultBatchTranslator!(b));

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);

        (IEngineAdapter? adapter, string? detectedEngine) = await bridge.DetectEngineAsync(ws.GameRootPath);
        Assert.NotNull(adapter);
        Assert.Contains("Loose", detectedEngine ?? adapter!.EngineName, StringComparison.OrdinalIgnoreCase);

        List<SubtitleEntry> extracted = await adapter!.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(sanitizer, extracted);
        await db.InsertSubtitleEntriesAsync(masked);

        string systemPrompt = bridge.BuildSystemPrompt(
            "Arcadia Chronicles",
            "Epic Sci-Fi",
            [("The Warden", "Sang Sipir", "Antagonis"), ("Aether Core", "Inti Aether", "Energi")]);

        RouterProfile profile = MockLlmRouterHandler.CreateProfile(
            maxConcurrency: 1,
            delayBetweenBatchesMs: 30,
            jitterMs: 5,
            batchSize: 2);

        SequentialWorkerHandle worker = bridge.CreateSequentialWorker(translator, sanitizer, db);

        // Act
        await Tiers3_4TestHelper.RunWorkerAsync(worker, db, profile, systemPrompt, CancellationToken.None);
        IReadOnlyList<SubtitleEntry> translatedEntries = await db.GetAllEntriesAsync();

        string[] filesToBackup = [manifest.SrtRelativePath, manifest.JsonRelativePath, manifest.XmlRelativePath];
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, filesToBackup, CancellationToken.None);
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);

        await adapter.PatchAsync(ws.GameRootPath, translatedEntries, CancellationToken.None);

        // Assert patched files preserve structure
        E2EAssertions.AssertSqliteSchemaValid(db.Connection);
        E2EAssertions.AssertSequentialExecution(mockRouter.Requests, mockRouter.MaxObservedConcurrency, minDelayMs: 25);
        Assert.All(translatedEntries, e => Assert.Equal(EntryStatus.Translated, e.Status));

        string patchedSrt = ws.ReadTextFile(manifest.SrtRelativePath);
        Assert.Contains("1\r\n00:00:01,000 --> 00:00:04,250", patchedSrt, StringComparison.Ordinal);
        Assert.Contains("<b>Selamat datang</b>, {player_name}!", patchedSrt, StringComparison.Ordinal);
        Assert.Contains("<color=#FFD700>Sang Sipir</color> guards %s.", patchedSrt, StringComparison.Ordinal);

        using JsonDocument jsonDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.JsonRelativePath));
        Assert.Equal(42, jsonDoc.RootElement.GetProperty("meta_version").GetInt32());
        Assert.False(jsonDoc.RootElement.GetProperty("is_demo").GetBoolean());
        Assert.Equal("Mulai Ekspedisi", jsonDoc.RootElement.GetProperty("ui_start").GetString());
        Assert.Contains("Inti Aether", jsonDoc.RootElement.GetProperty("dialog").GetProperty("line_2").GetString(), StringComparison.Ordinal);

        XDocument xmlDoc = XDocument.Parse(ws.ReadTextFile(manifest.XmlRelativePath));
        List<XElement> lines = xmlDoc.Root!.Elements("line").ToList();
        Assert.Equal("L001", lines[0].Attribute("id")?.Value);
        Assert.Equal("Aria", lines[0].Attribute("speaker")?.Value);
        Assert.Contains("Selamat datang to <b>Arcadia</b>, {partner}!", lines[0].Value, StringComparison.Ordinal);

        // Act — 1-click restore
        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);

        // Assert byte-for-byte T0 restoration
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    [Fact]
    [Trait("Tier", "Tier4")]
    [Trait("Scenario", "S2")]
    public async Task FullGameWorkload_RpgMakerMvPipelineAndRestore_PatchesTargetCodesPreservesNullsAndRestores()
    {
        // Arrange
        using TempWorkspace ws = new("t4_02_rpgmv_full_game");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: true);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t4-02";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "MockMV Odyssey", ws.GameRootPath, "RpgMaker");

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);

        (IEngineAdapter? adapter, _) = await bridge.DetectEngineAsync(ws.GameRootPath);
        Assert.NotNull(adapter);
        List<SubtitleEntry> extracted = await adapter!.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        Assert.Equal(9, extracted.Count);

        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(sanitizer, extracted);
        await db.InsertSubtitleEntriesAsync(masked);

        SequentialWorkerHandle worker = bridge.CreateSequentialWorker(translator, sanitizer, db);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 5);

        // Act
        await Tiers3_4TestHelper.RunWorkerAsync(worker, db, profile, "Translate preserving RPG codes.", CancellationToken.None);
        IReadOnlyList<SubtitleEntry> translated = await db.GetAllEntriesAsync();

        await backupService.CreateBackupAsync(
            projectId,
            ws.GameRootPath,
            [manifest.Map001RelativePath, manifest.CommonEventsRelativePath],
            CancellationToken.None);
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);

        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert patched JSON
        using JsonDocument mapDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.Map001RelativePath));
        JsonElement eventsArr = mapDoc.RootElement.GetProperty("events");
        Assert.Equal(JsonValueKind.Null, eventsArr[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, eventsArr[2].ValueKind);
        JsonElement mapList = eventsArr[1].GetProperty("pages")[0].GetProperty("list");

        Assert.Equal("Actor1", mapList[0].GetProperty("parameters")[0].GetString());
        Assert.Equal("[ID] Hello, \\N[1]!\nWelcome to \\C[2]Aether Harbor\\C[0].", mapList[1].GetProperty("parameters")[0].GetString());
        Assert.Equal("[ID] Bring {crystal_count} shards.", mapList[2].GetProperty("parameters")[0].GetString());
        Assert.Equal("[ID] Long ago, the Warden sealed the sky.", mapList[4].GetProperty("parameters")[0].GetString());
        Assert.Equal("[ID] Accept mission", mapList[5].GetProperty("parameters")[0][0].GetString());
        Assert.Equal("[ID] Wait here", mapList[5].GetProperty("parameters")[0][1].GetString());
        Assert.Equal("Accept mission", mapList[6].GetProperty("parameters")[1].GetString());
        Assert.Equal(404, mapList[7].GetProperty("code").GetInt32());
        Assert.Equal(0, mapList[8].GetProperty("code").GetInt32());

        using JsonDocument commonDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.CommonEventsRelativePath));
        Assert.Equal(JsonValueKind.Null, commonDoc.RootElement[0].ValueKind);
        JsonElement commonList = commonDoc.RootElement[1].GetProperty("list");
        Assert.Equal("Guide", commonList[0].GetProperty("parameters")[0].GetString());
        Assert.Equal("[ID] Save often at crystals, \\N[1].", commonList[1].GetProperty("parameters")[0].GetString());
        Assert.Equal("[ID] Chapter One: Signal Lost", commonList[2].GetProperty("parameters")[0].GetString());

        // Act — Restore & Assert T0 hashes
        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    [Fact]
    [Trait("Tier", "Tier4")]
    [Trait("Scenario", "S3")]
    public async Task FullGameWorkload_RpgMakerMzMixedValidCorruptLlm_FallbacksFailedValidationAndFlagsOverflow()
    {
        // Arrange
        using TempWorkspace ws = new("t4_03_rpgmz_mixed_corrupt");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t4-03";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "MockMZ Odyssey", ws.GameRootPath, "RpgMaker");

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        (IEngineAdapter? adapter, _) = await bridge.DetectEngineAsync(ws.GameRootPath);
        Assert.NotNull(adapter);
        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(
            sanitizer,
            await adapter!.ExtractAsync(ws.GameRootPath, CancellationToken.None));
        Assert.Equal(9, masked.Count);
        await db.InsertSubtitleEntriesAsync(masked);

        MockLlmRouterHandler mockRouter = new();
        // Batch 1 (3 entries): valid + 1 overflow (>25%)
        mockRouter.EnqueueBatchTranslator(batch =>
        {
            Dictionary<string, string> res = new(StringComparer.Ordinal);
            bool madeOverflow = false;
            foreach (KeyValuePair<string, string> kv in batch)
            {
                if (!madeOverflow && !kv.Value.Contains("[VAR_", StringComparison.Ordinal))
                {
                    res[kv.Key] = $"{kv.Value} — terjemahan tambahan yang sangat panjang sekali melebihi batas dua puluh lima persen";
                    madeOverflow = true;
                }
                else
                {
                    res[kv.Key] = kv.Value;
                }
            }

            return res;
        });
        // Batch 2 (3 entries): prose-wrapped JSON dropping [TAG_0] on CommonEvents "Save often at crystals, \N[1]." entry
        mockRouter.EnqueueProseWrappedBatch(batch =>
        {
            Dictionary<string, string> res = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in batch)
            {
                if (kv.Value.Contains("crystals", StringComparison.Ordinal))
                {
                    res[kv.Key] = "Simpan sering di kristal tanpa kode."; // drops [TAG_0]
                }
                else
                {
                    res[kv.Key] = $"[ID] {kv.Value}";
                }
            }

            return res;
        });
        // Batch 3 (3 entries): valid translations
        mockRouter.EnqueueBatchTranslator(batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));

        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);
        SequentialWorkerHandle worker = bridge.CreateSequentialWorker(translator, sanitizer, db);
        PipelineOrchestratorHandle orchestrator = bridge.CreatePipelineOrchestrator(
            db, sanitizer, translator, backupService, [adapter], ws.BackupRootPath);

        // Act
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 3);
        await Tiers3_4TestHelper.RunWorkerAsync(worker, db, profile, "Translate MZ.", CancellationToken.None);
        IReadOnlyList<SubtitleEntry> persisted = await db.GetAllEntriesAsync();

        await Tiers3_4TestHelper.TryInvokePipelineAsync(
            orchestrator, ["OpenProject"], projectId: projectId, gameRootPath: ws.GameRootPath, adapter: adapter, entries: persisted);
        await Tiers3_4TestHelper.InvokePipelineAsync(
            orchestrator, ["PatchGame", "Patch", "ApplyPatch"], excludedMethodSubstrings: ["RePatch"],
            projectId: projectId, gameRootPath: ws.GameRootPath, adapter: adapter, entries: persisted);

        // Assert
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        Assert.Equal(9, persisted.Count);
        Assert.All(persisted, E2EAssertions.AssertOverflowWarningRule);

        SubtitleEntry failedRow = Assert.Single(persisted, e => e.Status == EntryStatus.FailedValidation);
        Assert.Contains("Save often at crystals", failedRow.RawText, StringComparison.Ordinal);
        Assert.False(failedRow.HasOverflowWarning);

        using JsonDocument commonDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.CommonEventsRelativePath));
        Assert.Equal(JsonValueKind.Null, commonDoc.RootElement[0].ValueKind);
        string actualCommonLine = commonDoc.RootElement[1].GetProperty("list")[1].GetProperty("parameters")[0].GetString()!;
        E2EAssertions.AssertFailedValidationFallback(failedRow, actualCommonLine);
        Assert.DoesNotContain("[TAG_0]", ws.ReadTextFile(manifest.CommonEventsRelativePath), StringComparison.Ordinal);
        Assert.Contains(persisted, e => e.Status == EntryStatus.Translated && e.HasOverflowWarning);

        string patchedMap = ws.ReadTextFile(manifest.Map001RelativePath);
        using JsonDocument mapDoc = JsonDocument.Parse(patchedMap);
        JsonElement eventsArr = mapDoc.RootElement.GetProperty("events");
        Assert.Equal(JsonValueKind.Null, eventsArr[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, eventsArr[2].ValueKind);
        JsonElement list = eventsArr[1].GetProperty("pages")[0].GetProperty("list");
        Assert.Equal("Actor1", list[0].GetProperty("parameters")[0].GetString());
        Assert.Equal("Accept mission", list[6].GetProperty("parameters")[1].GetString());
        Assert.Equal(0, list[8].GetProperty("code").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier4")]
    [Trait("Scenario", "S4")]
    public async Task FullGameWorkload_UnityAssetsPipelineAndRestore_PatchesTextAssetsPreservesBundleAndRestores()
    {
        // Arrange
        using TempWorkspace ws = new("t4_04_unity_full_game");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes(rel => rel.EndsWith(".assets", StringComparison.OrdinalIgnoreCase));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t4-04";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "MockUnityGame", ws.GameRootPath, "Unity");

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);

        (IEngineAdapter? adapter, _) = await bridge.DetectEngineAsync(ws.GameRootPath);
        Assert.NotNull(adapter);
        List<SubtitleEntry> extracted = await adapter!.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        Assert.DoesNotContain(extracted, e => e.RawText.Contains("MockMainBundle", StringComparison.Ordinal));

        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(sanitizer, extracted);
        await db.InsertSubtitleEntriesAsync(masked);

        SequentialWorkerHandle worker = bridge.CreateSequentialWorker(translator, sanitizer, db);

        // Act
        await Tiers3_4TestHelper.RunWorkerAsync(worker, db, MockLlmRouterHandler.CreateProfile(batchSize: 5), "Translate Unity.", CancellationToken.None);
        IReadOnlyList<SubtitleEntry> translated = await db.GetAllEntriesAsync();

        string relRes = Path.GetRelativePath(ws.GameRootPath, manifest.ResourcesAssetsPath).Replace('\\', '/');
        string relShared = Path.GetRelativePath(ws.GameRootPath, manifest.SharedAssets0Path).Replace('\\', '/');
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [relRes, relShared], CancellationToken.None);
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);

        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert updated TextAssets + intact AssetBundle
        IReadOnlyList<(long PathId, string Name, string Script)> resAssets = SyntheticGameBuilder.ReadUnityTextAssets(manifest.ResourcesAssetsPath);
        IReadOnlyList<(long PathId, string Name, string Script)> sharedAssets = SyntheticGameBuilder.ReadUnityTextAssets(manifest.SharedAssets0Path);
        Assert.StartsWith("[ID] <color=#FFD700><b>Commander</b></color>: Welcome, {player_name}!", resAssets.Single(a => a.Name == "IntroDialog").Script, StringComparison.Ordinal);
        Assert.StartsWith("[ID] ", resAssets.Single(a => a.Name == "LocTable_en").Script, StringComparison.Ordinal);
        Assert.StartsWith("[ID] <i>System</i>: Aether Core at {0}% capacity.", sharedAssets.Single(a => a.Name == "Scene0_Cutscene").Script, StringComparison.Ordinal);

        AssetsManager manager = new();
        try
        {
            AssetsFileInstance inst = manager.LoadAssetsFile(manifest.ResourcesAssetsPath, loadDeps: true);
            AssetFileInfo bundle = Assert.Single(inst.file.GetAssetsOfType(AssetClassID.AssetBundle));
            Assert.Equal("MockMainBundle", manager.GetBaseField(inst, bundle)["m_Name"].AsString);
        }
        finally
        {
            manager.UnloadAll(unloadClassData: true);
        }

        // Act — Restore & verify exact SHA-256 + handle release
        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);

        using FileStream lockRes = File.Open(manifest.ResourcesAssetsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using FileStream lockShared = File.Open(manifest.SharedAssets0Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(lockRes.CanWrite && lockShared.CanWrite);
    }

    [Fact]
    [Trait("Tier", "Tier4")]
    [Trait("Scenario", "S5")]
    public async Task FullGameWorkload_UnrealLocresToModsPakPipeline_CreatesModsPakPreservesBasePakAndRestores()
    {
        // Arrange
        using TempWorkspace ws = new("t4_05_unreal_full_game");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        string basePakShaT0 = TempWorkspace.ComputeSha256(manifest.BasePakPath);

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t4-05";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "MockUnrealGame", ws.GameRootPath, "Unreal");

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                kv => kv.Value == "Aether Core"
                    ? "Inti Aether — café élan"
                    : $"[ID] {kv.Value}",
                StringComparer.Ordinal)
        };
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);

        (IEngineAdapter? adapter, _) = await bridge.DetectEngineAsync(ws.GameRootPath);
        Assert.NotNull(adapter);
        List<SubtitleEntry> extracted = await adapter!.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        Assert.Equal(basePakShaT0, TempWorkspace.ComputeSha256(manifest.BasePakPath));

        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(sanitizer, extracted);
        await db.InsertSubtitleEntriesAsync(masked);

        string systemPrompt = bridge.BuildSystemPrompt(
            "MockUnrealGame",
            "Sci-Fi",
            [("Aether Core", "Inti Aether", "Tech")]);
        SequentialWorkerHandle worker = bridge.CreateSequentialWorker(translator, sanitizer, db);

        // Act
        await Tiers3_4TestHelper.RunWorkerAsync(worker, db, MockLlmRouterHandler.CreateProfile(batchSize: 5), systemPrompt, CancellationToken.None);
        IReadOnlyList<SubtitleEntry> translated = await db.GetAllEntriesAsync();

        string basePakRel = Path.GetRelativePath(ws.GameRootPath, manifest.BasePakPath).Replace('\\', '/');
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [basePakRel], CancellationToken.None);
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);

        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert ~mods pak created & readable, base pak untouched
        Assert.True(File.Exists(manifest.ExpectedModsPakPath));
        Assert.Equal(basePakShaT0, TempWorkspace.ComputeSha256(manifest.BasePakPath));

        string modsDir = Path.GetDirectoryName(manifest.ExpectedModsPakPath)!;
        IReadOnlyDictionary<string, string> modLocres = SyntheticGameBuilder.ReadUnrealPakLocres(modsDir, manifest.LocresVirtualPath);
        Assert.Equal("[ID] <RichText.Bold>Welcome</>, {PlayerName}!", modLocres["Dialog::Line_001"]);
        Assert.Equal("[ID] Press [KEY]\tnow.\nProceed to sector {0}?", modLocres["Dialog::Line_002"]);
        Assert.Equal("Inti Aether — café élan", modLocres["UI::Title"]);

        // Act — Restore & verify base pak byte-identical + handles released
        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);
        Assert.Equal(basePakShaT0, TempWorkspace.ComputeSha256(manifest.BasePakPath));

        using FileStream lockBase = File.Open(manifest.BasePakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(lockBase.CanWrite);
        if (File.Exists(manifest.ExpectedModsPakPath))
        {
            using FileStream lockMods = File.Open(manifest.ExpectedModsPakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(lockMods.CanWrite);
        }
    }

    [Fact]
    [Trait("Tier", "Tier4")]
    [Trait("Scenario", "S6")]
    public async Task FullGameWorkload_MidRunCancellationAndResume_Checkpoints30EntriesAndResumesOnlyPending()
    {
        // Arrange — 64 entries, BatchSize = 15 (15 + 15 + 15 + 15 + 4)
        using TempWorkspace ws = new("t4_06_midrun_cancel_resume");
        Dictionary<string, string> lines = Enumerable.Range(1, 64)
            .ToDictionary(i => $"line_{i:D2}", i => $"Expedition dialog line number {i}");
        ws.WriteTextFile("dialog_large.json", JsonSerializer.Serialize(lines));

        OpaqueContractBridge bridge = new();
        const string projectId = "proj-t4-06";
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        List<string> firstRunKeys = [];
        using CancellationTokenSource cts = new();

        MockLlmRouterHandler mockRouter1 = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };

        // Run 1: translate 2 batches (30 entries), then cancel deterministically before Batch 3
        await using (DatabaseSession db1 = await bridge.OpenDatabaseAsync(ws.ConnectionString))
        {
            await Tiers3_4TestHelper.SeedProjectAsync(db1, projectId, "Cancel Resume", ws.GameRootPath, "Loose");
            List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(
                sanitizer,
                await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None));
            Assert.Equal(64, masked.Count);
            await db1.InsertSubtitleEntriesAsync(masked);

            ITranslatorProvider translator1 = bridge.CreateTranslatorProvider(mockRouter1);
            SequentialWorkerHandle worker1 = bridge.CreateSequentialWorker(
                translator1,
                sanitizer,
                db1,
                onProgress: (completed, _) =>
                {
                    if (completed >= 30 && !cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                    }
                });

            RouterProfile profile1 = MockLlmRouterHandler.CreateProfile(
                maxConcurrency: 1,
                delayBetweenBatchesMs: 80,
                jitterMs: 0,
                batchSize: 15);

            // Act 1
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Tiers3_4TestHelper.RunWorkerAsync(worker1, db1, profile1, "Translate.", cts.Token));

            IReadOnlyList<SubtitleEntry> midAll = await db1.GetAllEntriesAsync();
            IReadOnlyList<SubtitleEntry> midPending = await db1.GetPendingEntriesAsync();
            Assert.Equal(30, midAll.Count(e => e.Status == EntryStatus.Translated));
            Assert.Equal(34, midPending.Count);
            Assert.Equal(2, mockRouter1.Requests.Count);
            firstRunKeys.AddRange(mockRouter1.Requests.SelectMany(r => r.ParsedInputBatch?.Keys ?? Enumerable.Empty<string>()));
            Assert.Equal(30, firstRunKeys.Count);
        }

        // Act 2 — Reopen DB session with fresh MockLlmRouterHandler and resume remaining 34 Pending entries
        MockLlmRouterHandler mockRouter2 = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };

        await using DatabaseSession db2 = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        ITranslatorProvider translator2 = bridge.CreateTranslatorProvider(mockRouter2);
        SequentialWorkerHandle worker2 = bridge.CreateSequentialWorker(translator2, sanitizer, db2);
        RouterProfile profile2 = MockLlmRouterHandler.CreateProfile(
            maxConcurrency: 1,
            delayBetweenBatchesMs: 10,
            jitterMs: 0,
            batchSize: 15);

        await Tiers3_4TestHelper.RunWorkerAsync(worker2, db2, profile2, "Translate.", CancellationToken.None);

        List<string> secondRunKeys = mockRouter2.Requests
            .SelectMany(r => r.ParsedInputBatch?.Keys ?? Enumerable.Empty<string>())
            .ToList();

        // Assert
        Assert.Equal(3, mockRouter2.Requests.Count); // 15 + 15 + 4
        Assert.Equal(34, secondRunKeys.Count);
        Assert.Empty(firstRunKeys.Intersect(secondRunKeys, StringComparer.Ordinal));

        IReadOnlyList<SubtitleEntry> finalAll = await db2.GetAllEntriesAsync();
        Assert.Equal(64, finalAll.Count);
        Assert.All(finalAll, e => Assert.Equal(EntryStatus.Translated, e.Status));

        IBackupService backupService = bridge.CreateBackupService(db2.ConnectionString, ws.BackupRootPath, db2.Connection);
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, ["dialog_large.json"], CancellationToken.None);
        await adapter.PatchAsync(ws.GameRootPath, finalAll, CancellationToken.None);

        using JsonDocument patchedDoc = JsonDocument.Parse(ws.ReadTextFile("dialog_large.json"));
        for (int i = 1; i <= 64; i++)
        {
            Assert.Equal($"[ID] Expedition dialog line number {i}", patchedDoc.RootElement.GetProperty($"line_{i:D2}").GetString());
        }
    }

    [Fact]
    [Trait("Tier", "Tier4")]
    [Trait("Scenario", "S7")]
    public async Task FullGameWorkload_Http429AutoPauseProfileHotSwapAndResume_CompletesPatchedGame()
    {
        // Arrange
        using TempWorkspace ws = new("t4_07_429_hotswap");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t4_07_429_hotswap";
        MockLlmRouterHandler mockRouter = await ArrangeAutoPauseHotSwapWorkloadAsync(
            ws,
            db,
            projectId,
            HttpStatusCode.TooManyRequests);

        // Act
        (bool isAutoPausedOnError, int requestsOnPause, bool isAutoPausedAfterResume) =
            await ExecuteAutoPauseHotSwapFlowAsync(bridge, db, ws, mockRouter, projectId);

        // Assert
        await AssertAutoPauseHotSwapCompletedAsync(
            db,
            ws,
            mockRouter,
            projectId,
            isAutoPausedOnError,
            requestsOnPause,
            isAutoPausedAfterResume);
    }

    [Theory]
    [Trait("Tier", "Tier4")]
    [Trait("Scenario", "S7")]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FullGameWorkload_HttpAuthErrorAutoPauseProfileHotSwapAndResume_CompletesPatchedGame(
        HttpStatusCode authStatus)
    {
        // Arrange
        string workspaceName = $"t4_08_{(int)authStatus}_hotswap";
        using TempWorkspace ws = new(workspaceName);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        string projectId = $"proj-{workspaceName}";
        MockLlmRouterHandler mockRouter = await ArrangeAutoPauseHotSwapWorkloadAsync(
            ws,
            db,
            projectId,
            authStatus);

        // Act
        (bool isAutoPausedOnError, int requestsOnPause, bool isAutoPausedAfterResume) =
            await ExecuteAutoPauseHotSwapFlowAsync(bridge, db, ws, mockRouter, projectId);

        // Assert
        await AssertAutoPauseHotSwapCompletedAsync(
            db,
            ws,
            mockRouter,
            projectId,
            isAutoPausedOnError,
            requestsOnPause,
            isAutoPausedAfterResume);
    }

    [Fact]
    [Trait("Tier", "Tier4")]
    [Trait("Scenario", "S8")]
    public async Task FullGameWorkload_PostPatchInlineQaEditRePatchAndRestore_FixesFlaggedRowsWithZeroLlmCalls()
    {
        // Arrange
        using TempWorkspace ws = new("t4_09_inline_qa_repatch_restore");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t4-09";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "Inline QA Full", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                kv =>
                {
                    if (kv.Value.StartsWith("[TAG_0]Welcome", StringComparison.Ordinal) &&
                        kv.Value.Contains("[VAR_0]", StringComparison.Ordinal))
                    {
                        // Entry 1: drops [VAR_0] -> FailedValidation
                        return "[TAG_0]Selamat datang[TAG_1]!";
                    }

                    if (kv.Value == "Start Expedition")
                    {
                        // Entry 2: >25% overflow
                        return "Mulai Ekspedisi Panjang Sekali Hari Ini";
                    }

                    return $"[ID] {kv.Value}";
                },
                StringComparer.Ordinal)
        };

        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, mockRouter);
        int llmCallsBeforeRePatch = 0;

        // Act
        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle pipelineVm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            ConfigurePipelineVm(pipelineVm, projectId, ws.GameRootPath, MockLlmRouterHandler.CreateProfile(batchSize: 10));
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetStartCommand(pipelineVm));
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetPatchGameCommand(pipelineVm));

            // Verify initial disk & SQLite state before QA edits
            IReadOnlyList<SubtitleEntry> initialEntries = await db.GetAllEntriesAsync();
            SubtitleEntry failedInitial = Assert.Single(initialEntries, e => e.RawText == "<b>Welcome</b>, {player_name}!");
            SubtitleEntry overflowInitial = Assert.Single(initialEntries, e => e.RawText == "Start Expedition");
            Assert.Equal(EntryStatus.FailedValidation, failedInitial.Status);
            E2EAssertions.AssertFailedValidationFallback(failedInitial, ws.ReadTextFile(manifest.SrtRelativePath));
            Assert.True(overflowInitial.HasOverflowWarning);

            // Open InlineGridViewModel and fix both flagged rows
            ViewModelHandle gridVm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Grid", "TranslationGrid", "Inline", "Editor", "Entries", "Main"],
                deps);
            await LoadGridAsync(gridVm, projectId, ws.GameRootPath);

            // Fix FailedValidation entry
            EnableFailedValidationFilter(gridVm, true);
            object failRow = Assert.Single(GetFilteredRows(gridVm));
            await ApplyManualEditAsync(gridVm, failRow, "[TAG_0]Selamat datang[TAG_1], [VAR_0]!");

            // Shorten OverflowWarning entry
            EnableFailedValidationFilter(gridVm, false);
            EnableOverflowFilter(gridVm, true);
            object overRow = Assert.Single(GetFilteredRows(gridVm));
            await ApplyManualEditAsync(gridVm, overRow, "Mulai Misi");

            // Snapshot LLM request count before Re-Patch
            llmCallsBeforeRePatch = mockRouter.Requests.Count;
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetRePatchCommand(gridVm));

            // Verify zero new LLM requests and updated patched files on disk
            Assert.Equal(llmCallsBeforeRePatch, mockRouter.Requests.Count);
            Assert.Contains("<b>Selamat datang</b>, {player_name}!", ws.ReadTextFile(manifest.SrtRelativePath), StringComparison.Ordinal);
            using JsonDocument patchedJson = JsonDocument.Parse(ws.ReadTextFile(manifest.JsonRelativePath));
            Assert.Equal("Mulai Misi", patchedJson.RootElement.GetProperty("ui_start").GetString());

            // Verify SQLite persisted ManuallyEdited states
            IReadOnlyList<SubtitleEntry> afterQaEntries = await db.GetAllEntriesAsync();
            SubtitleEntry qaFixed = afterQaEntries.Single(e => e.RawText == "<b>Welcome</b>, {player_name}!");
            SubtitleEntry qaShortened = afterQaEntries.Single(e => e.RawText == "Start Expedition");
            Assert.Equal(EntryStatus.ManuallyEdited, qaFixed.Status);
            Assert.Equal("<b>Selamat datang</b>, {player_name}!", qaFixed.FinalPatchedText);
            Assert.Equal(EntryStatus.ManuallyEdited, qaShortened.Status);
            Assert.False(qaShortened.HasOverflowWarning);

            // 1-Click Restore against T0 original SHA-256 hashes
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetRestoreCommand(pipelineVm));
        });

        // Assert T0 baseline SHA-256 restoration
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    private static async Task<MockLlmRouterHandler> ArrangeAutoPauseHotSwapWorkloadAsync(
        TempWorkspace ws,
        DatabaseSession db,
        string projectId,
        HttpStatusCode pauseStatus)
    {
        Dictionary<string, string> lines = Enumerable.Range(1, 6)
            .ToDictionary(i => $"line_{i}", i => $"Dialog line {i}");
        ws.WriteTextFile("dialog.json", JsonSerializer.Serialize(lines));

        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "HotSwap Game", ws.GameRootPath, "Loose");

        MockLlmRouterHandler mockRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
        };
        mockRouter.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")); // Batch 1 -> 200
        mockRouter.EnqueueHttpStatus(pauseStatus); // Batch 2 -> 429 / 401 / 403
        return mockRouter;
    }

    private static async Task<(bool IsAutoPausedOnError, int RequestsOnPause, bool IsAutoPausedAfterResume)>
        ExecuteAutoPauseHotSwapFlowAsync(
            OpaqueContractBridge bridge,
            DatabaseSession db,
            TempWorkspace ws,
            MockLlmRouterHandler mockRouter,
            string projectId)
    {
        object[] deps = Tiers3_4TestHelper.CreateUiDependencies(bridge, db, ws, mockRouter);

        bool isAutoPausedOnError = false;
        int requestsOnPause = 0;
        bool isAutoPausedAfterResume = true;

        await Tiers3_4TestHelper.RunOnStaWithDispatcherAsync(async () =>
        {
            ViewModelHandle pipelineVm = Tiers3_4TestHelper.ResolveViewModel(
                bridge,
                ["Pipeline", "Progress", "Main"],
                deps);

            RouterProfile initialProfile = MockLlmRouterHandler.CreateProfile(
                apiKey: "sk-expired",
                batchSize: 2);
            await bridge.SaveProfilesAsync(
                ws.ProfilesJsonPath,
                "local_router",
                new Dictionary<string, RouterProfile>(StringComparer.Ordinal) { ["local_router"] = initialProfile });

            ConfigurePipelineVm(pipelineVm, projectId, ws.GameRootPath, initialProfile);
            await Tiers3_4TestHelper.ExecuteCommandAsync(GetStartCommand(pipelineVm));

            Tiers3_4TestHelper.TryGetProperty(pipelineVm, out isAutoPausedOnError, "IsAutoPaused", "IsPaused");
            requestsOnPause = mockRouter.Requests.Count;

            IReadOnlyList<SubtitleEntry> mid = await db.GetAllEntriesAsync();
            Assert.Equal(2, mid.Count(e => e.Status == EntryStatus.Translated));
            Assert.Equal(4, mid.Count(e => e.Status == EntryStatus.Pending));

            // Hot-swap RouterProfile and persist updated profiles.json
            RouterProfile freshProfile = initialProfile with { ApiKey = "sk-fresh-token" };
            await bridge.SaveProfilesAsync(
                ws.ProfilesJsonPath,
                "local_router",
                new Dictionary<string, RouterProfile>(StringComparer.Ordinal) { ["local_router"] = freshProfile });

            Tiers3_4TestHelper.TrySetProperty(pipelineVm, freshProfile, "ActiveRouterProfile", "RouterProfile", "Profile");
            Tiers3_4TestHelper.TrySetProperty(pipelineVm, "sk-fresh-token", "ApiKey");

            await Tiers3_4TestHelper.ExecuteCommandAsync(GetResumeCommand(pipelineVm));
            Tiers3_4TestHelper.TryGetProperty(pipelineVm, out isAutoPausedAfterResume, "IsAutoPaused", "IsPaused");

            await Tiers3_4TestHelper.ExecuteCommandAsync(GetPatchGameCommand(pipelineVm));
        });

        return (isAutoPausedOnError, requestsOnPause, isAutoPausedAfterResume);
    }

    private static async Task AssertAutoPauseHotSwapCompletedAsync(
        DatabaseSession db,
        TempWorkspace ws,
        MockLlmRouterHandler mockRouter,
        string projectId,
        bool isAutoPausedOnError,
        int requestsOnPause,
        bool isAutoPausedAfterResume)
    {
        Assert.True(isAutoPausedOnError);
        Assert.Equal(2, requestsOnPause); // ZERO Batch 3 calls before hot-swap
        Assert.False(isAutoPausedAfterResume);
        Assert.Equal(4, mockRouter.Requests.Count); // Batch 1 + failed Batch 2 + retried Batch 2 + Batch 3
        Assert.Equal("Bearer sk-fresh-token", mockRouter.Requests[2].Authorization);
        Assert.Equal("Bearer sk-fresh-token", mockRouter.Requests[3].Authorization);

        IReadOnlyList<SubtitleEntry> finalEntries = await db.GetAllEntriesAsync();
        Assert.Equal(6, finalEntries.Count);
        Assert.All(finalEntries, e => Assert.Equal(EntryStatus.Translated, e.Status));

        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        using JsonDocument patchedDoc = JsonDocument.Parse(ws.ReadTextFile("dialog.json"));
        for (int i = 1; i <= 6; i++)
        {
            Assert.Equal($"[ID] Dialog line {i}", patchedDoc.RootElement.GetProperty($"line_{i}").GetString());
        }
    }

    private static void ConfigurePipelineVm(
        ViewModelHandle vm,
        string projectId,
        string gameRootPath,
        RouterProfile profile)
    {
        Tiers3_4TestHelper.TrySetProperty(vm, projectId, "ProjectId", "CurrentProjectId");
        Tiers3_4TestHelper.TrySetProperty(vm, gameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "ProjectPath");
        Tiers3_4TestHelper.TrySetProperty(vm, profile, "ActiveRouterProfile", "RouterProfile", "Profile");
        Tiers3_4TestHelper.TrySetProperty(vm, profile.BaseUrl, "BaseUrl");
        Tiers3_4TestHelper.TrySetProperty(vm, profile.ApiKey, "ApiKey");
        Tiers3_4TestHelper.TrySetProperty(vm, profile.Model, "Model");
        Tiers3_4TestHelper.TrySetProperty(vm, profile.BatchSize, "BatchSize");
    }

    private static ICommand GetStartCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["StartTranslation", "StartPipeline", "Start"]);

    private static ICommand GetResumeCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["ResumeTranslation", "ResumePipeline", "Resume"]);

    private static ICommand GetPatchGameCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["PatchGame", "Patch"], excludedSubstrings: ["RePatch"]);

    private static ICommand GetRestoreCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["RestoreOriginal", "RestoreBackup", "Restore"]);

    private static ICommand GetRePatchCommand(ViewModelHandle vm) =>
        Tiers3_4TestHelper.GetCommandSafe(vm, ["RePatch", "InstantRePatch"]);

    private static async Task LoadGridAsync(ViewModelHandle vm, string projectId, string gameRootPath)
    {
        Tiers3_4TestHelper.TrySetProperty(vm, projectId, "ProjectId", "CurrentProjectId");
        Tiers3_4TestHelper.TrySetProperty(vm, gameRootPath, "GameRootPath", "GameFolderPath", "SelectedGamePath", "ProjectPath");

        (bool loadedWithId, _) = await Tiers3_4TestHelper.TryInvokeViewModelMethodAsync(
            vm, ["LoadEntries", "LoadGrid", "Refresh", "LoadProject"], projectId);
        if (loadedWithId)
        {
            return;
        }

        (bool loadedNoArgs, _) = await Tiers3_4TestHelper.TryInvokeViewModelMethodAsync(
            vm, ["LoadEntries", "LoadGrid", "Refresh"]);
        if (loadedNoArgs)
        {
            return;
        }

        ICommand refreshCmd = Tiers3_4TestHelper.GetCommandSafe(vm, ["Refresh", "LoadEntries", "Reload"]);
        await Tiers3_4TestHelper.ExecuteCommandAsync(refreshCmd);
    }

    private static IReadOnlyList<object> GetFilteredRows(ViewModelHandle vm)
    {
        object? col = Tiers3_4TestHelper.GetProperty<object>(
            vm,
            "FilteredEntries", "EntriesView", "GridEntries", "Entries", "Items");
        return Tiers3_4TestHelper.ReadCollectionItems(col);
    }

    private static void EnableFailedValidationFilter(ViewModelHandle vm, bool enabled)
    {
        if (Tiers3_4TestHelper.TrySetProperty(vm, enabled, "FilterFailedValidationOnly", "ShowFailedValidationOnly", "OnlyFailedValidation", "FailedValidationOnly"))
        {
            return;
        }

        Tiers3_4TestHelper.SetProperty(vm, enabled ? "FailedValidation" : "All", "SelectedStatusFilter", "StatusFilter", "FilterMode");
    }

    private static void EnableOverflowFilter(ViewModelHandle vm, bool enabled)
    {
        if (Tiers3_4TestHelper.TrySetProperty(vm, enabled, "FilterOverflowWarningOnly", "ShowOverflowOnly", "ShowOverflowWarningOnly", "OnlyOverflowWarning", "OverflowOnly"))
        {
            return;
        }

        Tiers3_4TestHelper.SetProperty(vm, enabled ? "OverflowWarning" : "All", "SelectedStatusFilter", "StatusFilter", "FilterMode");
    }

    private static async Task ApplyManualEditAsync(ViewModelHandle vm, object row, string newText)
    {
        Tiers3_4TestHelper.TrySetProperty(vm, row, "SelectedEntry", "CurrentEntry");

        (bool editedDirect, _) = await Tiers3_4TestHelper.TryInvokeViewModelMethodAsync(
            vm, ["EditEntry", "UpdateTranslation", "ApplyManualEdit", "SaveEdit", "CommitEdit"], row, newText);
        if (editedDirect)
        {
            return;
        }

        PropertyInfo? editProp =
            row.GetType().GetProperty("EditedText", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) ??
            row.GetType().GetProperty("TranslatedText", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) ??
            row.GetType().GetProperty("FinalPatchedText", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (editProp is not null && editProp.CanWrite)
        {
            editProp.SetValue(row, newText);
        }

        Tiers3_4TestHelper.TrySetProperty(vm, newText, "EditedText", "ManualEditText");

        if (Tiers3_4TestHelper.TryGetCommand(vm, ["SaveEdit", "CommitEdit", "ApplyEdit", "SaveManualEdit"], out ICommand? saveCmd) &&
            saveCmd is not null)
        {
            await Tiers3_4TestHelper.ExecuteCommandAsync(saveCmd, row);
            return;
        }

        await Tiers3_4TestHelper.InvokeViewModelMethodAsync(vm, ["SaveEdit", "CommitEdit", "ApplyEdit"], row);
    }
}
