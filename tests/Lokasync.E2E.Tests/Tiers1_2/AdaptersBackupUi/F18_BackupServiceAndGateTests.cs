using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F18_BackupServiceAndGateTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public async Task CreateBackupAsync_ValidRelativeFiles_CopiesFilesAndRecordsSha256Manifests()
    {
        // Arrange
        using TempWorkspace ws = new("f18_backup_valid");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t1-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Loose Game", ws.GameRootPath, "Loose");
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        string[] files = [manifest.SrtRelativePath, manifest.JsonRelativePath, manifest.XmlRelativePath];

        // Act
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, files, CancellationToken.None);

        // Assert
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        int rowCount = await AdaptersBackupUiTestHelper.QueryBackupManifestCountAsync(db.Connection, projectId);
        Assert.Equal(3, rowCount);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public async Task RestoreBackupAsync_AfterFilesModifiedByPatch_RestoresOriginalsByteForByte()
    {
        // Arrange
        using TempWorkspace ws = new("f18_restore_valid");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t1-2";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Loose Game", ws.GameRootPath, "Loose");
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        string[] files = [manifest.SrtRelativePath, manifest.JsonRelativePath, manifest.XmlRelativePath];

        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, files, CancellationToken.None);
        ws.WriteTextFile(manifest.SrtRelativePath, "PATCHED SRT");
        ws.WriteTextFile(manifest.JsonRelativePath, "{\"patched\":true}");
        ws.WriteTextFile(manifest.XmlRelativePath, "<patched/>");

        // Act
        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);

        // Assert
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public async Task CreateBackupAsync_LargeBinaryArchive_StreamsSha256AndRestoresAccurately()
    {
        // Arrange
        using TempWorkspace ws = new("f18_large_binary");
        byte[] payload = new byte[20 * 1024 * 1024];
        new Random(42).NextBytes(payload);
        string relPath = "Content/Paks/large_archive.bin";
        string fullPath = ws.WriteBytesFile(relPath, payload);
        string expectedHash = TempWorkspace.ComputeSha256(fullPath);

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t1-3";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Large Binary", ws.GameRootPath, "Unreal");
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);

        // Act
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [relPath], CancellationToken.None);
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        ws.WriteBytesFile(relPath, [0x01, 0x02, 0x03]);
        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);

        // Assert
        Assert.Equal(expectedHash, TempWorkspace.ComputeSha256(fullPath));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public async Task CreateBackupAsync_NestedDirectoriesAndSpacesInPaths_PreservesRelativeStructure()
    {
        // Arrange
        using TempWorkspace ws = new("f18_spaces_paths");
        const string file1 = "sub dir/Chapter 1/dialog file.json";
        const string file2 = "data/Map001.json";
        ws.WriteTextFile(file1, "{\"line\":\"Original 1\"}");
        ws.WriteTextFile(file2, "{\"line\":\"Original 2\"}");
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t1-4";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Spaced Paths", ws.GameRootPath, "Loose");
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);

        // Act
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [file1, file2], CancellationToken.None);
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        ws.WriteTextFile(file1, "{\"line\":\"Mutated 1\"}");
        ws.WriteTextFile(file2, "{\"line\":\"Mutated 2\"}");
        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);

        // Assert
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public async Task PipelineOrchestrator_PatchCommand_RunsCreateBackupGateBeforePatchAsync()
    {
        // Arrange
        using TempWorkspace ws = new("f18_orchestrator_gate");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        string originalJsonHash = TempWorkspace.ComputeSha256(ws.GetGameFilePath(manifest.JsonRelativePath));

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t1-5";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Gate Test", ws.GameRootPath, "Loose");

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        MockLlmRouterHandler mockRouter = new();
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        IEngineAdapter looseAdapter = bridge.GetEngineAdapter("Loose");
        PipelineOrchestratorHandle orchestrator = AdaptersBackupUiTestHelper.CreatePipelineOrchestratorSafe(
            bridge, db, sanitizer, translator, backupService, [looseAdapter], ws.BackupRootPath);

        List<SubtitleEntry> extracted = await looseAdapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = extracted
            .Select(e => e with
            {
                TranslatedText = e.RawText == "Start Expedition" ? "Mulai Ekspedisi" : $"[ID] {e.RawText}",
                FinalPatchedText = e.RawText == "Start Expedition" ? "Mulai Ekspedisi" : $"[ID] {e.RawText}",
                Status = EntryStatus.Translated
            })
            .ToList();
        await db.InsertSubtitleEntriesAsync(translated);

        // Act
        await AdaptersBackupUiTestHelper.TryInvokePipelineAsync(
            orchestrator,
            ["OpenProject"],
            projectId: projectId,
            gameRootPath: ws.GameRootPath,
            adapter: looseAdapter,
            entries: translated,
            ct: CancellationToken.None);

        await AdaptersBackupUiTestHelper.InvokePipelineAsync(
            orchestrator,
            ["PatchGame", "Patch", "ApplyPatch"],
            excludedMethodSubstrings: ["RePatch"],
            projectId: projectId,
            gameRootPath: ws.GameRootPath,
            adapter: looseAdapter,
            entries: translated,
            ct: CancellationToken.None);

        // Assert
        E2EAssertions.AssertBackupManifestMatchesDisk(ws.GameRootPath, db.Connection, projectId);
        Assert.Contains("Mulai Ekspedisi", ws.ReadTextFile(manifest.JsonRelativePath), StringComparison.Ordinal);
        Assert.NotEqual(originalJsonHash, TempWorkspace.ComputeSha256(ws.GetGameFilePath(manifest.JsonRelativePath)));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public async Task CreateBackupAsync_CalledTwiceBeforeRestore_PreservesOriginalCleanBackup()
    {
        // Arrange
        using TempWorkspace ws = new("f18_idempotent_backup");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t1-6";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Clean Baseline", ws.GameRootPath, "Loose");
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);
        string[] files = [manifest.SrtRelativePath, manifest.JsonRelativePath, manifest.XmlRelativePath];

        // Act
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, files, CancellationToken.None);
        ws.WriteTextFile(manifest.JsonRelativePath, "{\"ui_start\":\"Patched Pass 1\"}");
        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, files, CancellationToken.None);
        ws.WriteTextFile(manifest.JsonRelativePath, "{\"ui_start\":\"Patched Pass 2\"}");
        await backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None);

        // Assert
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public async Task PipelineOrchestrator_PatchWithoutBackup_BlockedByPrePatchGate()
    {
        // Arrange
        using TempWorkspace ws = new("f18_gate_blocks");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();

        // Point backupRootPath at an existing regular file so backup creation fails if attempted.
        string blockingFilePath = Path.Combine(ws.RootPath, "blocking_backup_file.txt");
        File.WriteAllText(blockingFilePath, "Not a directory");

        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t2-1";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Blocked Gate", ws.GameRootPath, "Loose");

        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        MockLlmRouterHandler mockRouter = new();
        ITranslatorProvider translator = bridge.CreateTranslatorProvider(mockRouter);
        IBackupService failingBackupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, blockingFilePath);
        IEngineAdapter looseAdapter = bridge.GetEngineAdapter("Loose");
        PipelineOrchestratorHandle orchestrator = AdaptersBackupUiTestHelper.CreatePipelineOrchestratorSafe(
            bridge, db, sanitizer, translator, failingBackupService, [looseAdapter], blockingFilePath);

        List<SubtitleEntry> extracted = await looseAdapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = extracted
            .Select(e => e with
            {
                TranslatedText = $"[BLOCKED] {e.RawText}",
                FinalPatchedText = $"[BLOCKED] {e.RawText}",
                Status = EntryStatus.Translated
            })
            .ToList();
        await db.InsertSubtitleEntriesAsync(translated);

        // Act
        await AdaptersBackupUiTestHelper.TryInvokePipelineAsync(
            orchestrator,
            ["OpenProject"],
            projectId: projectId,
            gameRootPath: ws.GameRootPath,
            adapter: looseAdapter,
            entries: translated,
            ct: CancellationToken.None);

        Exception? ex = await Record.ExceptionAsync(() => AdaptersBackupUiTestHelper.InvokePipelineAsync(
            orchestrator,
            ["PatchGame", "Patch", "ApplyPatch"],
            excludedMethodSubstrings: ["RePatch"],
            projectId: projectId,
            gameRootPath: ws.GameRootPath,
            adapter: looseAdapter,
            entries: translated,
            ct: CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        Assert.False(
            ex.Message.StartsWith("Could not bind", StringComparison.OrdinalIgnoreCase),
            $"Expected orchestrator pre-patch gate failure, got reflection binding error: {ex.Message}");
        Assert.Equal(0, await AdaptersBackupUiTestHelper.QueryBackupManifestCountAsync(db.Connection, projectId));
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);

        // Also verify calling Patch when 0 backup_manifests exist (valid backup root, CreateBackupAsync never called)
        using TempWorkspace wsNoManifest = new("f18_gate_zero_manifests");
        SyntheticGameBuilder.CreateLooseFileGame(wsNoManifest.GameRootPath);
        IReadOnlyDictionary<string, string> origNoManifestHashes = wsNoManifest.SnapshotGameHashes();
        await using DatabaseSession dbNoManifest = await bridge.OpenDatabaseAsync(wsNoManifest.ConnectionString);
        const string projNoManifest = "proj-f18-t2-1b";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(dbNoManifest, projNoManifest, "Zero Manifest Gate", wsNoManifest.GameRootPath, "Loose");

        IBackupService validBackupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, dbNoManifest, wsNoManifest.BackupRootPath);
        PipelineOrchestratorHandle orchestratorNoManifest = AdaptersBackupUiTestHelper.CreatePipelineOrchestratorSafe(
            bridge, dbNoManifest, sanitizer, translator, validBackupService, [looseAdapter], wsNoManifest.BackupRootPath);
        List<SubtitleEntry> extracted2 = await looseAdapter.ExtractAsync(wsNoManifest.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated2 = extracted2
            .Select(e => e with { TranslatedText = $"[GATE] {e.RawText}", FinalPatchedText = $"[GATE] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();
        await dbNoManifest.InsertSubtitleEntriesAsync(translated2);

        await AdaptersBackupUiTestHelper.TryInvokePipelineAsync(
            orchestratorNoManifest,
            ["OpenProject"],
            projectId: projNoManifest,
            gameRootPath: wsNoManifest.GameRootPath,
            adapter: looseAdapter,
            entries: translated2,
            ct: CancellationToken.None);

        (bool hasGateQuery, object? gateRes) = await AdaptersBackupUiTestHelper.TryInvokePipelineAsync(
            orchestratorNoManifest,
            ["HasBackup", "CanPatch", "IsBackupComplete", "VerifyBackup"],
            projectId: projNoManifest,
            gameRootPath: wsNoManifest.GameRootPath,
            adapter: looseAdapter,
            entries: translated2,
            ct: CancellationToken.None);
        if (hasGateQuery && gateRes is bool canPatch)
        {
            Assert.False(canPatch);
        }

        Exception? directPatchEx = null;
        bool invokedDirectPatch = false;
        try
        {
            (invokedDirectPatch, _) = await AdaptersBackupUiTestHelper.TryInvokePipelineAsync(
                orchestratorNoManifest,
                ["PatchOnly", "ApplyPatch", "Patch"],
                excludedMethodSubstrings: ["RePatch", "PatchGame"],
                projectId: projNoManifest,
                gameRootPath: wsNoManifest.GameRootPath,
                adapter: looseAdapter,
                entries: translated2,
                ct: CancellationToken.None);
        }
        catch (Exception caught)
        {
            invokedDirectPatch = true;
            directPatchEx = caught;
        }

        if (invokedDirectPatch)
        {
            int manifestsAfter = await AdaptersBackupUiTestHelper.QueryBackupManifestCountAsync(dbNoManifest.Connection, projNoManifest);
            if (directPatchEx is not null)
            {
                Assert.False(
                    directPatchEx.Message.StartsWith("Could not bind", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected binding error: {directPatchEx.Message}");
                Assert.Equal(0, manifestsAfter);
                E2EAssertions.AssertFilesMatchHashes(wsNoManifest.GameRootPath, origNoManifestHashes);
            }
            else
            {
                // If Patch created a backup before patching, manifests must exist and match original disk backup
                Assert.True(manifestsAfter > 0, "Patch succeeded with 0 backup_manifests on disk — pre-patch backup gate bypassed!");
                E2EAssertions.AssertBackupManifestMatchesDisk(wsNoManifest.GameRootPath, dbNoManifest.Connection, projNoManifest);
            }
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public async Task RestoreBackupAsync_TamperedBackupFile_ThrowsHashMismatchAndAbortsRestore()
    {
        // Arrange
        using TempWorkspace ws = new("f18_tampered_backup");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t2-2";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Tamper Test", ws.GameRootPath, "Loose");
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);

        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [manifest.JsonRelativePath], CancellationToken.None);

        string backupFilePath;
        await using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT backup_file_path FROM backup_manifests WHERE project_id = @pid LIMIT 1;";
            cmd.Parameters.AddWithValue("@pid", projectId);
            backupFilePath = Convert.ToString(await cmd.ExecuteScalarAsync())!;
        }

        byte[] tamperedBytes = File.ReadAllBytes(backupFilePath);
        tamperedBytes[0] ^= 0xFF;
        File.WriteAllBytes(backupFilePath, tamperedBytes);

        const string sentinelOnGameDisk = "{\"sentinel\":\"must_not_be_overwritten_by_tampered_backup\"}";
        ws.WriteTextFile(manifest.JsonRelativePath, sentinelOnGameDisk);

        // Act
        Exception? ex = await Record.ExceptionAsync(
            () => backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        Assert.True(
            ex is InvalidOperationException or CryptographicException or IOException or InvalidDataException,
            $"Expected hash-mismatch exception, got {ex.GetType().FullName}: {ex.Message}");
        Assert.Equal(sentinelOnGameDisk, ws.ReadTextFile(manifest.JsonRelativePath));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public async Task RestoreBackupAsync_MissingBackupFileOnDisk_ThrowsFileNotFoundException()
    {
        // Arrange
        using TempWorkspace ws = new("f18_missing_backup_file");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t2-3";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Missing Backup File", ws.GameRootPath, "Loose");
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);

        await backupService.CreateBackupAsync(projectId, ws.GameRootPath, [manifest.SrtRelativePath], CancellationToken.None);
        string backupFilePath;
        await using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT backup_file_path FROM backup_manifests WHERE project_id = @pid LIMIT 1;";
            cmd.Parameters.AddWithValue("@pid", projectId);
            backupFilePath = Convert.ToString(await cmd.ExecuteScalarAsync())!;
        }

        File.Delete(backupFilePath);

        // Act & Assert
        Exception? ex = await Record.ExceptionAsync(
            () => backupService.RestoreBackupAsync(projectId, ws.GameRootPath, CancellationToken.None));
        Assert.NotNull(ex);
        Assert.True(
            ex is FileNotFoundException or InvalidOperationException,
            $"Expected FileNotFoundException or InvalidOperationException, got {ex.GetType().FullName}: {ex.Message}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public async Task CreateBackupAsync_PathTraversalRelativePath_ThrowsArgumentOrSecurityException()
    {
        // Arrange
        using TempWorkspace ws = new("f18_path_traversal");
        File.WriteAllText(Path.Combine(ws.RootPath, "outside_secret.txt"), "secret");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t2-4";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Traversal", ws.GameRootPath, "Loose");
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);

        // Act & Assert
        Exception? ex = await Record.ExceptionAsync(
            () => backupService.CreateBackupAsync(projectId, ws.GameRootPath, ["..\\outside_secret.txt"], CancellationToken.None));
        Assert.NotNull(ex);
        Assert.True(
            ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException,
            $"Expected ArgumentException/UnauthorizedAccessException/InvalidOperationException, got {ex.GetType().FullName}: {ex.Message}");
        Assert.Equal(0, await AdaptersBackupUiTestHelper.QueryBackupManifestCountAsync(db.Connection, projectId));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public async Task CreateBackupAsync_MissingSourceFile_ThrowsFileNotFoundException()
    {
        // Arrange
        using TempWorkspace ws = new("f18_missing_source");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        const string projectId = "proj-f18-t2-5";
        await AdaptersBackupUiTestHelper.SeedProjectAsync(db, projectId, "Missing Source", ws.GameRootPath, "Loose");
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);

        // Act & Assert
        await Assert.ThrowsAnyAsync<FileNotFoundException>(
            () => backupService.CreateBackupAsync(projectId, ws.GameRootPath, ["does_not_exist.json"], CancellationToken.None));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public async Task RestoreBackupAsync_UnknownProjectIdWithNoManifests_ThrowsOrNoOpsSafely()
    {
        // Arrange
        using TempWorkspace ws = new("f18_unknown_project");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.ConnectionString);
        IBackupService backupService = AdaptersBackupUiTestHelper.CreateBackupServiceSafe(bridge, db, ws.BackupRootPath);

        // Act
        Exception? ex = await Record.ExceptionAsync(
            () => backupService.RestoreBackupAsync("non-existent-project", ws.GameRootPath, CancellationToken.None));

        // Assert
        if (ex is not null)
        {
            Assert.IsAssignableFrom<InvalidOperationException>(ex);
        }

        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }
}
