using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lokasync.App.Services;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Xunit;

namespace Lokasync.Tests.App;

public sealed class BackupServiceTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _connectionFactory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteProjectRepository _projectRepository;
    private readonly SqliteBackupManifestRepository _manifestRepository;
    private readonly string _tempRoot;
    private readonly string _gameRoot;
    private readonly string _backupRoot;
    private readonly string _projectId;

    public BackupServiceTests()
    {
        _projectRepository = new SqliteProjectRepository(_connectionFactory);
        _manifestRepository = new SqliteBackupManifestRepository(_connectionFactory);
        _tempRoot = Path.Combine(Path.GetTempPath(), "Lokasync_BackupTests", Guid.NewGuid().ToString("N"));
        _gameRoot = Path.Combine(_tempRoot, "Game");
        _backupRoot = Path.Combine(_tempRoot, "Backups");
        Directory.CreateDirectory(_gameRoot);
        Directory.CreateDirectory(_backupRoot);
        _projectId = SqliteConnectionFactory.ComputeProjectHash(_gameRoot);
    }

    public async Task InitializeAsync()
    {
        await _connectionFactory.InitializeSchemaAsync();
        await _projectRepository.UpsertAsync(new ProjectRecord(
            Id: _projectId,
            GameTitle: "Test Game",
            GameRootPath: _gameRoot,
            DetectedEngine: "LooseFile",
            NarrativeContextJson: null,
            CreatedAt: DateTimeOffset.UtcNow));
    }

    public async Task DisposeAsync()
    {
        await _connectionFactory.DisposeAsync();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_ValidFiles_CopiesToBackupRootAndRecordsSha256Manifests()
    {
        // Arrange
        string subDir = Path.Combine(_gameRoot, "subs");
        Directory.CreateDirectory(subDir);
        string srtPath = Path.Combine(subDir, "intro.srt");
        byte[] srtBytes = Encoding.UTF8.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nHello world!\r\n");
        await File.WriteAllBytesAsync(srtPath, srtBytes);

        BackupService service = new(_manifestRepository, _backupRoot);
        string expectedHash = Convert.ToHexString(SHA256.HashData(srtBytes)).ToLowerInvariant();

        // Act
        await service.CreateBackupAsync(_projectId, _gameRoot, ["subs\\intro.srt"], CancellationToken.None);

        // Assert
        IReadOnlyList<BackupManifestRecord> manifests = await _manifestRepository.GetByProjectIdAsync(_projectId);
        BackupManifestRecord manifest = Assert.Single(manifests);
        Assert.Equal("subs/intro.srt", manifest.OriginalRelativePath);
        Assert.Equal(expectedHash, manifest.Sha256Hash);
        Assert.True(File.Exists(manifest.BackupFilePath));
        Assert.Equal(srtBytes, await File.ReadAllBytesAsync(manifest.BackupFilePath));
    }

    [Fact]
    public async Task CreateBackupAsync_PathTraversalRelativePath_ThrowsInvalidOperationException()
    {
        // Arrange
        BackupService service = new(_manifestRepository, _backupRoot);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateBackupAsync(_projectId, _gameRoot, ["../outside.txt"], CancellationToken.None));
    }

    [Fact]
    public async Task CreateBackupAsync_MissingSourceFile_ThrowsFileNotFoundException()
    {
        // Arrange
        BackupService service = new(_manifestRepository, _backupRoot);

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.CreateBackupAsync(_projectId, _gameRoot, ["missing.json"], CancellationToken.None));
    }

    [Fact]
    public async Task EnsureBackupVerifiedAsync_WhenNoBackupCreated_ThrowsInvalidOperationException()
    {
        // Arrange
        BackupService service = new(_manifestRepository, _backupRoot);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureBackupVerifiedAsync(_projectId, ["dialog.json"], CancellationToken.None));
    }

    [Fact]
    public async Task EnsureBackupVerifiedAsync_WhenBackupFileTamperedOrDeleted_ThrowsInvalidOperationException()
    {
        // Arrange
        string dialogPath = Path.Combine(_gameRoot, "dialog.json");
        await File.WriteAllTextAsync(dialogPath, "{\"k\":\"hello\"}");

        BackupService service = new(_manifestRepository, _backupRoot);
        await service.CreateBackupAsync(_projectId, _gameRoot, ["dialog.json"], CancellationToken.None);

        BackupManifestRecord manifest = Assert.Single(await service.GetManifestsAsync(_projectId));
        await File.WriteAllTextAsync(manifest.BackupFilePath, "{\"k\":\"TAMPERED\"}");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureBackupVerifiedAsync(_projectId, ["dialog.json"], CancellationToken.None));

        // Also verify deletion triggers failure
        File.Delete(manifest.BackupFilePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureBackupVerifiedAsync(_projectId, ["dialog.json"], CancellationToken.None));
    }

    [Fact]
    public async Task EnsureBackupVerifiedAsync_WhenAllBackupsMatchSha256_CompletesWithoutThrowing()
    {
        // Arrange
        string dialogPath = Path.Combine(_gameRoot, "dialog.json");
        await File.WriteAllTextAsync(dialogPath, "{\"k\":\"hello\"}");

        BackupService service = new(_connectionFactory, _backupRoot);
        await service.CreateBackupAsync(_projectId, _gameRoot, ["dialog.json"], CancellationToken.None);

        // Act
        await service.EnsureBackupVerifiedAsync(_projectId, ["dialog.json"], CancellationToken.None);

        // Assert
        Assert.True(await service.HasVerifiedBackupAsync(_projectId, ["dialog.json"]));
    }

    [Fact]
    public async Task RestoreBackupAsync_AfterInPlaceModification_RestoresByteIdenticalOriginalFiles()
    {
        // Arrange
        string dialogPath = Path.Combine(_gameRoot, "dialog.json");
        byte[] originalBytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"line\":\"Hello!\"}")];
        await File.WriteAllBytesAsync(dialogPath, originalBytes);

        BackupService service = new(_manifestRepository, _backupRoot);
        await service.CreateBackupAsync(_projectId, _gameRoot, ["dialog.json"], CancellationToken.None);

        // Simulate in-place patching
        await File.WriteAllTextAsync(dialogPath, "{\"line\":\"Halo!\"}");

        // Act
        await service.RestoreBackupAsync(_projectId, _gameRoot, CancellationToken.None);

        // Assert
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(dialogPath));
    }

    [Fact]
    public async Task RestoreBackupAsync_WhenBackupHashCorrupted_ThrowsBeforeOverwritingGameFiles()
    {
        // Arrange
        string dialogPath = Path.Combine(_gameRoot, "dialog.json");
        await File.WriteAllTextAsync(dialogPath, "{\"line\":\"Original\"}");

        BackupService service = new(_manifestRepository, _backupRoot);
        await service.CreateBackupAsync(_projectId, _gameRoot, ["dialog.json"], CancellationToken.None);

        string patchedContent = "{\"line\":\"Patched\"}";
        await File.WriteAllTextAsync(dialogPath, patchedContent);

        BackupManifestRecord manifest = Assert.Single(await service.GetManifestsAsync(_projectId));
        await File.WriteAllTextAsync(manifest.BackupFilePath, "{\"line\":\"CORRUPTED\"}");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreBackupAsync(_projectId, _gameRoot, CancellationToken.None));

        Assert.Equal(patchedContent, await File.ReadAllTextAsync(dialogPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreBackupAsync_UnrealProjectWithModsPak_RestoresAndDeletesLokasyncModsPak(bool nestedSubdir)
    {
        // Arrange
        string paksRelDir = nestedSubdir ? Path.Combine("MyGame", "Content", "Paks") : Path.Combine("Content", "Paks");
        string paksDir = Path.Combine(_gameRoot, paksRelDir);
        string modsDir = Path.Combine(paksDir, "~mods");
        Directory.CreateDirectory(modsDir);

        string sourcePakPath = Path.Combine(paksDir, "pakchunk0-WindowsNoEditor.pak");
        byte[] originalPakBytes = Encoding.ASCII.GetBytes("ORIGINAL_PAK_CONTENT");
        await File.WriteAllBytesAsync(sourcePakPath, originalPakBytes);

        string relPak = nestedSubdir
            ? "MyGame/Content/Paks/pakchunk0-WindowsNoEditor.pak"
            : "Content/Paks/pakchunk0-WindowsNoEditor.pak";

        BackupService service = new(_manifestRepository, _backupRoot);
        await service.CreateBackupAsync(_projectId, _gameRoot, [relPak], CancellationToken.None);

        string modsPakPath = Path.Combine(modsDir, "Lokasync_ID_P.pak");
        await File.WriteAllBytesAsync(modsPakPath, Encoding.ASCII.GetBytes("MOD_PAK"));

        // Act
        await service.RestoreBackupAsync(_projectId, _gameRoot, CancellationToken.None);

        // Assert
        Assert.False(File.Exists(modsPakPath));
        Assert.False(Directory.Exists(modsDir));
        Assert.Equal(originalPakBytes, await File.ReadAllBytesAsync(sourcePakPath));
    }

    [Fact]
    public async Task CreateBackupAsync_CalledAgainAfterGameFileModified_PreservesOriginalBackupAndBacksUpNewFile()
    {
        // Arrange
        string dialogPath = Path.Combine(_gameRoot, "dialog.json");
        byte[] originalDialogBytes = Encoding.UTF8.GetBytes("{\"line\":\"Original Hello\"}");
        await File.WriteAllBytesAsync(dialogPath, originalDialogBytes);

        BackupService service = new(_manifestRepository, _backupRoot);
        await service.CreateBackupAsync(_projectId, _gameRoot, ["dialog.json"], CancellationToken.None);

        BackupManifestRecord initialManifest = Assert.Single(await service.GetManifestsAsync(_projectId));
        string expectedOriginalHash = initialManifest.Sha256Hash;

        // Simulate patched game file on disk plus a newly added second file
        await File.WriteAllTextAsync(dialogPath, "{\"line\":\"Patched Halo\"}");
        string extraPath = Path.Combine(_gameRoot, "extra.json");
        byte[] extraOriginalBytes = Encoding.UTF8.GetBytes("{\"extra\":\"New\"}");
        await File.WriteAllBytesAsync(extraPath, extraOriginalBytes);

        // Act
        await service.CreateBackupAsync(_projectId, _gameRoot, ["dialog.json", "extra.json"], CancellationToken.None);

        // Assert
        IReadOnlyList<BackupManifestRecord> manifests = await service.GetManifestsAsync(_projectId);
        Assert.Equal(2, manifests.Count);

        BackupManifestRecord dialogManifest = manifests.Single(m => m.OriginalRelativePath == "dialog.json");
        Assert.Equal(expectedOriginalHash, dialogManifest.Sha256Hash);
        Assert.Equal(originalDialogBytes, await File.ReadAllBytesAsync(dialogManifest.BackupFilePath));

        BackupManifestRecord extraManifest = manifests.Single(m => m.OriginalRelativePath == "extra.json");
        Assert.Equal(extraOriginalBytes, await File.ReadAllBytesAsync(extraManifest.BackupFilePath));

        await service.RestoreBackupAsync(_projectId, _gameRoot, CancellationToken.None);
        Assert.Equal(originalDialogBytes, await File.ReadAllBytesAsync(dialogPath));
    }
}
