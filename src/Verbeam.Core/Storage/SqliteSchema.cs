using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

internal static class SqliteSchema
{
    public const int CurrentVersion = 3;

    private static readonly string[] Statements =
    [
        """
        PRAGMA foreign_keys = ON;
        """,
        """
        PRAGMA busy_timeout = 5000;
        """,
        """
        PRAGMA journal_mode = WAL;
        """,
        """
        PRAGMA synchronous = NORMAL;
        """,
        """
        PRAGMA wal_autocheckpoint = 1000;
        """,
        """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            applied_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS translations (
            key TEXT PRIMARY KEY,
            source_text TEXT NOT NULL,
            translated_text TEXT NOT NULL,
            source_language TEXT NOT NULL,
            target_language TEXT NOT NULL,
            mode TEXT NOT NULL,
            provider TEXT NOT NULL,
            engine TEXT NOT NULL,
            model TEXT NOT NULL,
            preset_version TEXT NOT NULL,
            glossary_hash TEXT NOT NULL,
            latency_ms INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_translations_created_at
            ON translations(created_at);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_translations_lookup
            ON translations(source_language, target_language, mode, provider, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS profiles (
            id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            source_language TEXT NOT NULL,
            target_language TEXT NOT NULL,
            default_mode TEXT NOT NULL,
            default_provider TEXT NOT NULL,
            notes TEXT NOT NULL DEFAULT '',
            metadata_json TEXT NOT NULL DEFAULT '{}',
            is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS translation_sessions (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL,
            display_name TEXT NOT NULL DEFAULT '',
            source_language TEXT NOT NULL,
            target_language TEXT NOT NULL,
            mode TEXT NOT NULL,
            provider TEXT NOT NULL,
            started_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            ended_at TEXT,
            metadata_json TEXT NOT NULL DEFAULT '{}',
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_translation_sessions_profile
            ON translation_sessions(profile_id, last_seen_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS translation_events (
            id TEXT PRIMARY KEY,
            session_id TEXT,
            profile_id TEXT NOT NULL,
            translation_key TEXT,
            request_name TEXT NOT NULL DEFAULT '',
            source_text TEXT NOT NULL,
            translated_text TEXT NOT NULL DEFAULT '',
            source_language TEXT NOT NULL,
            target_language TEXT NOT NULL,
            mode TEXT NOT NULL,
            provider TEXT NOT NULL,
            glossary_id TEXT NOT NULL DEFAULT '',
            glossary_hash TEXT NOT NULL DEFAULT '',
            engine TEXT NOT NULL DEFAULT '',
            model TEXT NOT NULL DEFAULT '',
            latency_ms INTEGER NOT NULL DEFAULT 0,
            cache_hit INTEGER NOT NULL DEFAULT 0 CHECK (cache_hit IN (0, 1)),
            error_code TEXT NOT NULL DEFAULT '0',
            error_message TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            FOREIGN KEY (session_id) REFERENCES translation_sessions(id) ON DELETE SET NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE,
            FOREIGN KEY (translation_key) REFERENCES translations(key) ON DELETE SET NULL
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_translation_events_session_recent
            ON translation_events(session_id, created_at);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_translation_events_profile_recent
            ON translation_events(profile_id, created_at);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_translation_events_translation_key
            ON translation_events(translation_key);
        """,
        """
        CREATE TABLE IF NOT EXISTS glossary_sets (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            source_language TEXT NOT NULL,
            target_language TEXT NOT NULL,
            hash TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL DEFAULT 'db',
            metadata_json TEXT NOT NULL DEFAULT '{}',
            is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_glossary_sets_profile
            ON glossary_sets(profile_id, is_active);
        """,
        """
        CREATE TABLE IF NOT EXISTS glossary_terms (
            id TEXT PRIMARY KEY,
            glossary_id TEXT NOT NULL,
            source_term TEXT NOT NULL,
            target_term TEXT NOT NULL,
            note TEXT NOT NULL DEFAULT '',
            priority INTEGER NOT NULL DEFAULT 0,
            is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (glossary_id) REFERENCES glossary_sets(id) ON DELETE CASCADE,
            UNIQUE (glossary_id, source_term)
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_glossary_terms_lookup
            ON glossary_terms(glossary_id, is_active, source_term);
        """,
        """
        CREATE TABLE IF NOT EXISTS memory_items (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL,
            memory_kind TEXT NOT NULL CHECK (
                memory_kind IN ('term', 'translation', 'ocr_correction', 'style', 'scene_summary')
            ),
            source_language TEXT NOT NULL,
            target_language TEXT NOT NULL,
            source_text TEXT NOT NULL,
            source_text_normalized TEXT NOT NULL DEFAULT '',
            target_text TEXT NOT NULL,
            note TEXT NOT NULL DEFAULT '',
            priority INTEGER NOT NULL DEFAULT 0,
            confidence REAL NOT NULL DEFAULT 1.0,
            tags_json TEXT NOT NULL DEFAULT '[]',
            metadata_json TEXT NOT NULL DEFAULT '{}',
            trust_level TEXT NOT NULL DEFAULT 'user_verified' CHECK (
                trust_level IN ('user_verified', 'trusted_import', 'local_generated', 'untrusted_import', 'quarantined')
            ),
            source_uri TEXT NOT NULL DEFAULT '',
            source_hash TEXT NOT NULL DEFAULT '',
            created_by TEXT NOT NULL DEFAULT '',
            approved_by TEXT NOT NULL DEFAULT '',
            security_flags_json TEXT NOT NULL DEFAULT '[]',
            classification TEXT NOT NULL DEFAULT 'normal',
            visibility TEXT NOT NULL DEFAULT 'profile' CHECK (
                visibility IN ('private', 'session', 'profile', 'shared')
            ),
            is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            last_used_at TEXT,
            use_count INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_items_lookup
            ON memory_items(profile_id, memory_kind, source_language, target_language, is_active, priority);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_items_source
            ON memory_items(profile_id, source_text_normalized);
        """,
        """
        CREATE TABLE IF NOT EXISTS memory_embeddings (
            memory_id TEXT NOT NULL,
            embedding_model TEXT NOT NULL,
            dims INTEGER NOT NULL,
            vector BLOB NOT NULL,
            content_hash TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (memory_id, embedding_model),
            FOREIGN KEY (memory_id) REFERENCES memory_items(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_embeddings_hash
            ON memory_embeddings(embedding_model, content_hash);
        """,
        """
        CREATE TABLE IF NOT EXISTS rag_context_audit (
            id TEXT PRIMARY KEY,
            request_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            session_id TEXT NOT NULL DEFAULT '',
            translation_key TEXT,
            memory_id TEXT NOT NULL,
            memory_kind TEXT NOT NULL,
            snippet_hash TEXT NOT NULL,
            context_hash TEXT NOT NULL,
            trust_level TEXT NOT NULL,
            source_hash TEXT NOT NULL DEFAULT '',
            policy_version TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE,
            FOREIGN KEY (translation_key) REFERENCES translations(key) ON DELETE SET NULL,
            FOREIGN KEY (memory_id) REFERENCES memory_items(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_rag_context_audit_profile_recent
            ON rag_context_audit(profile_id, created_at);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_rag_context_audit_request
            ON rag_context_audit(request_id);
        """,
        """
        CREATE TABLE IF NOT EXISTS scene_summaries (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            summary_text TEXT NOT NULL,
            start_event_id TEXT NOT NULL,
            end_event_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (session_id) REFERENCES translation_sessions(id) ON DELETE CASCADE,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE,
            FOREIGN KEY (start_event_id) REFERENCES translation_events(id),
            FOREIGN KEY (end_event_id) REFERENCES translation_events(id)
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_scene_summaries_session
            ON scene_summaries(session_id, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS ocr_events (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL DEFAULT 'default',
            session_id TEXT NOT NULL DEFAULT '',
            image_hash TEXT NOT NULL,
            image_mime_type TEXT NOT NULL,
            language TEXT NOT NULL,
            provider TEXT NOT NULL,
            engine TEXT NOT NULL,
            raw_text TEXT NOT NULL DEFAULT '',
            corrected_text TEXT NOT NULL DEFAULT '',
            blocks_json TEXT NOT NULL DEFAULT '[]',
            corrections_json TEXT NOT NULL DEFAULT '[]',
            latency_ms INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_ocr_events_profile_recent
            ON ocr_events(profile_id, created_at);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_ocr_events_image_hash
            ON ocr_events(image_hash);
        """,
        """
        CREATE TABLE IF NOT EXISTS ocr_corrections (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL DEFAULT 'default',
            language TEXT NOT NULL,
            wrong_text TEXT NOT NULL,
            wrong_text_normalized TEXT NOT NULL,
            corrected_text TEXT NOT NULL,
            note TEXT NOT NULL DEFAULT '',
            priority INTEGER NOT NULL DEFAULT 0,
            confidence REAL NOT NULL DEFAULT 1.0,
            source TEXT NOT NULL DEFAULT 'user',
            is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            last_used_at TEXT,
            use_count INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE,
            UNIQUE (profile_id, language, wrong_text_normalized, corrected_text)
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_ocr_corrections_lookup
            ON ocr_corrections(profile_id, language, is_active, priority);
        """,
        """
        CREATE TABLE IF NOT EXISTS speech_events (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL DEFAULT 'default',
            session_id TEXT NOT NULL DEFAULT '',
            source_kind TEXT NOT NULL DEFAULT '',
            source_uri TEXT NOT NULL DEFAULT '',
            audio_hash TEXT NOT NULL DEFAULT '',
            audio_mime_type TEXT NOT NULL DEFAULT '',
            language TEXT NOT NULL,
            provider TEXT NOT NULL,
            engine TEXT NOT NULL,
            text TEXT NOT NULL DEFAULT '',
            segments_json TEXT NOT NULL DEFAULT '[]',
            captions_used INTEGER NOT NULL DEFAULT 0 CHECK (captions_used IN (0, 1)),
            latency_ms INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_speech_events_profile_recent
            ON speech_events(profile_id, created_at);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_speech_events_audio_hash
            ON speech_events(audio_hash);
        """,
        """
        CREATE TABLE IF NOT EXISTS speech_jobs (
            id TEXT PRIMARY KEY,
            status TEXT NOT NULL,
            profile_id TEXT NOT NULL DEFAULT 'default',
            session_id TEXT NOT NULL DEFAULT '',
            source_kind TEXT NOT NULL DEFAULT '',
            source_uri TEXT NOT NULL DEFAULT '',
            language TEXT NOT NULL,
            provider TEXT NOT NULL,
            engine TEXT NOT NULL DEFAULT '',
            captions_used INTEGER NOT NULL DEFAULT 0 CHECK (captions_used IN (0, 1)),
            segment_count INTEGER NOT NULL DEFAULT 0,
            progress REAL NOT NULL DEFAULT 0,
            result_event_id TEXT NOT NULL DEFAULT '',
            error_code TEXT NOT NULL DEFAULT '',
            error_message TEXT NOT NULL DEFAULT '',
            request_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL,
            started_at TEXT,
            completed_at TEXT,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_speech_jobs_profile_recent
            ON speech_jobs(profile_id, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS speech_job_events (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id TEXT NOT NULL,
            type TEXT NOT NULL,
            payload_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES speech_jobs(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_speech_job_events_job_sequence
            ON speech_job_events(job_id, sequence);
        """,
        """
        INSERT OR IGNORE INTO profiles (
            id,
            display_name,
            source_language,
            target_language,
            default_mode,
            default_provider,
            created_at,
            updated_at
        )
        VALUES (
            'default',
            'Default',
            'ja',
            'zh-TW',
            'game_dialogue',
            'ollama',
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        );
        """,
        """
        INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
        VALUES (1, 'initial_runtime_schema', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        """
    ];

    public static async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        foreach (var statement in Statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureMemorySecurityColumnsAsync(connection, cancellationToken);
        await MarkSecurityMigrationAsync(connection, cancellationToken);
        await MarkAuditMigrationAsync(connection, cancellationToken);
    }

    private static async Task EnsureMemorySecurityColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(
            connection,
            "memory_items",
            "trust_level",
            "TEXT NOT NULL DEFAULT 'user_verified' CHECK (trust_level IN ('user_verified', 'trusted_import', 'local_generated', 'untrusted_import', 'quarantined'))",
            cancellationToken);
        await EnsureColumnAsync(connection, "memory_items", "source_uri", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "memory_items", "source_hash", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "memory_items", "created_by", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "memory_items", "approved_by", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "memory_items", "security_flags_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "memory_items", "classification", "TEXT NOT NULL DEFAULT 'normal'", cancellationToken);
        await EnsureColumnAsync(
            connection,
            "memory_items",
            "visibility",
            "TEXT NOT NULL DEFAULT 'profile' CHECK (visibility IN ('private', 'session', 'profile', 'shared'))",
            cancellationToken);

        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_memory_items_security
                ON memory_items(profile_id, memory_kind, trust_level, visibility, is_active);
            """;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(connection, table, column, cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task MarkSecurityMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (2, 'rag_memory_security_fields', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkAuditMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (3, 'rag_context_audit', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
