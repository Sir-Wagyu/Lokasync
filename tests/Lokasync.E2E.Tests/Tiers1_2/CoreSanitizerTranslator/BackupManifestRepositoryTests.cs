using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.CoreSanitizerTranslator;

public sealed class BackupManifestRepositoryTests
{
    private static readonly string SampleShaLower = new('a', 64);

    private static Task SeedProjectAsync(DatabaseSession db, string projectId) =>
        CoreTranslatorTestHelpers.CreateProjectAsync(
            db,
            projectId,
            $"Game_{projectId}",
            $"C:\\Games\\{projectId}",
            "LooseFile",
            null);

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F4")]
    public async Task AddManifestAsync_ValidManifest_PersistsAllColumnsAndTimestamp()
    {
        // Arrange
        using TempWorkspace ws = new("f4_add");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await SeedProjectAsync(db, "proj-1");
        string backupPath = System.IO.Path.Combine(ws.BackupRootPath, "Map001.json.bak");

        // Act
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(
            db, "proj-1", "data/Map001.json", backupPath, SampleShaLower);
        IReadOnlyList<BackupManifestSnapshot> manifests =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "proj-1");

        // Assert
        Assert.Single(manifests);
        Assert.Equal("proj-1", manifests[0].ProjectId);
        Assert.Equal("data/Map001.json", manifests[0].OriginalRelativePath);
        Assert.Equal(backupPath, manifests[0].BackupFilePath);
        Assert.Equal(SampleShaLower, manifests[0].Sha256Hash);
        Assert.False(string.IsNullOrWhiteSpace(manifests[0].BackedUpAt));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F4")]
    public async Task GetManifestsByProjectIdAsync_MultipleFilesForSameProject_ReturnsAllManifests()
    {
        // Arrange
        using TempWorkspace ws = new("f4_multi");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await SeedProjectAsync(db, "proj-1");
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-1", "data/Map001.json", "C:\\bak\\1", SampleShaLower);
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-1", "data/Map002.json", "C:\\bak\\2", new string('b', 64));
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-1", "data/CommonEvents.json", "C:\\bak\\3", new string('c', 64));

        // Act
        IReadOnlyList<BackupManifestSnapshot> manifests =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "proj-1");

        // Assert
        Assert.Equal(3, manifests.Count);
        Assert.Equal(3, manifests.Select(m => m.OriginalRelativePath).Distinct().Count());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F4")]
    public async Task GetManifestsByProjectIdAsync_MultipleProjectsInDb_IsolatesResultsByProjectId()
    {
        // Arrange
        using TempWorkspace ws = new("f4_isolate");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await SeedProjectAsync(db, "proj-A");
        await SeedProjectAsync(db, "proj-B");
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-A", "a1.srt", "C:\\bak\\a1", SampleShaLower);
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-A", "a2.srt", "C:\\bak\\a2", new string('b', 64));
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-B", "b1.srt", "C:\\bak\\b1", new string('c', 64));

        // Act
        IReadOnlyList<BackupManifestSnapshot> projA =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "proj-A");

        // Assert
        Assert.Equal(2, projA.Count);
        Assert.All(projA, m => Assert.Equal("proj-A", m.ProjectId));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F4")]
    public async Task AddManifestAsync_LowerAndUpperHexSha256_PreservesExact64CharDigest()
    {
        // Arrange
        using TempWorkspace ws = new("f4_sha");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await SeedProjectAsync(db, "proj-sha");
        string mixedSha = "0123456789abcdefABCDEF0123456789abcdefABCDEF0123456789abcdefABCD";

        // Act
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-sha", "file.xml", "C:\\bak\\file.xml", mixedSha);
        IReadOnlyList<BackupManifestSnapshot> manifests =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "proj-sha");

        // Assert
        Assert.Single(manifests);
        Assert.Equal(64, manifests[0].Sha256Hash.Length);
        Assert.Equal(mixedSha, manifests[0].Sha256Hash);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F4")]
    public async Task AddManifestAsync_WindowsRelativeSubpaths_PreservesBackslashOrForwardSlashPaths()
    {
        // Arrange
        using TempWorkspace ws = new("f4_win_subpath");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await SeedProjectAsync(db, "proj-ue");
        string relPath = "Content\\Paks\\pakchunk0-WindowsNoEditor.pak";

        // Act
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-ue", relPath, "C:\\bak\\pak", SampleShaLower);
        IReadOnlyList<BackupManifestSnapshot> manifests =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "proj-ue");

        // Assert
        Assert.Single(manifests);
        Assert.Equal(relPath, manifests[0].OriginalRelativePath);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F4")]
    public async Task AddManifestAsync_BatchOrRepeatedInsertsForProject_PreservesAllEntriesWithoutOverwrite()
    {
        // Arrange
        using TempWorkspace ws = new("f4_repeated");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await SeedProjectAsync(db, "proj-1");

        // Act
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-1", "Map001.json", "C:\\bak\\Map001.json", SampleShaLower);
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-1", "CommonEvents.json", "C:\\bak\\CommonEvents.json", new string('d', 64));
        IReadOnlyList<BackupManifestSnapshot> manifests =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "proj-1");

        // Assert
        Assert.Equal(2, manifests.Count);
        Assert.NotEqual(manifests[0].Id, manifests[1].Id);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F4")]
    public async Task GetManifestsByProjectIdAsync_UnknownProjectId_ReturnsEmptyCollection()
    {
        // Arrange
        using TempWorkspace ws = new("f4_unknown");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await SeedProjectAsync(db, "proj-1");
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-1", "Map001.json", "C:\\bak\\Map001.json", SampleShaLower);

        // Act
        IReadOnlyList<BackupManifestSnapshot> missing =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "unknown-proj");

        // Assert
        Assert.NotNull(missing);
        Assert.Empty(missing);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F4")]
    public async Task AddManifestAsync_SpacesQuotesAndSpecialCharsInWindowsPaths_SafelyParameterized()
    {
        // Arrange
        using TempWorkspace ws = new("f4_special_chars");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await SeedProjectAsync(db, "proj-sp");
        string trickyRel = "Sub Folder\\Bob's [Mod] (v1.0); --\\file.srt";
        string trickyBak = System.IO.Path.Combine(ws.BackupRootPath, "Bob's [Backup]; --.bak");

        // Act
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-sp", trickyRel, trickyBak, SampleShaLower);
        IReadOnlyList<BackupManifestSnapshot> manifests =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "proj-sp");

        // Assert
        Assert.Single(manifests);
        Assert.Equal(trickyRel, manifests[0].OriginalRelativePath);
        Assert.Equal(trickyBak, manifests[0].BackupFilePath);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F4")]
    public async Task AddManifestAsync_UnicodeCharactersInPaths_PreservesExactCharacters()
    {
        // Arrange
        using TempWorkspace ws = new("f4_unicode");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await SeedProjectAsync(db, "proj-uni");
        string unicodePath = "Data\\Lokalisasi_Bahasa_日本語\\dialog.xml";

        // Act
        await CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-uni", unicodePath, "C:\\bak\\日本語\\dialog.xml", SampleShaLower);
        IReadOnlyList<BackupManifestSnapshot> manifests =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "proj-uni");

        // Assert
        Assert.Single(manifests);
        Assert.Equal(unicodePath, manifests[0].OriginalRelativePath);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F4")]
    public async Task AddManifestAsync_NullRequiredField_ThrowsSqliteOrArgumentNullException()
    {
        // Arrange
        using TempWorkspace ws = new("f4_null_field");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);

        // Act
        Exception? ex = await Record.ExceptionAsync(() =>
            CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-null", null!, "C:\\bak\\a", null!));

        // Assert
        Assert.NotNull(ex);
        Assert.True(ex is SqliteException or ArgumentException or ArgumentNullException,
            $"Unexpected exception type: {ex!.GetType().FullName}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F4")]
    public async Task GetManifestsByProjectIdAsync_EmptyTable_ReturnsEmptyCollection()
    {
        // Arrange
        using TempWorkspace ws = new("f4_empty_table");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);

        // Act
        IReadOnlyList<BackupManifestSnapshot> manifests =
            await CoreTranslatorTestHelpers.GetBackupManifestsByProjectIdAsync(db, "proj-1");

        // Assert
        Assert.NotNull(manifests);
        Assert.Empty(manifests);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F4")]
    public async Task AddManifestAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using TempWorkspace ws = new("f4_cancel");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoreTranslatorTestHelpers.AddBackupManifestAsync(db, "proj-1", "a.srt", "C:\\bak\\a.srt", SampleShaLower, cts.Token));
    }
}
