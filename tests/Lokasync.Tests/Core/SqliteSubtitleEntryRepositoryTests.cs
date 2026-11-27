using Microsoft.Data.Sqlite;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Xunit;

namespace Lokasync.Tests.Core;

public sealed class SqliteSubtitleEntryRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _factory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteSubtitleEntryRepository _repository;

    public SqliteSubtitleEntryRepositoryTests()
    {
        _repository = new SqliteSubtitleEntryRepository(_factory);
    }

    public Task InitializeAsync() => _factory.InitializeSchemaAsync();

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task BulkUpsertAsync_BatchOfEntries_PersistsAllAndAssignsIds()
    {
        // Arrange
        var entries = new[]
        {
            CreateSampleEntry("key-1", "Hello <color=#FFF>World</color>", "[TAG_0]Hello[TAG_1]", new() { ["[TAG_0]"] = "<color=#FFF>", ["[TAG_1]"] = "</color>" }),
            CreateSampleEntry("key-2", "Second line", "Second line", new())
        };

        // Act
        var saved = await _repository.BulkUpsertAsync(entries);
        var fetchedAll = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(2, saved.Count);
        Assert.All(saved, e => Assert.True(e.Id > 0));
        Assert.Equal(2, fetchedAll.Count);
        Assert.Equal("<color=#FFF>", fetchedAll[0].Placeholders["[TAG_0]"]);
    }

    [Fact]
    public async Task BulkUpsertAsync_QuotesSqlInjectionAndControlCharacters_RoundTripsVerbatim()
    {
        // Arrange
        const string injectionPayload = "It's \"fine\"; DROP TABLE subtitle_entries; --";
        const string richControlPayload = "<color=#FFD700>O'Connor</color>\r\n\t日本語";
        var entries = new[]
        {
            CreateSampleEntry("inj-1", injectionPayload, injectionPayload, new()),
            CreateSampleEntry("inj-2", richControlPayload, "[TAG_0]O'Connor[TAG_1][NL_0][TAB_0]日本語", new()
            {
                ["[TAG_0]"] = "<color=#FFD700>",
                ["[TAG_1]"] = "</color>",
                ["[NL_0]"] = "\r\n",
                ["[TAB_0]"] = "\t"
            })
        };

        // Act
        await _repository.BulkUpsertAsync(entries);
        var fetched1 = await _repository.GetByEntryKeyAsync("inj-1");
        var fetched2 = await _repository.GetByEntryKeyAsync("inj-2");

        // Assert
        Assert.NotNull(fetched1);
        Assert.Equal(injectionPayload, fetched1.RawText);
        Assert.NotNull(fetched2);
        Assert.Equal(richControlPayload, fetched2.RawText);
        Assert.Equal("\r\n", fetched2.Placeholders["[NL_0]"]);
        Assert.Equal("\t", fetched2.Placeholders["[TAB_0]"]);
    }

    [Fact]
    public async Task BulkUpsertAsync_ExistingEntryKey_UpdatesRowWithoutDuplication()
    {
        // Arrange
        var initial = CreateSampleEntry("dup-key", "Original", "Original", new());
        await _repository.BulkUpsertAsync([initial]);

        var updated = initial with
        {
            TranslatedText = "Terjemahan",
            FinalPatchedText = "Terjemahan",
            Status = EntryStatus.Translated
        };

        // Act
        await _repository.BulkUpsertAsync([updated]);
        var count = await _repository.CountAsync();
        var fetched = await _repository.GetByEntryKeyAsync("dup-key");

        // Assert
        Assert.Equal(1, count);
        Assert.NotNull(fetched);
        Assert.Equal(EntryStatus.Translated, fetched.Status);
        Assert.Equal("Terjemahan", fetched.TranslatedText);
    }

    [Fact]
    public async Task BulkUpsertAsync_MidBatchConstraintFailure_RollsBackEntireTransaction()
    {
        // Arrange
        var batch = new[]
        {
            CreateSampleEntry("atomic-1", "Valid first row", "Valid first row", new()),
            new SubtitleEntry(0, null!, "dialog.json", null, "Invalid null key", "Invalid", new(), null, null, EntryStatus.Pending, false)
        };

        // Act & Assert
        await Assert.ThrowsAsync<SqliteException>(() => _repository.BulkUpsertAsync(batch));

        var count = await _repository.CountAsync();
        var first = await _repository.GetByEntryKeyAsync("atomic-1");
        Assert.Equal(0, count);
        Assert.Null(first);
    }

    [Fact]
    public async Task GetPendingAsync_AfterPartialProgressCheckpoint_ReturnsOnlyPendingEntries()
    {
        // Arrange
        var initial = Enumerable.Range(1, 5)
            .Select(i => CreateSampleEntry($"resume-{i}", $"Line {i}", $"Line {i}", new()))
            .ToList();
        var persisted = await _repository.BulkUpsertAsync(initial);

        await _repository.UpdateTranslationAsync(persisted[0].Id, "Baris 1", "Baris 1", EntryStatus.Translated, false);
        await _repository.UpdateTranslationAsync(persisted[1].Id, "Baris 2", "Baris 2", EntryStatus.Translated, false);
        await _repository.UpdateTranslationAsync(persisted[2].Id, "Baris 3 [BROKEN]", "Line 3", EntryStatus.FailedValidation, false);

        // Act
        var pending = await _repository.GetPendingAsync();

        // Assert
        Assert.Equal(2, pending.Count);
        Assert.Equal(["resume-4", "resume-5"], pending.Select(p => p.EntryKey).ToArray());
    }

    [Fact]
    public async Task GetAllAsync_LegacyAndCanonicalStatusStrings_MapsToExpectedEntryStatus()
    {
        // Arrange
        await using (var lease = await _factory.OpenConnectionAsync())
        {
            var statuses = new[] { "EDITED", "MANUALLY_EDITED", "ManuallyEdited", "PENDING", "TRANSLATED", "FAILED_VALIDATION" };
            for (var i = 0; i < statuses.Length; i++)
            {
                await using var cmd = lease.Connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO subtitle_entries (entry_key, source_file, raw_text, masked_text, placeholders_json, status, has_overflow_warning)
                    VALUES (@key, 'test.json', 'raw', 'raw', '{}', @status, 0);
                    """;
                cmd.Parameters.AddWithValue("@key", $"status-{i}");
                cmd.Parameters.AddWithValue("@status", statuses[i]);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // Act
        var entries = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(6, entries.Count);
        Assert.Equal(EntryStatus.ManuallyEdited, entries[0].Status);
        Assert.Equal(EntryStatus.ManuallyEdited, entries[1].Status);
        Assert.Equal(EntryStatus.ManuallyEdited, entries[2].Status);
        Assert.Equal(EntryStatus.Pending, entries[3].Status);
        Assert.Equal(EntryStatus.Translated, entries[4].Status);
        Assert.Equal(EntryStatus.FailedValidation, entries[5].Status);
    }

    [Fact]
    public async Task GetAllAsync_UnknownStatusValue_ThrowsInvalidOperationException()
    {
        // Arrange
        await using (var lease = await _factory.OpenConnectionAsync())
        await using (var cmd = lease.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subtitle_entries (entry_key, source_file, raw_text, masked_text, placeholders_json, status, has_overflow_warning)
                VALUES ('bad-status', 'test.json', 'raw', 'raw', '{}', 'CORRUPTED_STATE', 0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.GetAllAsync());
    }

    [Fact]
    public async Task QueryAsync_StatusOverflowAndEscapedWildcardSearch_ReturnsFilteredEntries()
    {
        // Arrange
        var entries = new[]
        {
            CreateSampleEntry("q-1", "100% power", "100% power", new()) with { Status = EntryStatus.Translated, FinalPatchedText = "100% daya sangat panjang", HasOverflowWarning = true },
            CreateSampleEntry("q-2", "Normal text", "Normal text", new()) with { Status = EntryStatus.Translated, FinalPatchedText = "Teks normal", HasOverflowWarning = false },
            CreateSampleEntry("q-3", "100% fail", "100% fail", new()) with { Status = EntryStatus.FailedValidation, HasOverflowWarning = true }
        };
        await _repository.BulkUpsertAsync(entries);

        // Act
        var filtered = await _repository.QueryAsync(
            status: EntryStatus.Translated,
            hasOverflowWarning: true,
            searchText: "100%");

        // Assert
        Assert.Single(filtered);
        Assert.Equal("q-1", filtered[0].EntryKey);
    }

    [Fact]
    public async Task BulkUpdateAsync_UpdatedTranslationsAndOverflowFlags_PersistsInSingleTransaction()
    {
        // Arrange
        var inserted = await _repository.BulkUpsertAsync(
        [
            CreateSampleEntry("bu-1", "Hi", "Hi", new()),
            CreateSampleEntry("bu-2", "Bye", "Bye", new())
        ]);

        var updatedBatch = new[]
        {
            inserted[0] with { TranslatedText = "Halo", FinalPatchedText = "Halo", Status = EntryStatus.Translated, HasOverflowWarning = true },
            inserted[1] with { Id = 0, TranslatedText = "Sampai jumpa", FinalPatchedText = "Sampai jumpa", Status = EntryStatus.ManuallyEdited, HasOverflowWarning = true }
        };

        // Act
        var affected = await _repository.BulkUpdateAsync(updatedBatch);
        var all = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(2, affected);
        Assert.Equal(EntryStatus.Translated, all[0].Status);
        Assert.Equal("Halo", all[0].FinalPatchedText);
        Assert.True(all[0].HasOverflowWarning);
        Assert.Equal(EntryStatus.ManuallyEdited, all[1].Status);
        Assert.Equal("Sampai jumpa", all[1].FinalPatchedText);
    }

    private static SubtitleEntry CreateSampleEntry(
        string key,
        string rawText,
        string maskedText,
        Dictionary<string, string> placeholders)
    {
        return new SubtitleEntry(
            Id: 0,
            EntryKey: key,
            SourceFile: "data/Map001.json",
            AssetPath: null,
            RawText: rawText,
            MaskedText: maskedText,
            Placeholders: placeholders,
            TranslatedText: null,
            FinalPatchedText: null,
            Status: EntryStatus.Pending,
            HasOverflowWarning: false);
    }
}
