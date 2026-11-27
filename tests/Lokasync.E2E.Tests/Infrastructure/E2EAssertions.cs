using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Models;
using Xunit;

namespace Lokasync.E2E.Tests.Infrastructure;

public static class E2EAssertions
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredSchemaColumns =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["projects"] = ["id", "game_title", "game_root_path", "detected_engine", "narrative_context_json", "created_at"],
            ["subtitle_entries"] =
            [
                "id", "entry_key", "source_file", "asset_path", "raw_text", "masked_text",
                "placeholders_json", "translated_text", "final_patched_text", "status", "has_overflow_warning"
            ],
            ["backup_manifests"] =
            [
                "id", "project_id", "original_relative_path", "backup_file_path", "sha256_hash", "backed_up_at"
            ]
        };

    public static void AssertSqliteSchemaValid(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        HashSet<string> existingTables = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingTables.Add(reader.GetString(0));
            }
        }

        foreach ((string table, string[] requiredColumns) in RequiredSchemaColumns)
        {
            Assert.True(
                existingTables.Contains(table),
                $"Required SQLite table '{table}' is missing. Found tables: [{string.Join(", ", existingTables)}].");

            HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
            using SqliteCommand colCmd = connection.CreateCommand();
            colCmd.CommandText = $"PRAGMA table_info({table});";
            using SqliteDataReader colReader = colCmd.ExecuteReader();
            while (colReader.Read())
            {
                columns.Add(colReader.GetString(1));
            }

            foreach (string requiredColumn in requiredColumns)
            {
                Assert.True(
                    columns.Contains(requiredColumn),
                    $"Required column '{requiredColumn}' missing on SQLite table '{table}'. Found: [{string.Join(", ", columns)}].");
            }
        }
    }

    public static void AssertFilesMatchHashes(
        string gameRootPath,
        IReadOnlyDictionary<string, string> expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        ArgumentNullException.ThrowIfNull(expectedSha256);

        string normalizedRoot = Path.GetFullPath(gameRootPath);
        foreach ((string relativePath, string expectedHash) in expectedSha256)
        {
            string fullPath = Path.GetFullPath(Path.Combine(
                normalizedRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));

            Assert.True(File.Exists(fullPath), $"Expected file '{relativePath}' does not exist at '{fullPath}'.");
            string actualHash = TempWorkspace.ComputeSha256(fullPath);
            Assert.Equal(expectedHash.ToLowerInvariant(), actualHash.ToLowerInvariant());
        }
    }

    public static void AssertBackupManifestMatchesDisk(
        string gameRootPath,
        SqliteConnection connection,
        string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        List<(string RelativePath, string BackupFilePath, string Sha256Hash)> rows = [];
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT original_relative_path, backup_file_path, sha256_hash
                FROM backup_manifests
                WHERE project_id = @projectId;
                """;
            cmd.Parameters.AddWithValue("@projectId", projectId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        Assert.NotEmpty(rows);
        string rootWithSep = Path.GetFullPath(gameRootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach ((string relativePath, string backupFilePath, string sha256Hash) in rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(relativePath), "Manifest original_relative_path must not be empty.");
            Assert.False(string.IsNullOrWhiteSpace(backupFilePath), "Manifest backup_file_path must not be empty.");
            Assert.Equal(64, sha256Hash.Length);

            string resolvedOriginal = Path.GetFullPath(Path.Combine(
                gameRootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            Assert.True(
                resolvedOriginal.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase),
                $"Manifest relative path '{relativePath}' escapes game root '{gameRootPath}'.");

            Assert.True(
                File.Exists(backupFilePath),
                $"Backup file '{backupFilePath}' referenced in backup_manifests for '{relativePath}' does not exist.");

            string actualBackupHash = TempWorkspace.ComputeSha256(backupFilePath);
            Assert.Equal(sha256Hash.ToLowerInvariant(), actualBackupHash.ToLowerInvariant());
        }
    }

    public static void AssertOverflowWarningRule(SubtitleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string raw = entry.RawText ?? string.Empty;
        string finalText = entry.FinalPatchedText ?? string.Empty;

        if (raw.Length == 0)
        {
            Assert.False(entry.HasOverflowWarning);
            return;
        }

        bool expectedOverflow = (long)finalText.Length * 4L > (long)raw.Length * 5L;
        Assert.Equal(expectedOverflow, entry.HasOverflowWarning);
    }

    public static void AssertFailedValidationFallback(SubtitleEntry entry, string patchedContent)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(patchedContent);

        Assert.Equal(EntryStatus.FailedValidation, entry.Status);
        Assert.True(
            entry.FinalPatchedText is null || string.Equals(entry.FinalPatchedText, entry.RawText, StringComparison.Ordinal),
            $"FailedValidation entry '{entry.EntryKey}' must fall back to RawText, but FinalPatchedText was '{entry.FinalPatchedText}'.");

        if (!string.IsNullOrEmpty(entry.RawText))
        {
            Assert.Contains(entry.RawText, patchedContent, StringComparison.Ordinal);
        }

        foreach (string placeholderKey in entry.Placeholders.Keys)
        {
            if (!entry.RawText.Contains(placeholderKey, StringComparison.Ordinal))
            {
                Assert.DoesNotContain(placeholderKey, patchedContent, StringComparison.Ordinal);
            }
        }
    }

    public static void AssertSequentialExecution(
        IReadOnlyList<CapturedRequest> requests,
        int maxObservedConcurrency,
        int minDelayMs)
    {
        ArgumentNullException.ThrowIfNull(requests);
        Assert.True(
            maxObservedConcurrency <= 1,
            $"Expected sequential MaxConcurrency <= 1, but observed {maxObservedConcurrency} concurrent in-flight HTTP requests.");

        if (requests.Count > 0)
        {
            Assert.Equal(1, maxObservedConcurrency);
        }

        double effectiveMinDelayMs = minDelayMs <= 0 ? 0.0 : Math.Max(0.0, minDelayMs - 20.0);
        for (int i = 1; i < requests.Count; i++)
        {
            double elapsedMs = (requests[i].TimestampUtc - requests[i - 1].TimestampUtc).TotalMilliseconds;
            Assert.True(
                elapsedMs >= effectiveMinDelayMs,
                $"Batch request #{i + 1} started only {elapsedMs:F1} ms after request #{i}, violating minimum delay {minDelayMs} ms.");
        }
    }
}
