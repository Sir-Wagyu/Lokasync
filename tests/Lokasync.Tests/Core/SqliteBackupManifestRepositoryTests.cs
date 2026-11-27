using Microsoft.Data.Sqlite;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Xunit;

namespace Lokasync.Tests.Core;

public sealed class SqliteBackupManifestRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _factory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteProjectRepository _projectRepository;
    private readonly SqliteBackupManifestRepository _manifestRepository;

    public SqliteBackupManifestRepositoryTests()
    {
        _projectRepository = new SqliteProjectRepository(_factory);
        _manifestRepository = new SqliteBackupManifestRepository(_factory);
    }

    public async Task InitializeAsync()
    {
        await _factory.InitializeSchemaAsync();
        await _projectRepository.CreateAsync(new ProjectRecord(
            Id: "proj-main",
            GameTitle: "Main Game",
            GameRootPath: @"C:\Games\Main",
            DetectedEngine: "Loose",
            NarrativeContextJson: null,
            CreatedAt: DateTimeOffset.UtcNow));
        await _projectRepository.CreateAsync(new ProjectRecord(
            Id: "proj-other",
            GameTitle: "Other Game",
            GameRootPath: @"C:\Games\Other",
            DetectedEngine: "Unity",
            NarrativeContextJson: null,
            CreatedAt: DateTimeOffset.UtcNow));
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task UpsertAsync_ValidManifestWithExistingProject_PersistsAndReturnsId()
    {
        // Arrange
        var manifest = new BackupManifestRecord(
            Id: 0,
            ProjectId: "proj-main",
            OriginalRelativePath: @"data\Map001.json",
            BackupFilePath: @"C:\Backups\Map001.json",
            Sha256Hash: "abc123",
            BackedUpAt: DateTimeOffset.UtcNow);

        // Act
        var saved = await _manifestRepository.UpsertAsync(manifest);
        var fetched = await _manifestRepository.GetByRelativePathAsync("proj-main", @"data\Map001.json");

        // Assert
        Assert.True(saved.Id > 0);
        Assert.NotNull(fetched);
        Assert.Equal("abc123", fetched.Sha256Hash);
    }

    [Fact]
    public async Task UpsertAsync_SameProjectAndRelativePath_UpdatesExistingManifest()
    {
        // Arrange
        await _manifestRepository.UpsertAsync(new BackupManifestRecord(
            0, "proj-main", @"data\dialog.srt", @"C:\Backups\v1\dialog.srt", "hash-v1", DateTimeOffset.UtcNow));

        // Act
        await _manifestRepository.UpsertAsync(new BackupManifestRecord(
            0, "proj-main", @"data\dialog.srt", @"C:\Backups\v2\dialog.srt", "hash-v2", DateTimeOffset.UtcNow));
        var manifests = await _manifestRepository.GetByProjectIdAsync("proj-main");

        // Assert
        Assert.Single(manifests);
        Assert.Equal("hash-v2", manifests[0].Sha256Hash);
        Assert.Equal(@"C:\Backups\v2\dialog.srt", manifests[0].BackupFilePath);
    }

    [Fact]
    public async Task BulkUpsertAsync_MultipleManifests_PersistsAllInSingleTransaction()
    {
        // Arrange
        var batch = new[]
        {
            new BackupManifestRecord(0, "proj-main", "b.json", @"C:\Backups\b.json", "hash-b", DateTimeOffset.UtcNow),
            new BackupManifestRecord(0, "proj-main", "a.json", @"C:\Backups\a.json", "hash-a", DateTimeOffset.UtcNow)
        };

        // Act
        var persisted = await _manifestRepository.BulkUpsertAsync(batch);
        var fetched = await _manifestRepository.GetByProjectIdAsync("proj-main");

        // Assert
        Assert.Equal(2, persisted.Count);
        Assert.Equal(2, fetched.Count);
        Assert.Equal("a.json", fetched[0].OriginalRelativePath);
        Assert.Equal("b.json", fetched[1].OriginalRelativePath);
    }

    [Fact]
    public async Task GetByProjectIdAsync_MultipleProjects_ReturnsOnlyTargetProjectManifests()
    {
        // Arrange
        await _manifestRepository.UpsertAsync(new BackupManifestRecord(0, "proj-main", "main.srt", @"C:\B\main.srt", "h1", DateTimeOffset.UtcNow));
        await _manifestRepository.UpsertAsync(new BackupManifestRecord(0, "proj-other", "other.srt", @"C:\B\other.srt", "h2", DateTimeOffset.UtcNow));

        // Act
        var mainManifests = await _manifestRepository.GetByProjectIdAsync("proj-main");

        // Assert
        Assert.Single(mainManifests);
        Assert.Equal("main.srt", mainManifests[0].OriginalRelativePath);
    }

    [Fact]
    public async Task UpsertAsync_MissingProjectForeignKey_ThrowsSqliteException()
    {
        // Arrange
        var orphanManifest = new BackupManifestRecord(
            0, "non-existent-project", "file.srt", @"C:\B\file.srt", "hash", DateTimeOffset.UtcNow);

        // Act & Assert
        await Assert.ThrowsAsync<SqliteException>(() => _manifestRepository.UpsertAsync(orphanManifest));
    }

    [Fact]
    public async Task DeleteByProjectIdAsync_ExistingProject_DeletesManifests()
    {
        // Arrange
        await _manifestRepository.UpsertAsync(new BackupManifestRecord(0, "proj-main", "main.srt", @"C:\B\main.srt", "h1", DateTimeOffset.UtcNow));

        // Act
        var deleted = await _manifestRepository.DeleteByProjectIdAsync("proj-main");
        var hasAny = await _manifestRepository.HasManifestsForProjectAsync("proj-main");

        // Assert
        Assert.Equal(1, deleted);
        Assert.False(hasAny);
    }
}
