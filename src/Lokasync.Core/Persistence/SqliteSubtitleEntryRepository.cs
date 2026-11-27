using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Models;

namespace Lokasync.Core.Persistence;

public sealed class SqliteSubtitleEntryRepository(ISqliteConnectionFactory connectionFactory) : ISubtitleEntryRepository
{
    public async Task<IReadOnlyList<SubtitleEntry>> BulkUpsertAsync(IEnumerable<SubtitleEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var entryList = entries as IList<SubtitleEntry> ?? entries.ToList();
        if (entryList.Count == 0)
        {
            return Array.Empty<SubtitleEntry>();
        }

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await lease.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await using var cmd = lease.Connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO subtitle_entries (
                    entry_key,
                    source_file,
                    asset_path,
                    raw_text,
                    masked_text,
                    placeholders_json,
                    translated_text,
                    final_patched_text,
                    status,
                    has_overflow_warning
                )
                VALUES (
                    @entry_key,
                    @source_file,
                    @asset_path,
                    @raw_text,
                    @masked_text,
                    @placeholders_json,
                    @translated_text,
                    @final_patched_text,
                    @status,
                    @has_overflow_warning
                )
                ON CONFLICT(entry_key) DO UPDATE SET
                    source_file = excluded.source_file,
                    asset_path = excluded.asset_path,
                    raw_text = excluded.raw_text,
                    masked_text = excluded.masked_text,
                    placeholders_json = excluded.placeholders_json,
                    translated_text = excluded.translated_text,
                    final_patched_text = excluded.final_patched_text,
                    status = excluded.status,
                    has_overflow_warning = excluded.has_overflow_warning
                RETURNING id;
                """;

            var pEntryKey = cmd.Parameters.Add("@entry_key", SqliteType.Text);
            var pSourceFile = cmd.Parameters.Add("@source_file", SqliteType.Text);
            var pAssetPath = cmd.Parameters.Add("@asset_path", SqliteType.Text);
            var pRawText = cmd.Parameters.Add("@raw_text", SqliteType.Text);
            var pMaskedText = cmd.Parameters.Add("@masked_text", SqliteType.Text);
            var pPlaceholdersJson = cmd.Parameters.Add("@placeholders_json", SqliteType.Text);
            var pTranslatedText = cmd.Parameters.Add("@translated_text", SqliteType.Text);
            var pFinalPatchedText = cmd.Parameters.Add("@final_patched_text", SqliteType.Text);
            var pStatus = cmd.Parameters.Add("@status", SqliteType.Text);
            var pHasOverflowWarning = cmd.Parameters.Add("@has_overflow_warning", SqliteType.Integer);

            var persisted = new List<SubtitleEntry>(entryList.Count);
            foreach (var entry in entryList)
            {
                ArgumentNullException.ThrowIfNull(entry);

                var placeholders = entry.Placeholders ?? new Dictionary<string, string>();
                pEntryKey.Value = (object?)entry.EntryKey ?? DBNull.Value;
                pSourceFile.Value = (object?)entry.SourceFile ?? DBNull.Value;
                pAssetPath.Value = (object?)entry.AssetPath ?? DBNull.Value;
                pRawText.Value = (object?)entry.RawText ?? DBNull.Value;
                pMaskedText.Value = (object?)entry.MaskedText ?? DBNull.Value;
                pPlaceholdersJson.Value = JsonSerializer.Serialize(placeholders);
                pTranslatedText.Value = (object?)entry.TranslatedText ?? DBNull.Value;
                pFinalPatchedText.Value = (object?)entry.FinalPatchedText ?? DBNull.Value;
                pStatus.Value = EntryStatusMapper.ToDbString(entry.Status);
                pHasOverflowWarning.Value = entry.HasOverflowWarning ? 1 : 0;

                var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var assignedId = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
                persisted.Add(entry with { Id = assignedId, Placeholders = placeholders });
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return persisted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> BulkUpdateAsync(IEnumerable<SubtitleEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var entryList = entries as IList<SubtitleEntry> ?? entries.ToList();
        if (entryList.Count == 0)
        {
            return 0;
        }

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await lease.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        SqliteCommand? updateByIdCmd = null;
        SqliteCommand? updateByKeyCmd = null;

        try
        {
            var totalAffected = 0;

            foreach (var entry in entryList)
            {
                ArgumentNullException.ThrowIfNull(entry);

                var placeholdersJson = JsonSerializer.Serialize(entry.Placeholders ?? new Dictionary<string, string>());
                var statusStr = EntryStatusMapper.ToDbString(entry.Status);
                var overflowInt = entry.HasOverflowWarning ? 1 : 0;

                if (entry.Id > 0)
                {
                    if (updateByIdCmd is null)
                    {
                        updateByIdCmd = CreateBulkUpdateCommand(lease.Connection, transaction, byId: true);
                    }

                    updateByIdCmd.Parameters["@id"].Value = entry.Id;
                    updateByIdCmd.Parameters["@masked_text"].Value = entry.MaskedText;
                    updateByIdCmd.Parameters["@placeholders_json"].Value = placeholdersJson;
                    updateByIdCmd.Parameters["@translated_text"].Value = (object?)entry.TranslatedText ?? DBNull.Value;
                    updateByIdCmd.Parameters["@final_patched_text"].Value = (object?)entry.FinalPatchedText ?? DBNull.Value;
                    updateByIdCmd.Parameters["@status"].Value = statusStr;
                    updateByIdCmd.Parameters["@has_overflow_warning"].Value = overflowInt;

                    totalAffected += await updateByIdCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    if (updateByKeyCmd is null)
                    {
                        updateByKeyCmd = CreateBulkUpdateCommand(lease.Connection, transaction, byId: false);
                    }

                    updateByKeyCmd.Parameters["@entry_key"].Value = entry.EntryKey;
                    updateByKeyCmd.Parameters["@masked_text"].Value = entry.MaskedText;
                    updateByKeyCmd.Parameters["@placeholders_json"].Value = placeholdersJson;
                    updateByKeyCmd.Parameters["@translated_text"].Value = (object?)entry.TranslatedText ?? DBNull.Value;
                    updateByKeyCmd.Parameters["@final_patched_text"].Value = (object?)entry.FinalPatchedText ?? DBNull.Value;
                    updateByKeyCmd.Parameters["@status"].Value = statusStr;
                    updateByKeyCmd.Parameters["@has_overflow_warning"].Value = overflowInt;

                    totalAffected += await updateByKeyCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return totalAffected;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (updateByIdCmd is not null)
            {
                await updateByIdCmd.DisposeAsync().ConfigureAwait(false);
            }

            if (updateByKeyCmd is not null)
            {
                await updateByKeyCmd.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> UpdateTranslationAsync(
        long id,
        string? translatedText,
        string? finalPatchedText,
        EntryStatus status,
        bool hasOverflowWarning,
        CancellationToken ct = default)
    {
        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE subtitle_entries
            SET translated_text = @translated_text,
                final_patched_text = @final_patched_text,
                status = @status,
                has_overflow_warning = @has_overflow_warning
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@translated_text", (object?)translatedText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@final_patched_text", (object?)finalPatchedText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", EntryStatusMapper.ToDbString(status));
        cmd.Parameters.AddWithValue("@has_overflow_warning", hasOverflowWarning ? 1 : 0);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> UpdateEntryAsync(SubtitleEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = entry.Id > 0
            ? """
              UPDATE subtitle_entries
              SET entry_key = @entry_key,
                  source_file = @source_file,
                  asset_path = @asset_path,
                  raw_text = @raw_text,
                  masked_text = @masked_text,
                  placeholders_json = @placeholders_json,
                  translated_text = @translated_text,
                  final_patched_text = @final_patched_text,
                  status = @status,
                  has_overflow_warning = @has_overflow_warning
              WHERE id = @id;
              """
            : """
              UPDATE subtitle_entries
              SET source_file = @source_file,
                  asset_path = @asset_path,
                  raw_text = @raw_text,
                  masked_text = @masked_text,
                  placeholders_json = @placeholders_json,
                  translated_text = @translated_text,
                  final_patched_text = @final_patched_text,
                  status = @status,
                  has_overflow_warning = @has_overflow_warning
              WHERE entry_key = @entry_key;
              """;

        if (entry.Id > 0)
        {
            cmd.Parameters.AddWithValue("@id", entry.Id);
        }

        cmd.Parameters.AddWithValue("@entry_key", entry.EntryKey);
        cmd.Parameters.AddWithValue("@source_file", entry.SourceFile);
        cmd.Parameters.AddWithValue("@asset_path", (object?)entry.AssetPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@raw_text", entry.RawText);
        cmd.Parameters.AddWithValue("@masked_text", entry.MaskedText);
        cmd.Parameters.AddWithValue("@placeholders_json", JsonSerializer.Serialize(entry.Placeholders ?? new Dictionary<string, string>()));
        cmd.Parameters.AddWithValue("@translated_text", (object?)entry.TranslatedText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@final_patched_text", (object?)entry.FinalPatchedText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", EntryStatusMapper.ToDbString(entry.Status));
        cmd.Parameters.AddWithValue("@has_overflow_warning", entry.HasOverflowWarning ? 1 : 0);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<SubtitleEntry?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_key, source_file, asset_path, raw_text, masked_text, placeholders_json,
                   translated_text, final_patched_text, status, has_overflow_warning
            FROM subtitle_entries
            WHERE id = @id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return MapEntry(reader);
    }

    public async Task<SubtitleEntry?> GetByEntryKeyAsync(string entryKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryKey);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_key, source_file, asset_path, raw_text, masked_text, placeholders_json,
                   translated_text, final_patched_text, status, has_overflow_warning
            FROM subtitle_entries
            WHERE entry_key = @entry_key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@entry_key", entryKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return MapEntry(reader);
    }

    public Task<IReadOnlyList<SubtitleEntry>> GetAllAsync(CancellationToken ct = default)
        => QueryAsync(status: null, hasOverflowWarning: null, searchText: null, ct: ct);

    public Task<IReadOnlyList<SubtitleEntry>> GetPendingAsync(CancellationToken ct = default)
        => QueryAsync(status: EntryStatus.Pending, hasOverflowWarning: null, searchText: null, ct: ct);

    public async Task<IReadOnlyList<SubtitleEntry>> QueryAsync(
        EntryStatus? status = null,
        bool? hasOverflowWarning = null,
        string? searchText = null,
        CancellationToken ct = default)
    {
        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();

        var sql = new StringBuilder("""
            SELECT id, entry_key, source_file, asset_path, raw_text, masked_text, placeholders_json,
                   translated_text, final_patched_text, status, has_overflow_warning
            FROM subtitle_entries
            """);

        var clauses = new List<string>(3);
        if (status.HasValue)
        {
            clauses.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", EntryStatusMapper.ToDbString(status.Value));
        }

        if (hasOverflowWarning.HasValue)
        {
            clauses.Add("has_overflow_warning = @overflow");
            cmd.Parameters.AddWithValue("@overflow", hasOverflowWarning.Value ? 1 : 0);
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            clauses.Add("""
                (entry_key LIKE @search ESCAPE '\'
                 OR source_file LIKE @search ESCAPE '\'
                 OR raw_text LIKE @search ESCAPE '\'
                 OR COALESCE(translated_text, '') LIKE @search ESCAPE '\'
                 OR COALESCE(final_patched_text, '') LIKE @search ESCAPE '\')
                """);
            cmd.Parameters.AddWithValue("@search", $"%{EscapeLikePattern(searchText)}%");
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", clauses));
        }

        sql.Append(" ORDER BY id ASC;");
        cmd.CommandText = sql.ToString();

        var results = new List<SubtitleEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapEntry(reader));
        }

        return results;
    }

    public async Task<int> CountAsync(EntryStatus? status = null, CancellationToken ct = default)
    {
        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();

        if (status.HasValue)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM subtitle_entries WHERE status = @status;";
            cmd.Parameters.AddWithValue("@status", EntryStatusMapper.ToDbString(status.Value));
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM subtitle_entries;";
        }

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM subtitle_entries;";
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static SqliteCommand CreateBulkUpdateCommand(SqliteConnection connection, SqliteTransaction transaction, bool byId)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = byId
            ? """
              UPDATE subtitle_entries
              SET masked_text = @masked_text,
                  placeholders_json = @placeholders_json,
                  translated_text = @translated_text,
                  final_patched_text = @final_patched_text,
                  status = @status,
                  has_overflow_warning = @has_overflow_warning
              WHERE id = @id;
              """
            : """
              UPDATE subtitle_entries
              SET masked_text = @masked_text,
                  placeholders_json = @placeholders_json,
                  translated_text = @translated_text,
                  final_patched_text = @final_patched_text,
                  status = @status,
                  has_overflow_warning = @has_overflow_warning
              WHERE entry_key = @entry_key;
              """;

        if (byId)
        {
            cmd.Parameters.Add("@id", SqliteType.Integer);
        }
        else
        {
            cmd.Parameters.Add("@entry_key", SqliteType.Text);
        }

        cmd.Parameters.Add("@masked_text", SqliteType.Text);
        cmd.Parameters.Add("@placeholders_json", SqliteType.Text);
        cmd.Parameters.Add("@translated_text", SqliteType.Text);
        cmd.Parameters.Add("@final_patched_text", SqliteType.Text);
        cmd.Parameters.Add("@status", SqliteType.Text);
        cmd.Parameters.Add("@has_overflow_warning", SqliteType.Integer);
        return cmd;
    }

    private static SubtitleEntry MapEntry(SqliteDataReader reader)
    {
        var assetPath = reader.IsDBNull(3) ? null : reader.GetString(3);
        var placeholdersJson = reader.GetString(6);
        var placeholders = JsonSerializer.Deserialize<Dictionary<string, string>>(placeholdersJson)
            ?? new Dictionary<string, string>();
        var translatedText = reader.IsDBNull(7) ? null : reader.GetString(7);
        var finalPatchedText = reader.IsDBNull(8) ? null : reader.GetString(8);
        var status = EntryStatusMapper.FromDbString(reader.GetString(9));
        var hasOverflowWarning = reader.GetInt64(10) != 0;

        return new SubtitleEntry(
            Id: reader.GetInt64(0),
            EntryKey: reader.GetString(1),
            SourceFile: reader.GetString(2),
            AssetPath: assetPath,
            RawText: reader.GetString(4),
            MaskedText: reader.GetString(5),
            Placeholders: placeholders,
            TranslatedText: translatedText,
            FinalPatchedText: finalPatchedText,
            Status: status,
            HasOverflowWarning: hasOverflowWarning);
    }

    private static string EscapeLikePattern(string input)
    {
        return input
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }
}
