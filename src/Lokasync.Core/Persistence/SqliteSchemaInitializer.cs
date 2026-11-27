namespace Lokasync.Core.Persistence;

internal static class SqliteSchemaInitializer
{
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS projects (
            id TEXT PRIMARY KEY,
            game_title TEXT NOT NULL,
            game_root_path TEXT NOT NULL,
            detected_engine TEXT NOT NULL,
            narrative_context_json TEXT,
            created_at DATETIME DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS subtitle_entries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            entry_key TEXT NOT NULL,
            source_file TEXT NOT NULL,
            asset_path TEXT,
            raw_text TEXT NOT NULL,
            masked_text TEXT NOT NULL,
            placeholders_json TEXT NOT NULL,
            translated_text TEXT,
            final_patched_text TEXT,
            status TEXT NOT NULL DEFAULT 'PENDING',
            has_overflow_warning INTEGER DEFAULT 0
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_subtitle_entries_entry_key
            ON subtitle_entries(entry_key);
        CREATE INDEX IF NOT EXISTS idx_subtitle_entries_status
            ON subtitle_entries(status);
        CREATE INDEX IF NOT EXISTS idx_subtitle_entries_overflow
            ON subtitle_entries(has_overflow_warning);
        CREATE INDEX IF NOT EXISTS idx_subtitle_entries_source_file
            ON subtitle_entries(source_file);

        CREATE TABLE IF NOT EXISTS backup_manifests (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id TEXT NOT NULL,
            original_relative_path TEXT NOT NULL,
            backup_file_path TEXT NOT NULL,
            sha256_hash TEXT NOT NULL,
            backed_up_at DATETIME DEFAULT CURRENT_TIMESTAMP,
            FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_backup_manifests_project_path
            ON backup_manifests(project_id, original_relative_path);
        CREATE INDEX IF NOT EXISTS idx_backup_manifests_project_id
            ON backup_manifests(project_id);
        """;
}
