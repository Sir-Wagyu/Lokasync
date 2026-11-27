using System.IO;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Xunit;

namespace Lokasync.Tests.Tier5;

public sealed class CoreHardeningTests
{
    [Fact]
    public async Task SqliteConnectionFactory_AfterDisposeAndCancel_ThrowsExpectedExceptionsIdempotently()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "Lokasync_Tier5_Core", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempDb = Path.Combine(tempDir, "hardening.db");

        try
        {
            SqliteConnectionFactory memoryFactory = SqliteConnectionFactory.ForInMemory();
            SqliteConnectionFactory fileFactory = SqliteConnectionFactory.ForFile(tempDb, pooling: false);

            await memoryFactory.InitializeSchemaAsync();
            await fileFactory.InitializeSchemaAsync();

            using CancellationTokenSource cts = new();
            cts.Cancel();

            // Act & Assert — cancelled token throws OperationCanceledException
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => memoryFactory.OpenConnectionAsync(cts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fileFactory.OpenConnectionAsync(cts.Token));

            // Act — idempotent double dispose
            await memoryFactory.DisposeAsync();
            await memoryFactory.DisposeAsync();
            await fileFactory.DisposeAsync();
            await fileFactory.DisposeAsync();

            // Assert — post-dispose open throws ObjectDisposedException
            await Assert.ThrowsAsync<ObjectDisposedException>(() => memoryFactory.OpenConnectionAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fileFactory.OpenConnectionAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SqliteProjectRepository_DeleteAsync_CascadesToBackupManifests()
    {
        // Arrange
        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();

        SqliteProjectRepository projectRepo = new(factory);
        SqliteBackupManifestRepository manifestRepo = new(factory);

        await projectRepo.CreateAsync(new ProjectRecord(
            Id: "p-cas",
            GameTitle: "Cascade Game",
            GameRootPath: @"C:\Games\Cascade",
            DetectedEngine: "LooseFile",
            NarrativeContextJson: null,
            CreatedAt: DateTimeOffset.UtcNow));

        await manifestRepo.UpsertAsync(new BackupManifestRecord(
            Id: 0,
            ProjectId: "p-cas",
            OriginalRelativePath: "dialog.json",
            BackupFilePath: @"C:\Backups\p-cas\dialog.json",
            Sha256Hash: new string('a', 64),
            BackedUpAt: DateTimeOffset.UtcNow));

        Assert.True(await manifestRepo.HasManifestsForProjectAsync("p-cas"));

        // Act
        bool deleted = await projectRepo.DeleteAsync("p-cas");
        bool hasManifestsAfter = await manifestRepo.HasManifestsForProjectAsync("p-cas");

        // Assert
        Assert.True(deleted);
        Assert.False(hasManifestsAfter);
    }

    [Fact]
    public async Task SqliteSubtitleEntryRepository_UpdateEntryByKeyAndBulkUpdateRollback_BehavesAtomically()
    {
        // Arrange
        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        SqliteSubtitleEntryRepository entryRepo = new(factory);

        IReadOnlyList<SubtitleEntry> seeded = await entryRepo.BulkUpsertAsync(
        [
            new SubtitleEntry(0, "k1", "dialog.json", "/k1", "One", "One", new Dictionary<string, string>(), null, null, EntryStatus.Pending, false),
            new SubtitleEntry(0, "k2", "dialog.json", "/k2", "Two", "Two", new Dictionary<string, string>(), null, null, EntryStatus.Pending, false)
        ]);

        SubtitleEntry k1 = seeded[0];
        SubtitleEntry k2 = seeded[1];

        SubtitleEntry[] invalidBatch =
        [
            k1 with { Status = EntryStatus.Translated, TranslatedText = "Satu", FinalPatchedText = "Satu" },
            k2 with { Status = (EntryStatus)999 }
        ];

        SubtitleEntry k2ByKey = k2 with
        {
            Id = 0,
            Status = EntryStatus.ManuallyEdited,
            TranslatedText = "Edit",
            FinalPatchedText = "Edit"
        };

        SubtitleEntry missingByKey = k2 with
        {
            Id = 0,
            EntryKey = "non_existent_key"
        };

        // Act
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => entryRepo.BulkUpdateAsync(invalidBatch));
        bool updatedK2 = await entryRepo.UpdateEntryAsync(k2ByKey);
        bool updatedMissing = await entryRepo.UpdateEntryAsync(missingByKey);

        // Assert
        SubtitleEntry? storedK1 = await entryRepo.GetByEntryKeyAsync("k1");
        SubtitleEntry? storedK2 = await entryRepo.GetByEntryKeyAsync("k2");

        Assert.NotNull(storedK1);
        Assert.Equal(EntryStatus.Pending, storedK1!.Status);
        Assert.Null(storedK1.FinalPatchedText);

        Assert.True(updatedK2);
        Assert.NotNull(storedK2);
        Assert.Equal(EntryStatus.ManuallyEdited, storedK2!.Status);
        Assert.Equal("Edit", storedK2.FinalPatchedText);

        Assert.False(updatedMissing);
    }

    [Fact]
    public async Task SqliteSubtitleEntryRepository_QueryAsync_EscapesBackslashAndUnderscoreWildcards()
    {
        // Arrange
        await using SqliteConnectionFactory factory = SqliteConnectionFactory.ForInMemory();
        await factory.InitializeSchemaAsync();
        SqliteSubtitleEntryRepository entryRepo = new(factory);

        await entryRepo.BulkUpsertAsync(
        [
            new SubtitleEntry(0, @"path\file_1", "a.json", null, @"path\file_1", @"path\file_1", new Dictionary<string, string>(), null, null, EntryStatus.Pending, false),
            new SubtitleEntry(0, @"path\fileX1", "a.json", null, @"path\fileX1", @"path\fileX1", new Dictionary<string, string>(), null, null, EntryStatus.Pending, false),
            new SubtitleEntry(0, "other", "a.json", null, "other", "other", new Dictionary<string, string>(), null, null, EntryStatus.Pending, false)
        ]);

        // Act
        IReadOnlyList<SubtitleEntry> underscoreResults = await entryRepo.QueryAsync(searchText: "file_1");
        IReadOnlyList<SubtitleEntry> backslashResults = await entryRepo.QueryAsync(searchText: @"path\file");

        // Assert
        SubtitleEntry single = Assert.Single(underscoreResults);
        Assert.Equal(@"path\file_1", single.EntryKey);

        Assert.Equal(2, backslashResults.Count);
        Assert.Contains(backslashResults, e => e.EntryKey == @"path\file_1");
        Assert.Contains(backslashResults, e => e.EntryKey == @"path\fileX1");
    }

    [Fact]
    public void EntryStatusMapper_InvalidEnumOrBlankString_ThrowsExpectedException()
    {
        // Arrange
        const EntryStatus invalidEnum = (EntryStatus)999;
        const string blankString = "   ";

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => EntryStatusMapper.ToDbString(invalidEnum));
        Assert.Throws<ArgumentException>(() => EntryStatusMapper.FromDbString(blankString));
    }
}
