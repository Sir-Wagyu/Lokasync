using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers3_4;

public sealed class PairwiseAdaptersBackupPipelineTests
{
    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F5,F6,F13,F14")]
    public async Task FingerprinterAndLooseAdapter_ExtractMaskUnmaskPatch_PreservesSrtXmlJsonStructure()
    {
        // Arrange
        using TempWorkspace ws = new("t3_09_fingerprinter_loose");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        // Act
        (IEngineAdapter? adapter, string? engineName) = await bridge.DetectEngineAsync(ws.GameRootPath);
        Assert.NotNull(adapter);
        Assert.Contains("Loose", engineName ?? adapter!.EngineName, StringComparison.OrdinalIgnoreCase);

        List<SubtitleEntry> extracted = await adapter!.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(sanitizer, extracted);

        Dictionary<string, string> translatedMap = masked.ToDictionary(
            e => e.EntryKey,
            e => $"[ID] {e.MaskedText}",
            StringComparer.Ordinal);
        List<SubtitleEntry> ready = Tiers3_4TestHelper.UnmaskEntries(sanitizer, masked, translatedMap);

        await adapter.PatchAsync(ws.GameRootPath, ready, CancellationToken.None);

        // Assert
        Assert.All(ready, e => Assert.Equal(EntryStatus.Translated, e.Status));

        string patchedSrt = ws.ReadTextFile(manifest.SrtRelativePath);
        Assert.Contains("00:00:01,000 --> 00:00:04,250", patchedSrt, StringComparison.Ordinal);
        Assert.Contains("00:00:05,000 --> 00:00:08,500", patchedSrt, StringComparison.Ordinal);
        Assert.Contains("[ID] <b>Welcome</b>, {player_name}!", patchedSrt, StringComparison.Ordinal);

        using JsonDocument jsonDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.JsonRelativePath));
        Assert.Equal(42, jsonDoc.RootElement.GetProperty("meta_version").GetInt32());
        Assert.False(jsonDoc.RootElement.GetProperty("is_demo").GetBoolean());
        Assert.Equal("[ID] Start Expedition", jsonDoc.RootElement.GetProperty("ui_start").GetString());

        XDocument xmlDoc = XDocument.Parse(ws.ReadTextFile(manifest.XmlRelativePath));
        List<XElement> xmlLines = xmlDoc.Root!.Elements("line").ToList();
        Assert.Equal(2, xmlLines.Count);
        Assert.Equal("Aria", xmlLines[0].Attribute("speaker")?.Value);
        Assert.Equal("[ID] Welcome to <b>Arcadia</b>, {partner}!", xmlLines[0].Value);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F5,F6,F13,F15")]
    public async Task FingerprinterAndRpgMakerAdapter_MvAndMzWithLooseReadme_PrioritizesRpgMakerAndPreservesControlCodes()
    {
        // Arrange
        using TempWorkspace ws = new("t3_10_fingerprinter_rpgmv");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: true);
        const string readmeContent = "{\"readme\":\"keep byte-identical\"}";
        ws.WriteTextFile("readme.json", readmeContent);
        string readmeShaBefore = TempWorkspace.ComputeSha256(ws.GetGameFilePath("readme.json"));

        OpaqueContractBridge bridge = new();
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        // Act
        (IEngineAdapter? adapter, string? engineName) = await bridge.DetectEngineAsync(ws.GameRootPath);
        Assert.NotNull(adapter);
        Assert.Contains("Rpg", engineName ?? adapter!.EngineName, StringComparison.OrdinalIgnoreCase);

        List<SubtitleEntry> extracted = await adapter!.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(sanitizer, extracted);
        Dictionary<string, string> translatedMap = masked.ToDictionary(
            e => e.EntryKey,
            e => $"[ID] {e.MaskedText}",
            StringComparer.Ordinal);
        List<SubtitleEntry> ready = Tiers3_4TestHelper.UnmaskEntries(sanitizer, masked, translatedMap);

        await adapter.PatchAsync(ws.GameRootPath, ready, CancellationToken.None);

        // Assert
        Assert.Equal(readmeShaBefore, TempWorkspace.ComputeSha256(ws.GetGameFilePath("readme.json")));

        using JsonDocument mapDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.Map001RelativePath));
        JsonElement eventsArr = mapDoc.RootElement.GetProperty("events");
        Assert.Equal(JsonValueKind.Null, eventsArr[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, eventsArr[2].ValueKind);

        JsonElement list = eventsArr[1].GetProperty("pages")[0].GetProperty("list");
        Assert.Equal("Actor1", list[0].GetProperty("parameters")[0].GetString());
        Assert.Contains("\\N[1]", list[1].GetProperty("parameters")[0].GetString(), StringComparison.Ordinal);
        Assert.Contains("\\C[2]", list[1].GetProperty("parameters")[0].GetString(), StringComparison.Ordinal);
        Assert.StartsWith("[ID] ", list[1].GetProperty("parameters")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal("[ID] Bring {crystal_count} shards.", list[2].GetProperty("parameters")[0].GetString());
        Assert.Equal("[ID] Long ago, the Warden sealed the sky.", list[4].GetProperty("parameters")[0].GetString());
        Assert.Equal("[ID] Accept mission", list[5].GetProperty("parameters")[0][0].GetString());
        Assert.Equal("Accept mission", list[6].GetProperty("parameters")[1].GetString()); // code 402 untouched

        using JsonDocument commonDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.CommonEventsRelativePath));
        Assert.Equal(JsonValueKind.Null, commonDoc.RootElement[0].ValueKind);
        JsonElement commonList = commonDoc.RootElement[1].GetProperty("list");
        Assert.StartsWith("[ID] Save often at crystals, \\N[1].", commonList[1].GetProperty("parameters")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F5,F6,F13,F16")]
    public async Task FingerprinterAndUnityAdapter_ExtractMaskUnmaskPatch_UpdatesTextAssetsAndPreservesAssetBundle()
    {
        // Arrange
        using TempWorkspace ws = new("t3_11_fingerprinter_unity");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        ws.WriteTextFile("config.json", "{\"ignore_loose\":true}");
        string configShaBefore = TempWorkspace.ComputeSha256(ws.GetGameFilePath("config.json"));

        OpaqueContractBridge bridge = new();
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        // Act
        (IEngineAdapter? adapter, string? engineName) = await bridge.DetectEngineAsync(ws.GameRootPath);
        Assert.NotNull(adapter);
        Assert.Contains("Unity", engineName ?? adapter!.EngineName, StringComparison.OrdinalIgnoreCase);

        List<SubtitleEntry> extracted = await adapter!.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(sanitizer, extracted);
        Dictionary<string, string> translatedMap = masked.ToDictionary(
            e => e.EntryKey,
            e => $"[ID] {e.MaskedText}",
            StringComparer.Ordinal);
        List<SubtitleEntry> ready = Tiers3_4TestHelper.UnmaskEntries(sanitizer, masked, translatedMap);

        await adapter.PatchAsync(ws.GameRootPath, ready, CancellationToken.None);

        // Assert
        Assert.Equal(configShaBefore, TempWorkspace.ComputeSha256(ws.GetGameFilePath("config.json")));

        IReadOnlyList<(long PathId, string Name, string Script)> resAssets = SyntheticGameBuilder.ReadUnityTextAssets(manifest.ResourcesAssetsPath);
        IReadOnlyList<(long PathId, string Name, string Script)> sharedAssets = SyntheticGameBuilder.ReadUnityTextAssets(manifest.SharedAssets0Path);

        string intro = resAssets.Single(a => a.Name == "IntroDialog").Script;
        Assert.StartsWith("[ID] <color=#FFD700><b>Commander</b></color>: Welcome, {player_name}!", intro, StringComparison.Ordinal);
        Assert.Contains("%s", intro, StringComparison.Ordinal);
        Assert.StartsWith("[ID] ", sharedAssets.Single(a => a.Name == "Scene0_Cutscene").Script, StringComparison.Ordinal);

        AssetsManager manager = new();
        try
        {
            AssetsFileInstance inst = manager.LoadAssetsFile(manifest.ResourcesAssetsPath, loadDeps: true);
            AssetFileInfo bundleInfo = Assert.Single(inst.file.GetAssetsOfType(AssetClassID.AssetBundle));
            Assert.Equal("MockMainBundle", manager.GetBaseField(inst, bundleInfo)["m_Name"].AsString);
        }
        finally
        {
            manager.UnloadAll(unloadClassData: true);
        }

        using FileStream lockRes = File.Open(manifest.ResourcesAssetsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using FileStream lockShared = File.Open(manifest.SharedAssets0Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(lockRes.CanWrite && lockShared.CanWrite);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F5,F6,F13,F17")]
    public async Task FingerprinterAndUnrealAdapter_ExtractMaskUnmaskPatch_GeneratesModsPakAndPreservesBasePak()
    {
        // Arrange
        using TempWorkspace ws = new("t3_12_fingerprinter_unreal");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: true);
        ws.WriteTextFile("manifest.xml", "<root><ignore>true</ignore></root>");
        string basePakShaBefore = TempWorkspace.ComputeSha256(manifest.BasePakPath);

        OpaqueContractBridge bridge = new();
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        // Act
        (IEngineAdapter? adapter, string? engineName) = await bridge.DetectEngineAsync(ws.GameRootPath);
        Assert.NotNull(adapter);
        Assert.Contains("Unreal", engineName ?? adapter!.EngineName, StringComparison.OrdinalIgnoreCase);

        List<SubtitleEntry> extracted = await adapter!.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> masked = Tiers3_4TestHelper.MaskEntries(sanitizer, extracted);
        Dictionary<string, string> translatedMap = masked.ToDictionary(
            e => e.EntryKey,
            e => $"[ID] {e.MaskedText}",
            StringComparer.Ordinal);
        List<SubtitleEntry> ready = Tiers3_4TestHelper.UnmaskEntries(sanitizer, masked, translatedMap);

        await adapter.PatchAsync(ws.GameRootPath, ready, CancellationToken.None);

        // Assert
        Assert.Equal(basePakShaBefore, TempWorkspace.ComputeSha256(manifest.BasePakPath));
        Assert.True(File.Exists(manifest.ExpectedModsPakPath));

        string modsDir = Path.GetDirectoryName(manifest.ExpectedModsPakPath)!;
        IReadOnlyDictionary<string, string> decoded = SyntheticGameBuilder.ReadUnrealPakLocres(modsDir, manifest.LocresVirtualPath);
        Assert.Equal("[ID] <RichText.Bold>Welcome</>, {PlayerName}!", decoded["Dialog::Line_001"]);
        Assert.Equal("[ID] Press [KEY]\tnow.\nProceed to sector {0}?", decoded["Dialog::Line_002"]);
        Assert.Equal("[ID] Aether Core", decoded["UI::Title"]);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F4,F14,F18")]
    public async Task BackupServiceAndLooseAdapter_CreateBackupGatePatchAndRestore_MatchesOriginalSha256()
    {
        // Arrange — First verify pre-patch gate blocks unbacked patch
        using TempWorkspace ws = new("t3_13_backup_loose_gate");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-13";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "Loose Gate", ws.GameRootPath, "Loose");

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(new MockLlmRouterHandler());
        IEngineAdapter looseAdapter = bridge.GetEngineAdapter("Loose");

        List<SubtitleEntry> extracted = await looseAdapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = extracted
            .Select(e => e with { TranslatedText = $"[PAT] {e.RawText}", FinalPatchedText = $"[PAT] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();
        await db.InsertSubtitleEntriesAsync(translated);

        string blockingFile = Path.Combine(ws.RootPath, "blocking_dir.txt");
        File.WriteAllText(blockingFile, "not a directory");
        IBackupService failingBackup = bridge.CreateBackupService(db.ConnectionString, blockingFile, db.Connection);
        PipelineOrchestratorHandle blockedOrchestrator = bridge.CreatePipelineOrchestrator(
            db, sanitizer, translator, failingBackup, [looseAdapter], blockingFile);

        await Tiers3_4TestHelper.TryInvokePipelineAsync(
            blockedOrchestrator, ["OpenProject"], projectId: projectId, gameRootPath: ws.GameRootPath, adapter: looseAdapter, entries: translated);
        Exception? gateEx = await Record.ExceptionAsync(() => Tiers3_4TestHelper.InvokePipelineAsync(
            blockedOrchestrator, ["PatchGame", "Patch", "ApplyPatch"], excludedMethodSubstrings: ["RePatch"],
            projectId: projectId, gameRootPath: ws.GameRootPath, adapter: looseAdapter, entries: translated));
        Assert.NotNull(gateEx);
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);

        // Act — Run valid backup + patch + restore
        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);
        PipelineOrchestratorHandle orchestrator = bridge.CreatePipelineOrchestrator(
            db, sanitizer, translator, backupService, [looseAdapter], ws.BackupRootPath);

        await Tiers3_4TestHelper.TryInvokePipelineAsync(
            orchestrator, ["OpenProject"], projectId: projectId, gameRootPath: ws.GameRootPath, adapter: looseAdapter, entries: translated);
        await Tiers3_4TestHelper.InvokePipelineAsync(
            orchestrator, ["PatchGame", "Patch", "ApplyPatch"], excludedMethodSubstrings: ["RePatch"],
            projectId: projectId, gameRootPath: ws.GameRootPath, adapter: looseAdapter, entries: translated);

        Assert.Equal(3, await Tiers3_4TestHelper.QueryBackupManifestCountAsync(db.Connection, projectId));
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        Assert.Contains("[PAT] ", ws.ReadTextFile(manifest.JsonRelativePath), StringComparison.Ordinal);

        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);

        // Assert
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F4,F15,F18")]
    public async Task BackupServiceAndRpgMakerAdapter_BackupPatchAndRestore_RestoresMapAndCommonEventsByteForByte()
    {
        // Arrange
        using TempWorkspace ws = new("t3_14_backup_rpgmz");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-14";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "MZ Backup", ws.GameRootPath, "RpgMaker");

        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with { TranslatedText = $"[ID] {e.RawText}", FinalPatchedText = $"[ID] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();

        // Act
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [manifest.Map001RelativePath, manifest.CommonEventsRelativePath], CancellationToken.None);
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        Assert.Contains("[ID] ", ws.ReadTextFile(manifest.Map001RelativePath), StringComparison.Ordinal);
        Assert.Contains("[ID] ", ws.ReadTextFile(manifest.CommonEventsRelativePath), StringComparison.Ordinal);

        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);

        // Assert
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F4,F16,F18")]
    public async Task BackupServiceAndUnityAdapter_BackupPatchAndRestore_RestoresBinaryAssetsAndManifests()
    {
        // Arrange
        using TempWorkspace ws = new("t3_15_backup_unity");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-15";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "Unity Backup", ws.GameRootPath, "Unity");

        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with { TranslatedText = $"[ID] {e.RawText}", FinalPatchedText = $"[ID] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();

        string relRes = Path.GetRelativePath(ws.GameRootPath, manifest.ResourcesAssetsPath).Replace('\\', '/');
        string relShared = Path.GetRelativePath(ws.GameRootPath, manifest.SharedAssets0Path).Replace('\\', '/');

        // Act
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [relRes, relShared], CancellationToken.None);
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        Assert.All(SyntheticGameBuilder.ReadUnityTextAssets(manifest.ResourcesAssetsPath), a => Assert.StartsWith("[ID] ", a.Script, StringComparison.Ordinal));

        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);

        // Assert
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
        IReadOnlyList<(long PathId, string Name, string Script)> restoredRes = SyntheticGameBuilder.ReadUnityTextAssets(manifest.ResourcesAssetsPath);
        Assert.Contains(restoredRes, a => a.Name == "IntroDialog" && !a.Script.StartsWith("[ID] ", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F4,F17,F18")]
    public async Task BackupServiceAndUnrealAdapter_BasePakManifestAndTamperedBackup_ProtectsBasePakIntegrity()
    {
        // Arrange
        using TempWorkspace ws = new("t3_16_backup_unreal_tamper");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        string baseShaBefore = TempWorkspace.ComputeSha256(manifest.BasePakPath);
        string baseRel = Path.GetRelativePath(ws.GameRootPath, manifest.BasePakPath).Replace('\\', '/');

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-t3-16";
        await Tiers3_4TestHelper.SeedProjectAsync(db, projectId, "Unreal Tamper", ws.GameRootPath, "Unreal");

        IBackupService backupService = bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with { TranslatedText = $"[ID] {e.RawText}", FinalPatchedText = $"[ID] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();

        // Act
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [baseRel], CancellationToken.None);
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);
        Assert.Equal(baseShaBefore, TempWorkspace.ComputeSha256(manifest.BasePakPath));

        string backupFilePath;
        await using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT backup_file_path FROM backup_manifests WHERE project_id = @pid LIMIT 1;";
            cmd.Parameters.AddWithValue("@pid", projectId);
            backupFilePath = Convert.ToString(await cmd.ExecuteScalarAsync())!;
        }

        byte[] tampered = File.ReadAllBytes(backupFilePath);
        tampered[0] ^= 0xFF;
        File.WriteAllBytes(backupFilePath, tampered);

        Exception? restoreEx = await Record.ExceptionAsync(
            () => backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None));

        // Assert
        Assert.NotNull(restoreEx);
        Assert.True(
            restoreEx is InvalidOperationException or CryptographicException or IOException or InvalidDataException,
            $"Expected hash-mismatch exception, got {restoreEx!.GetType().FullName}: {restoreEx.Message}");
        Assert.Equal(baseShaBefore, TempWorkspace.ComputeSha256(manifest.BasePakPath));
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F6,F14,F15,F19")]
    public async Task PipelineFallbackAndOverflow_CorruptPlaceholderAndLongTranslation_FallsBackAndFlagsOverflow()
    {
        // Arrange — Loose game via real SUT SequentialWorker + PipelineOrchestrator
        using TempWorkspace wsLoose = new("t3_17_loose_overflow");
        wsLoose.WriteTextFile("dialog.json", JsonSerializer.Serialize(new
        {
            entry_a = "<b>Hero</b>",
            entry_b = "1234", // exact 25% boundary: len 4 -> len 5 (5*4=20 <= 4*5=20)
            entry_c = "Go!!"  // >25%: len 4 -> len 6 (6*4=24 > 4*5=20)
        }));

        OpaqueContractBridge bridge = new();
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        IEngineAdapter looseAdapter = bridge.GetEngineAdapter("Loose");

        await using DatabaseSession dbLoose = await bridge.OpenDatabaseAsync(wsLoose.ConnectionString);
        const string looseProjectId = "proj-t3-17-loose";
        await Tiers3_4TestHelper.SeedProjectAsync(dbLoose, looseProjectId, "Loose Overflow", wsLoose.GameRootPath, "Loose");

        List<SubtitleEntry> looseMasked = Tiers3_4TestHelper.MaskEntries(
            sanitizer,
            await looseAdapter.ExtractAsync(wsLoose.GameRootPath, CancellationToken.None));
        await dbLoose.InsertSubtitleEntriesAsync(looseMasked);

        MockLlmRouterHandler mockLooseRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                kv => kv.Value switch
                {
                    var v when v.Contains("Hero", StringComparison.Ordinal) => "Pahlawan tanpa tag", // drops [TAG_0]/[TAG_1] -> FailedValidation
                    "1234" => "12345",
                    "Go!!" => "123456",
                    _ => kv.Value
                },
                StringComparer.Ordinal)
        };
        ITranslatorProvider looseTranslator = bridge.CreateTranslatorProvider(mockLooseRouter);
        IBackupService looseBackup = bridge.CreateBackupService(dbLoose.ConnectionString, wsLoose.BackupRootPath, dbLoose.Connection);
        SequentialWorkerHandle looseWorker = bridge.CreateSequentialWorker(looseTranslator, sanitizer, dbLoose);
        PipelineOrchestratorHandle looseOrchestrator = bridge.CreatePipelineOrchestrator(
            dbLoose, sanitizer, looseTranslator, looseBackup, [looseAdapter], wsLoose.BackupRootPath);

        // Arrange — RPG Maker MZ game via real SUT SequentialWorker + PipelineOrchestrator
        using TempWorkspace wsMz = new("t3_17_mz_overflow");
        RpgMakerFixtureManifest mzManifest = SyntheticGameBuilder.CreateRpgMakerGame(wsMz.GameRootPath, isMv: false);
        IEngineAdapter mzAdapter = bridge.GetEngineAdapter("Rpg");

        await using DatabaseSession dbMz = await bridge.OpenDatabaseAsync(wsMz.ConnectionString);
        const string mzProjectId = "proj-t3-17-mz";
        await Tiers3_4TestHelper.SeedProjectAsync(dbMz, mzProjectId, "MZ Overflow", wsMz.GameRootPath, "RpgMaker");

        List<SubtitleEntry> mzMasked = Tiers3_4TestHelper.MaskEntries(
            sanitizer,
            await mzAdapter.ExtractAsync(wsMz.GameRootPath, CancellationToken.None));
        await dbMz.InsertSubtitleEntriesAsync(mzMasked);

        MockLlmRouterHandler mockMzRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Contains("shards", StringComparison.Ordinal)
                    ? "Bawa pecahan." // drops [VAR_0] -> FailedValidation
                    : $"{kv.Value} {new string('A', 25)}", // preserves placeholders and exceeds +25%
                StringComparer.Ordinal)
        };
        ITranslatorProvider mzTranslator = bridge.CreateTranslatorProvider(mockMzRouter);
        IBackupService mzBackup = bridge.CreateBackupService(dbMz.ConnectionString, wsMz.BackupRootPath, dbMz.Connection);
        SequentialWorkerHandle mzWorker = bridge.CreateSequentialWorker(mzTranslator, sanitizer, dbMz);
        PipelineOrchestratorHandle mzOrchestrator = bridge.CreatePipelineOrchestrator(
            dbMz, sanitizer, mzTranslator, mzBackup, [mzAdapter], wsMz.BackupRootPath);

        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 10);

        // Act
        await Tiers3_4TestHelper.RunWorkerAsync(looseWorker, dbLoose, profile, "Translate.", CancellationToken.None);
        IReadOnlyList<SubtitleEntry> loosePersisted = await dbLoose.GetAllEntriesAsync();
        await Tiers3_4TestHelper.TryInvokePipelineAsync(
            looseOrchestrator, ["OpenProject"], projectId: looseProjectId, gameRootPath: wsLoose.GameRootPath, adapter: looseAdapter, entries: loosePersisted);
        await Tiers3_4TestHelper.InvokePipelineAsync(
            looseOrchestrator, ["PatchGame", "Patch", "ApplyPatch"], excludedMethodSubstrings: ["RePatch"],
            projectId: looseProjectId, gameRootPath: wsLoose.GameRootPath, adapter: looseAdapter, entries: loosePersisted);
        IReadOnlyList<SubtitleEntry> looseFinal = await dbLoose.GetAllEntriesAsync();

        await Tiers3_4TestHelper.RunWorkerAsync(mzWorker, dbMz, profile, "Translate.", CancellationToken.None);
        IReadOnlyList<SubtitleEntry> mzPersisted = await dbMz.GetAllEntriesAsync();
        await Tiers3_4TestHelper.TryInvokePipelineAsync(
            mzOrchestrator, ["OpenProject"], projectId: mzProjectId, gameRootPath: wsMz.GameRootPath, adapter: mzAdapter, entries: mzPersisted);
        await Tiers3_4TestHelper.InvokePipelineAsync(
            mzOrchestrator, ["PatchGame", "Patch", "ApplyPatch"], excludedMethodSubstrings: ["RePatch"],
            projectId: mzProjectId, gameRootPath: wsMz.GameRootPath, adapter: mzAdapter, entries: mzPersisted);
        IReadOnlyList<SubtitleEntry> mzFinal = await dbMz.GetAllEntriesAsync();

        // Assert
        E2EAssertions.AssertBackupManifestMatchesDisk(wsLoose.GameRootPath, dbLoose.Connection, looseProjectId);
        E2EAssertions.AssertBackupManifestMatchesDisk(wsMz.GameRootPath, dbMz.Connection, mzProjectId);

        SubtitleEntry entryA = looseFinal.Single(e => e.RawText == "<b>Hero</b>");
        SubtitleEntry entryB = looseFinal.Single(e => e.RawText == "1234");
        SubtitleEntry entryC = looseFinal.Single(e => e.RawText == "Go!!");

        Assert.Equal(EntryStatus.FailedValidation, entryA.Status);
        Assert.False(entryA.HasOverflowWarning);
        E2EAssertions.AssertFailedValidationFallback(entryA, wsLoose.ReadTextFile("dialog.json"));

        Assert.Equal(EntryStatus.Translated, entryB.Status);
        Assert.Equal("12345", entryB.FinalPatchedText);
        Assert.False(entryB.HasOverflowWarning);

        Assert.Equal(EntryStatus.Translated, entryC.Status);
        Assert.Equal("123456", entryC.FinalPatchedText);
        Assert.True(entryC.HasOverflowWarning);

        Assert.All(looseFinal, E2EAssertions.AssertOverflowWarningRule);
        Assert.All(mzFinal, E2EAssertions.AssertOverflowWarningRule);

        SubtitleEntry mzFailed = mzFinal.Single(e => e.RawText.Contains("{crystal_count}", StringComparison.Ordinal));
        Assert.False(mzFailed.HasOverflowWarning);
        E2EAssertions.AssertFailedValidationFallback(mzFailed, wsMz.ReadTextFile(mzManifest.Map001RelativePath));
        Assert.Contains(mzFinal, e => e.Status == EntryStatus.Translated && e.HasOverflowWarning);
    }

    [Fact]
    [Trait("Tier", "Tier3")]
    [Trait("Features", "F1,F3,F16,F17,F19")]
    public async Task BinaryAdaptersAndValidation_FailedValidationInUnityAndUnreal_WritesRawTextFallbackSafely()
    {
        // Arrange — Unity via real SUT SequentialWorker + CreateBackupService
        using TempWorkspace wsUnity = new("t3_18_unity_fallback");
        UnityFixtureManifest unityManifest = SyntheticGameBuilder.CreateUnityGame(wsUnity.GameRootPath);
        OpaqueContractBridge bridge = new();
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();

        await using DatabaseSession dbUnity = await bridge.OpenDatabaseAsync(wsUnity.ConnectionString);
        const string unityProjectId = "proj-t3-18-u";
        await Tiers3_4TestHelper.SeedProjectAsync(dbUnity, unityProjectId, "Unity Valid", wsUnity.GameRootPath, "Unity");
        IEngineAdapter unityAdapter = bridge.GetEngineAdapter("Unity");

        List<SubtitleEntry> unityMasked = Tiers3_4TestHelper.MaskEntries(
            sanitizer,
            await unityAdapter.ExtractAsync(wsUnity.GameRootPath, CancellationToken.None));
        await dbUnity.InsertSubtitleEntriesAsync(unityMasked);

        MockLlmRouterHandler mockUnityRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Contains("Commander", StringComparison.Ordinal)
                    ? "Komandan rusak tanpa tag atau var" // drops [TAG_*]/[VAR_*] -> FailedValidation
                    : $"[ID] {kv.Value} — versi panjang untuk overflow warning!", // >25%
                StringComparer.Ordinal)
        };
        ITranslatorProvider unityTranslator = bridge.CreateTranslatorProvider(mockUnityRouter);
        IBackupService unityBackup = bridge.CreateBackupService(dbUnity.ConnectionString, wsUnity.BackupRootPath, dbUnity.Connection);
        SequentialWorkerHandle unityWorker = bridge.CreateSequentialWorker(unityTranslator, sanitizer, dbUnity);

        // Arrange — Unreal via real SUT SequentialWorker + CreateBackupService
        using TempWorkspace wsUnreal = new("t3_18_unreal_fallback");
        UnrealFixtureManifest unrealManifest = SyntheticGameBuilder.CreateUnrealGame(wsUnreal.GameRootPath);
        await using DatabaseSession dbUnreal = await bridge.OpenDatabaseAsync(wsUnreal.ConnectionString);
        const string unrealProjectId = "proj-t3-18-ue";
        await Tiers3_4TestHelper.SeedProjectAsync(dbUnreal, unrealProjectId, "Unreal Valid", wsUnreal.GameRootPath, "Unreal");
        IEngineAdapter unrealAdapter = bridge.GetEngineAdapter("Unreal");

        List<SubtitleEntry> unrealMasked = Tiers3_4TestHelper.MaskEntries(
            sanitizer,
            await unrealAdapter.ExtractAsync(wsUnreal.GameRootPath, CancellationToken.None));
        await dbUnreal.InsertSubtitleEntriesAsync(unrealMasked);

        MockLlmRouterHandler mockUnrealRouter = new()
        {
            DefaultBatchTranslator = batch => batch.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Contains("Welcome", StringComparison.Ordinal)
                    ? "Selamat datang rusak" // drops [TAG_0]/[TAG_1]/[VAR_0] -> FailedValidation
                    : $"[ID] {kv.Value} — teks sangat panjang!", // >25%
                StringComparer.Ordinal)
        };
        ITranslatorProvider unrealTranslator = bridge.CreateTranslatorProvider(mockUnrealRouter);
        IBackupService unrealBackup = bridge.CreateBackupService(dbUnreal.ConnectionString, wsUnreal.BackupRootPath, dbUnreal.Connection);
        SequentialWorkerHandle unrealWorker = bridge.CreateSequentialWorker(unrealTranslator, sanitizer, dbUnreal);

        RouterProfile profile = MockLlmRouterHandler.CreateProfile(batchSize: 10);

        // Act
        await Tiers3_4TestHelper.RunWorkerAsync(unityWorker, dbUnity, profile, "Translate Unity.", CancellationToken.None);
        IReadOnlyList<SubtitleEntry> unityReloaded = await dbUnity.GetAllEntriesAsync();
        string relUnityRes = Path.GetRelativePath(wsUnity.GameRootPath, unityManifest.ResourcesAssetsPath).Replace('\\', '/');
        string relUnityShared = Path.GetRelativePath(wsUnity.GameRootPath, unityManifest.SharedAssets0Path).Replace('\\', '/');
        await unityBackup.CreateBackupAsync(unityProjectId, wsUnity.GameRootPath, [relUnityRes, relUnityShared], CancellationToken.None);

        // Prove UnityAdapter ignores FinalPatchedText on FailedValidation rows and writes exact RawText
        List<SubtitleEntry> unityPatchInputs = unityReloaded
            .Select(e => e.Status == EntryStatus.FailedValidation
                ? e with { FinalPatchedText = "CORRUPT_SHOULD_NOT_APPEAR" }
                : e)
            .ToList();
        await unityAdapter.PatchAsync(wsUnity.GameRootPath, unityPatchInputs, CancellationToken.None);

        await Tiers3_4TestHelper.RunWorkerAsync(unrealWorker, dbUnreal, profile, "Translate Unreal.", CancellationToken.None);
        IReadOnlyList<SubtitleEntry> unrealReloaded = await dbUnreal.GetAllEntriesAsync();
        string relUnrealBasePak = Path.GetRelativePath(wsUnreal.GameRootPath, unrealManifest.BasePakPath).Replace('\\', '/');
        await unrealBackup.CreateBackupAsync(unrealProjectId, wsUnreal.GameRootPath, [relUnrealBasePak], CancellationToken.None);

        // Prove UnrealAdapter ignores null FinalPatchedText on FailedValidation rows and writes exact RawText
        List<SubtitleEntry> unrealPatchInputs = unrealReloaded
            .Select(e => e.Status == EntryStatus.FailedValidation
                ? e with { FinalPatchedText = null }
                : e)
            .ToList();
        await unrealAdapter.PatchAsync(wsUnreal.GameRootPath, unrealPatchInputs, CancellationToken.None);

        // Assert
        E2EAssertions.AssertBackupManifestMatchesDisk(wsUnity.GameRootPath, dbUnity.Connection, unityProjectId);
        E2EAssertions.AssertBackupManifestMatchesDisk(wsUnreal.GameRootPath, dbUnreal.Connection, unrealProjectId);

        IReadOnlyList<(long PathId, string Name, string Script)> unityRes = SyntheticGameBuilder.ReadUnityTextAssets(unityManifest.ResourcesAssetsPath);
        SubtitleEntry unityIntro = unityReloaded.Single(e => e.AssetPath?.Contains("IntroDialog", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(EntryStatus.FailedValidation, unityIntro.Status);
        Assert.False(unityIntro.HasOverflowWarning);
        string patchedIntroScript = unityRes.Single(a => a.Name == "IntroDialog").Script;
        E2EAssertions.AssertFailedValidationFallback(unityIntro, patchedIntroScript);
        Assert.Equal(unityIntro.RawText, patchedIntroScript);
        Assert.DoesNotContain("CORRUPT_SHOULD_NOT_APPEAR", patchedIntroScript, StringComparison.Ordinal);
        Assert.StartsWith("[ID] ", unityRes.Single(a => a.Name == "LocTable_en").Script, StringComparison.Ordinal);

        Assert.All(unityReloaded, E2EAssertions.AssertOverflowWarningRule);
        Assert.Contains(unityReloaded, e => e.Status == EntryStatus.FailedValidation && !e.HasOverflowWarning);
        Assert.Contains(unityReloaded, e => e.Status == EntryStatus.Translated && e.HasOverflowWarning);

        string modsDir = Path.GetDirectoryName(unrealManifest.ExpectedModsPakPath)!;
        IReadOnlyDictionary<string, string> unrealLocres = SyntheticGameBuilder.ReadUnrealPakLocres(modsDir, unrealManifest.LocresVirtualPath);
        SubtitleEntry unrealWelcome = unrealReloaded.Single(e => e.RawText.Contains("Welcome", StringComparison.Ordinal));
        Assert.Equal(EntryStatus.FailedValidation, unrealWelcome.Status);
        Assert.False(unrealWelcome.HasOverflowWarning);
        E2EAssertions.AssertFailedValidationFallback(unrealWelcome, unrealLocres["Dialog::Line_001"]);
        Assert.Equal(unrealWelcome.RawText, unrealLocres["Dialog::Line_001"]);
        Assert.StartsWith("[ID] ", unrealLocres["UI::Title"], StringComparison.Ordinal);

        Assert.All(unrealReloaded, E2EAssertions.AssertOverflowWarningRule);
        Assert.Contains(unrealReloaded, e => e.Status == EntryStatus.FailedValidation && !e.HasOverflowWarning);
        Assert.Contains(unrealReloaded, e => e.Status == EntryStatus.Translated && e.HasOverflowWarning);
    }
}
