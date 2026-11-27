using System.Globalization;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Models;

namespace Lokasync.Core.Persistence;

public sealed class SqliteProjectRepository(ISqliteConnectionFactory connectionFactory) : IProjectRepository
{
    public async Task<ProjectRecord> CreateAsync(ProjectRecord project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Id);

        var createdAt = project.CreatedAt == default ? DateTimeOffset.UtcNow : project.CreatedAt;

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, game_title, game_root_path, detected_engine, narrative_context_json, created_at)
            VALUES (@id, @game_title, @game_root_path, @detected_engine, @narrative_context_json, @created_at);
            """;
        cmd.Parameters.AddWithValue("@id", project.Id);
        cmd.Parameters.AddWithValue("@game_title", project.GameTitle);
        cmd.Parameters.AddWithValue("@game_root_path", project.GameRootPath);
        cmd.Parameters.AddWithValue("@detected_engine", project.DetectedEngine);
        cmd.Parameters.AddWithValue("@narrative_context_json", (object?)project.NarrativeContextJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return project with { CreatedAt = createdAt };
    }

    public async Task UpsertAsync(ProjectRecord project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Id);

        var createdAt = project.CreatedAt == default ? DateTimeOffset.UtcNow : project.CreatedAt;

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, game_title, game_root_path, detected_engine, narrative_context_json, created_at)
            VALUES (@id, @game_title, @game_root_path, @detected_engine, @narrative_context_json, @created_at)
            ON CONFLICT(id) DO UPDATE SET
                game_title = excluded.game_title,
                game_root_path = excluded.game_root_path,
                detected_engine = excluded.detected_engine,
                narrative_context_json = excluded.narrative_context_json;
            """;
        cmd.Parameters.AddWithValue("@id", project.Id);
        cmd.Parameters.AddWithValue("@game_title", project.GameTitle);
        cmd.Parameters.AddWithValue("@game_root_path", project.GameRootPath);
        cmd.Parameters.AddWithValue("@detected_engine", project.DetectedEngine);
        cmd.Parameters.AddWithValue("@narrative_context_json", (object?)project.NarrativeContextJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ProjectRecord?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, game_title, game_root_path, detected_engine, narrative_context_json, created_at
            FROM projects
            WHERE id = @id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return MapProject(reader);
    }

    public async Task<ProjectRecord?> GetByGameRootPathAsync(string gameRootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);

        var normalized = SqliteConnectionFactory.NormalizeGameRootPath(gameRootPath);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, game_title, game_root_path, detected_engine, narrative_context_json, created_at
            FROM projects
            WHERE game_root_path = @raw_path OR game_root_path = @normalized_path
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@raw_path", gameRootPath);
        cmd.Parameters.AddWithValue("@normalized_path", normalized);

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return MapProject(reader);
            }
        }

        await using var scanCmd = lease.Connection.CreateCommand();
        scanCmd.CommandText = """
            SELECT id, game_title, game_root_path, detected_engine, narrative_context_json, created_at
            FROM projects
            ORDER BY created_at DESC, id ASC;
            """;

        await using var scanReader = await scanCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await scanReader.ReadAsync(ct).ConfigureAwait(false))
        {
            var candidate = MapProject(scanReader);
            if (string.Equals(
                    SqliteConnectionFactory.NormalizeGameRootPath(candidate.GameRootPath),
                    normalized,
                    StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<bool> UpdateAsync(ProjectRecord project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Id);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE projects
            SET game_title = @game_title,
                game_root_path = @game_root_path,
                detected_engine = @detected_engine,
                narrative_context_json = @narrative_context_json
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", project.Id);
        cmd.Parameters.AddWithValue("@game_title", project.GameTitle);
        cmd.Parameters.AddWithValue("@game_root_path", project.GameRootPath);
        cmd.Parameters.AddWithValue("@detected_engine", project.DetectedEngine);
        cmd.Parameters.AddWithValue("@narrative_context_json", (object?)project.NarrativeContextJson ?? DBNull.Value);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> UpdateNarrativeContextAsync(string projectId, string? narrativeContextJson, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE projects
            SET narrative_context_json = @narrative_context_json
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.Parameters.AddWithValue("@narrative_context_json", (object?)narrativeContextJson ?? DBNull.Value);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> UpdateDetectedEngineAsync(string projectId, string detectedEngine, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(detectedEngine);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE projects
            SET detected_engine = @detected_engine
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.Parameters.AddWithValue("@detected_engine", detectedEngine);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, game_title, game_root_path, detected_engine, narrative_context_json, created_at
            FROM projects
            ORDER BY created_at DESC, id ASC;
            """;

        var results = new List<ProjectRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapProject(reader));
        }

        return results;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var lease = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM projects WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    private static ProjectRecord MapProject(SqliteDataReader reader)
    {
        var narrativeContextJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        var createdAtRaw = reader.IsDBNull(5) ? null : reader.GetString(5);
        var createdAt = ParseTimestamp(createdAtRaw);

        return new ProjectRecord(
            Id: reader.GetString(0),
            GameTitle: reader.GetString(1),
            GameRootPath: reader.GetString(2),
            DetectedEngine: reader.GetString(3),
            NarrativeContextJson: narrativeContextJson,
            CreatedAt: createdAt);
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
