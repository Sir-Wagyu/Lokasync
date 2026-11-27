using System.Globalization;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Models;

namespace Lokasync.Core.Persistence;

public sealed class SqliteBackupManifestRepository(ISqliteConnectionFactory connectionFactory) : IBackupManifestRepository
{
    public async Task<BackupManifestRecord> UpsertAsync(BackupManifestRecord manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var result = await BulkUpsertAsync([manifest], ct).ConfigureAwait(false);
        return result[0];
    }

    public async Task<IReadOnlyList<BackupManifestRecord>> BulkUpsertAsync(
        IEnumerable<BackupManifestRecord> manifests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        var list = manifests as IList<BackupManifestRecord> ?? manifests.ToList();
        if (list.Count == 0)
        {
            return Array.Empty<BackupManifestRecord>();
        }

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await lease.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await using var cmd = lease.Connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO backup_manifests (
                    project_id,
                    original_relative_path,
                    backup_file_path,
                    sha256_hash,
                    backed_up_at
                )
                VALUES (
                    @project_id,
                    @original_relative_path,
                    @backup_file_path,
                    @sha256_hash,
                    @backed_up_at
                )
                ON CONFLICT(project_id, original_relative_path) DO UPDATE SET
                    backup_file_path = excluded.backup_file_path,
                    sha256_hash = excluded.sha256_hash,
                    backed_up_at = excluded.backed_up_at
                RETURNING id;
                """;

            var pProjectId = cmd.Parameters.Add("@project_id", SqliteType.Text);
            var pOriginalPath = cmd.Parameters.Add("@original_relative_path", SqliteType.Text);
            var pBackupPath = cmd.Parameters.Add("@backup_file_path", SqliteType.Text);
            var pSha256 = cmd.Parameters.Add("@sha256_hash", SqliteType.Text);
            var pBackedUpAt = cmd.Parameters.Add("@backed_up_at", SqliteType.Text);

            var persisted = new List<BackupManifestRecord>(list.Count);
            foreach (var manifest in list)
            {
                ArgumentNullException.ThrowIfNull(manifest);
                var backedUpAt = manifest.BackedUpAt == default ? DateTimeOffset.UtcNow : manifest.BackedUpAt;

                pProjectId.Value = (object?)manifest.ProjectId ?? DBNull.Value;
                pOriginalPath.Value = (object?)manifest.OriginalRelativePath ?? DBNull.Value;
                pBackupPath.Value = (object?)manifest.BackupFilePath ?? DBNull.Value;
                pSha256.Value = (object?)manifest.Sha256Hash ?? DBNull.Value;
                pBackedUpAt.Value = backedUpAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

                var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var assignedId = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
                persisted.Add(manifest with { Id = assignedId, BackedUpAt = backedUpAt });
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

    public async Task<IReadOnlyList<BackupManifestRecord>> GetByProjectIdAsync(string projectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_id, original_relative_path, backup_file_path, sha256_hash, backed_up_at
            FROM backup_manifests
            WHERE project_id = @project_id
            ORDER BY original_relative_path ASC;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);

        var results = new List<BackupManifestRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapManifest(reader));
        }

        return results;
    }

    public async Task<BackupManifestRecord?> GetByRelativePathAsync(
        string projectId,
        string originalRelativePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalRelativePath);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_id, original_relative_path, backup_file_path, sha256_hash, backed_up_at
            FROM backup_manifests
            WHERE project_id = @project_id AND original_relative_path = @original_relative_path
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@original_relative_path", originalRelativePath);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return MapManifest(reader);
    }

    public async Task<bool> HasManifestsForProjectAsync(string projectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM backup_manifests WHERE project_id = @project_id);";
        cmd.Parameters.AddWithValue("@project_id", projectId);

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture) != 0;
    }

    public async Task<int> DeleteByProjectIdAsync(string projectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM backup_manifests WHERE project_id = @project_id;";
        cmd.Parameters.AddWithValue("@project_id", projectId);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static BackupManifestRecord MapManifest(SqliteDataReader reader)
    {
        var backedUpAtRaw = reader.IsDBNull(5) ? null : reader.GetString(5);
        var backedUpAt = ParseTimestamp(backedUpAtRaw);

        return new BackupManifestRecord(
            Id: reader.GetInt64(0),
            ProjectId: reader.GetString(1),
            OriginalRelativePath: reader.GetString(2),
            BackupFilePath: reader.GetString(3),
            Sha256Hash: reader.GetString(4),
            BackedUpAt: backedUpAt);
    }

    private static DateTimeOffset ParseTimestamp(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }
}
