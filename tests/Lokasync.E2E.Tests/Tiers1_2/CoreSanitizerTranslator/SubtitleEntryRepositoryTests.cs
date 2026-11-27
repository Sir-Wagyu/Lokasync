using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.CoreSanitizerTranslator;

public sealed class SubtitleEntryRepositoryTests
{
    private static SubtitleEntry MakeEntry(
        long id,
        string key,
        string raw,
        EntryStatus status = EntryStatus.Pending,
        Dictionary<string, string>? placeholders = null,
        string? translated = null,
        string? finalPatched = null,
        bool overflow = false,
        string? assetPath = "Map001") =>
        new(
            id,
            key,
            "data/Map001.json",
            assetPath,
            raw,
            raw,
            placeholders ?? new Dictionary<string, string>(),
            translated,
            finalPatched,
            status,
            overflow);

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F3")]
    public async Task InsertBatchAsync_ThousandEntries_PersistsAllRowsInSingleTransaction()
    {
        // Arrange
        using TempWorkspace ws = new("f3_1000");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        List<SubtitleEntry> batch = Enumerable.Range(1, 1000)
            .Select(i => MakeEntry(
                0,
                $"key_{i:D4}",
                $"Line {i} <b>Hero</b>",
                EntryStatus.Pending,
                new Dictionary<string, string> { ["[TAG_0]"] = "<b>", ["[TAG_1]"] = "</b>" }))
            .ToList();

        // Act
        await db.InsertSubtitleEntriesAsync(batch);
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(1000, all.Count);
        Assert.All(all, e => Assert.True(e.Id > 0));
        Assert.Equal("key_0001", all[0].EntryKey);
        Assert.Equal("key_1000", all[^1].EntryKey);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F3")]
    public async Task GetPendingEntriesAsync_MixedStatusRows_ReturnsOnlyPendingRowsForResume()
    {
        // Arrange
        using TempWorkspace ws = new("f3_pending");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        List<SubtitleEntry> entries =
        [
            MakeEntry(0, "p1", "Pending one", EntryStatus.Pending),
            MakeEntry(0, "t1", "Translated one", EntryStatus.Translated, translated: "Terjemahan", finalPatched: "Terjemahan"),
            MakeEntry(0, "f1", "Failed one", EntryStatus.FailedValidation),
            MakeEntry(0, "m1", "Manual one", EntryStatus.ManuallyEdited, translated: "Manual", finalPatched: "Manual"),
            MakeEntry(0, "p2", "Pending two", EntryStatus.Pending)
        ];
        await db.InsertSubtitleEntriesAsync(entries);

        // Act
        IReadOnlyList<SubtitleEntry> pending = await db.GetPendingEntriesAsync();

        // Assert
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, e => e.EntryKey == "p1");
        Assert.Contains(pending, e => e.EntryKey == "p2");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F3")]
    public async Task UpdateBatchAsync_TranslatedFinalTextAndOverflowFlag_PersistsCheckpointChanges()
    {
        // Arrange
        using TempWorkspace ws = new("f3_update_batch");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync([MakeEntry(0, "k1", "Go"), MakeEntry(0, "k2", "Wait")]);
        IReadOnlyList<SubtitleEntry> inserted = await db.GetAllEntriesAsync();

        List<SubtitleEntry> updated = inserted.Select(e => e with
        {
            TranslatedText = "Berangkatlah sekarang juga",
            FinalPatchedText = "Berangkatlah sekarang juga",
            Status = EntryStatus.Translated,
            HasOverflowWarning = true
        }).ToList();

        // Act
        await CoreTranslatorTestHelpers.UpdateSubtitleEntriesBatchAsync(db, updated);
        IReadOnlyList<SubtitleEntry> reloaded = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(2, reloaded.Count);
        Assert.All(reloaded, r =>
        {
            Assert.Equal(EntryStatus.Translated, r.Status);
            Assert.Equal("Berangkatlah sekarang juga", r.TranslatedText);
            Assert.Equal("Berangkatlah sekarang juga", r.FinalPatchedText);
            Assert.True(r.HasOverflowWarning);
        });
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F3")]
    public async Task QueryEntriesAsync_FilterByStatusAndSearchText_ReturnsMatchingSubset()
    {
        // Arrange
        using TempWorkspace ws = new("f3_query_filter");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync([
            MakeEntry(0, "k_warden", "The Warden approaches.", EntryStatus.FailedValidation),
            MakeEntry(0, "k_hello", "Hello world.", EntryStatus.Translated, translated: "Halo dunia.", finalPatched: "Halo dunia."),
            MakeEntry(0, "k_warden_ok", "The Warden speaks.", EntryStatus.Translated, translated: "Sang Sipir bicara.", finalPatched: "Sang Sipir bicara.")
        ]);

        // Act
        IReadOnlyList<SubtitleEntry> failedOnly = await CoreTranslatorTestHelpers.QuerySubtitleEntriesAsync(db, EntryStatus.FailedValidation, null);
        IReadOnlyList<SubtitleEntry> wardenSearch = await CoreTranslatorTestHelpers.QuerySubtitleEntriesAsync(db, null, "Warden");

        // Assert
        Assert.Single(failedOnly);
        Assert.Equal("k_warden", failedOnly[0].EntryKey);
        Assert.Equal(2, wardenSearch.Count);
        Assert.All(wardenSearch, e => Assert.Contains("Warden", e.RawText, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F3")]
    public async Task GetEntriesAsync_AllFourEntryStatusValues_RoundTripDeterministically()
    {
        // Arrange
        using TempWorkspace ws = new("f3_statuses");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        List<SubtitleEntry> entries =
        [
            MakeEntry(0, "s_pending", "A", EntryStatus.Pending),
            MakeEntry(0, "s_translated", "B", EntryStatus.Translated),
            MakeEntry(0, "s_failed", "C", EntryStatus.FailedValidation),
            MakeEntry(0, "s_manual", "D", EntryStatus.ManuallyEdited)
        ];

        // Act
        await db.InsertSubtitleEntriesAsync(entries);
        IReadOnlyList<SubtitleEntry> loaded = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(4, loaded.Count);
        Assert.Equal(EntryStatus.Pending, loaded.Single(e => e.EntryKey == "s_pending").Status);
        Assert.Equal(EntryStatus.Translated, loaded.Single(e => e.EntryKey == "s_translated").Status);
        Assert.Equal(EntryStatus.FailedValidation, loaded.Single(e => e.EntryKey == "s_failed").Status);
        Assert.Equal(EntryStatus.ManuallyEdited, loaded.Single(e => e.EntryKey == "s_manual").Status);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F3")]
    public async Task GetEntriesAsync_ManuallyEditedAliasesInSqlite_MapsEditedVariantsToManuallyEdited()
    {
        // Arrange
        using TempWorkspace ws = new("f3_edited_aliases");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        string[] variants = ["EDITED", "MANUALLY_EDITED", "ManuallyEdited"];

        for (int i = 0; i < variants.Length; i++)
        {
            await using SqliteCommand cmd = db.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO subtitle_entries (entry_key, source_file, raw_text, masked_text, placeholders_json, status)
                VALUES (@k, 'dialog.srt', 'Raw', 'Raw', '{}', @st);
                """;
            cmd.Parameters.AddWithValue("@k", $"alias_{i}");
            cmd.Parameters.AddWithValue("@st", variants[i]);
            await cmd.ExecuteNonQueryAsync();
        }

        // Act
        IReadOnlyList<SubtitleEntry> loaded = await db.GetAllEntriesAsync();

        // Assert
        Assert.Equal(3, loaded.Count);
        Assert.All(loaded, e => Assert.Equal(EntryStatus.ManuallyEdited, e.Status));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F3")]
    public async Task InsertBatchAsync_EmptyCollection_CompletesWithoutErrorAndLeavesTableEmpty()
    {
        // Arrange
        using TempWorkspace ws = new("f3_empty");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);

        // Act
        await db.InsertSubtitleEntriesAsync([]);
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Empty(all);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F3")]
    public async Task InsertBatchAsync_ConstraintViolationMidBatch_RollsBackEntireTransactionAtomically()
    {
        // Arrange
        using TempWorkspace ws = new("f3_rollback");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        List<SubtitleEntry> invalidBatch =
        [
            MakeEntry(0, "ok_1", "Valid 1"),
            MakeEntry(0, "ok_2", "Valid 2"),
            new SubtitleEntry(0, "bad_3", "Map001.json", null, null!, "masked", new(), null, null, EntryStatus.Pending, false),
            MakeEntry(0, "ok_4", "Valid 4"),
            MakeEntry(0, "ok_5", "Valid 5")
        ];

        // Act
        Exception? ex = await Record.ExceptionAsync(() => db.InsertSubtitleEntriesAsync(invalidBatch));
        IReadOnlyList<SubtitleEntry> rowsAfter = await db.GetAllEntriesAsync();

        // Assert
        Assert.NotNull(ex);
        Assert.True(ex is SqliteException or ArgumentNullException, $"Expected SqliteException or ArgumentNullException, got {ex!.GetType().FullName}");
        Assert.Empty(rowsAfter);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F3")]
    public async Task InsertBatchAsync_QuotesNewlinesAndControlCharsInDialog_PreservesExactText()
    {
        // Arrange
        using TempWorkspace ws = new("f3_quotes_ctrl");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        string trickyText = "He said, 'Run!' & \"Hide\"\r\nSecond\n\tTabbed; DROP TABLE subtitle_entries; --";
        SubtitleEntry entry = new(
            0, "k_tricky", "sub/file.srt", null, trickyText, trickyText, new(), null, null, EntryStatus.Pending, false);

        // Act
        await db.InsertSubtitleEntriesAsync([entry]);
        IReadOnlyList<SubtitleEntry> loaded = await db.GetAllEntriesAsync();

        // Assert
        Assert.Single(loaded);
        Assert.Equal(trickyText, loaded[0].RawText);
        Assert.Null(loaded[0].AssetPath);
        Assert.Null(loaded[0].TranslatedText);
        Assert.Null(loaded[0].FinalPatchedText);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F3")]
    public async Task InsertBatchAsync_EmptyAndMultiEntryPlaceholdersDictionary_RoundTripsJson()
    {
        // Arrange
        using TempWorkspace ws = new("f3_placeholders");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        SubtitleEntry entryA = MakeEntry(0, "ph_empty", "Plain", placeholders: new Dictionary<string, string>());
        SubtitleEntry entryB = MakeEntry(0, "ph_multi", "[TAG_0][VAR_0]", placeholders: new Dictionary<string, string>
        {
            ["[TAG_0]"] = "<color=#FFF>",
            ["[VAR_0]"] = "{player}"
        });

        // Act
        await db.InsertSubtitleEntriesAsync([entryA, entryB]);
        IReadOnlyList<SubtitleEntry> loaded = await db.GetAllEntriesAsync();

        // Assert
        SubtitleEntry loadedA = loaded.Single(e => e.EntryKey == "ph_empty");
        SubtitleEntry loadedB = loaded.Single(e => e.EntryKey == "ph_multi");
        Assert.Empty(loadedA.Placeholders);
        Assert.Equal(2, loadedB.Placeholders.Count);
        Assert.Equal("<color=#FFF>", loadedB.Placeholders["[TAG_0]"]);
        Assert.Equal("{player}", loadedB.Placeholders["[VAR_0]"]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F3")]
    public async Task UpdateBatchAsync_NonExistentEntryId_LeavesExistingRowsUntouched()
    {
        // Arrange
        using TempWorkspace ws = new("f3_nonexistent_upd");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await db.InsertSubtitleEntriesAsync([MakeEntry(0, "existing_1", "Original Line", EntryStatus.Pending)]);
        SubtitleEntry phantom = MakeEntry(99999, "phantom", "Ghost", EntryStatus.Translated, translated: "Hantu", finalPatched: "Hantu");

        // Act
        await CoreTranslatorTestHelpers.UpdateSubtitleEntriesBatchAsync(db, [phantom]);
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();

        // Assert
        Assert.Single(all);
        Assert.Equal("existing_1", all[0].EntryKey);
        Assert.Equal("Original Line", all[0].RawText);
        Assert.Equal(EntryStatus.Pending, all[0].Status);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F3")]
    public async Task InsertBatchAsync_CancelledToken_ThrowsOperationCanceledExceptionAndCommitsNothing()
    {
        // Arrange
        using TempWorkspace ws = new("f3_cancel");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        List<SubtitleEntry> batch = Enumerable.Range(1, 50).Select(i => MakeEntry(0, $"c_{i}", $"Text {i}")).ToList();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.InsertSubtitleEntriesAsync(batch, cts.Token));
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync();
        Assert.Empty(all);
    }
}
