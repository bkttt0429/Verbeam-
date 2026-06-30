using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

internal static class SqliteSchema
{
    public const int CurrentVersion = 23;

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
        CREATE INDEX IF NOT EXISTS idx_translation_events_session_success
            ON translation_events(profile_id, session_id, source_language, target_language, mode, error_code, created_at);
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
        CREATE TABLE IF NOT EXISTS memory_principal_permissions (
            principal_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            role TEXT NOT NULL DEFAULT 'custom' CHECK (role IN ('blocked', 'reader', 'contributor', 'reviewer', 'admin', 'custom')),
            can_read_shared_memory INTEGER NOT NULL DEFAULT 0 CHECK (can_read_shared_memory IN (0, 1)),
            can_write_memory INTEGER NOT NULL DEFAULT 0 CHECK (can_write_memory IN (0, 1)),
            can_approve_memory INTEGER NOT NULL DEFAULT 0 CHECK (can_approve_memory IN (0, 1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (principal_id, profile_id),
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_principal_permissions_profile
            ON memory_principal_permissions(profile_id, principal_id);
        """,
        """
        CREATE TABLE IF NOT EXISTS memory_principal_sessions (
            id TEXT PRIMARY KEY,
            principal_id TEXT NOT NULL,
            token_hash TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            expires_at TEXT,
            revoked_at TEXT,
            last_seen_at TEXT
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_principal_sessions_principal
            ON memory_principal_sessions(principal_id, revoked_at, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS memory_principal_credentials (
            id TEXT PRIMARY KEY,
            principal_id TEXT NOT NULL,
            label TEXT NOT NULL DEFAULT '',
            secret_hash TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT,
            revoked_at TEXT,
            last_used_at TEXT
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_principal_credentials_lookup
            ON memory_principal_credentials(principal_id, secret_hash, revoked_at);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_principal_credentials_principal
            ON memory_principal_credentials(principal_id, revoked_at, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS memory_oidc_refresh_tokens (
            id TEXT PRIMARY KEY,
            principal_id TEXT NOT NULL,
            nonce BLOB NOT NULL,
            tag BLOB NOT NULL,
            ciphertext BLOB NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            expires_at TEXT,
            revoked_at TEXT,
            last_used_at TEXT
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_oidc_refresh_tokens_principal
            ON memory_oidc_refresh_tokens(principal_id, revoked_at, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS memory_maintenance_jobs (
            id TEXT PRIMARY KEY,
            job_kind TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'running', 'completed', 'failed')),
            profile_id TEXT NOT NULL,
            session_id TEXT NOT NULL DEFAULT '',
            source_language TEXT NOT NULL,
            target_language TEXT NOT NULL,
            mode TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            error_message TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            started_at TEXT,
            completed_at TEXT
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_maintenance_jobs_status
            ON memory_maintenance_jobs(status, updated_at, created_at);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_memory_maintenance_jobs_profile
            ON memory_maintenance_jobs(profile_id, status, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS rag_context_audit (
            id TEXT PRIMARY KEY,
            request_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            principal_id TEXT NOT NULL DEFAULT 'local',
            session_id TEXT NOT NULL DEFAULT '',
            translation_key TEXT,
            memory_id TEXT NOT NULL,
            memory_kind TEXT NOT NULL,
            snippet_hash TEXT NOT NULL,
            context_hash TEXT NOT NULL,
            trust_level TEXT NOT NULL,
            source_hash TEXT NOT NULL DEFAULT '',
            policy_version TEXT NOT NULL,
            context_character_count INTEGER NOT NULL DEFAULT 0,
            selected_memory_count INTEGER NOT NULL DEFAULT 0,
            selected_recent_event_count INTEGER NOT NULL DEFAULT 0,
            decision TEXT NOT NULL DEFAULT 'used',
            reason TEXT NOT NULL DEFAULT '',
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
        CREATE INDEX IF NOT EXISTS idx_scene_summaries_profile_latest
            ON scene_summaries(profile_id, session_id, updated_at);
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
            document_json TEXT NOT NULL DEFAULT '{"version":"ocr-ir-v1","pages":[]}',
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
        CREATE TABLE IF NOT EXISTS ocr_results (
            key TEXT PRIMARY KEY,
            image_hash TEXT NOT NULL,
            image_mime_type TEXT NOT NULL,
            provider TEXT NOT NULL,
            engine TEXT NOT NULL,
            engine_model_version TEXT NOT NULL,
            language TEXT NOT NULL,
            normalize_whitespace INTEGER NOT NULL CHECK (normalize_whitespace IN (0, 1)),
            correction_hash TEXT NOT NULL,
            raw_text TEXT NOT NULL DEFAULT '',
            corrected_text TEXT NOT NULL DEFAULT '',
            blocks_json TEXT NOT NULL DEFAULT '[]',
            document_json TEXT NOT NULL DEFAULT '{"version":"ocr-ir-v1","pages":[]}',
            corrections_json TEXT NOT NULL DEFAULT '[]',
            latency_ms INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            last_used_at TEXT NOT NULL,
            use_count INTEGER NOT NULL DEFAULT 0,
            detection_json TEXT NOT NULL DEFAULT '{}'
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_ocr_results_lookup
            ON ocr_results(image_hash, provider, language, engine_model_version, correction_hash);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_ocr_results_last_used
            ON ocr_results(last_used_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS ocr_jobs (
            id TEXT PRIMARY KEY,
            status TEXT NOT NULL,
            profile_id TEXT NOT NULL DEFAULT 'default',
            session_id TEXT NOT NULL DEFAULT '',
            image_hash TEXT NOT NULL DEFAULT '',
            image_mime_type TEXT NOT NULL DEFAULT '',
            language TEXT NOT NULL,
            provider TEXT NOT NULL,
            engine TEXT NOT NULL DEFAULT '',
            block_count INTEGER NOT NULL DEFAULT 0,
            progress REAL NOT NULL DEFAULT 0,
            result_event_id TEXT NOT NULL DEFAULT '',
            cache_hit INTEGER NOT NULL DEFAULT 0 CHECK (cache_hit IN (0, 1)),
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
        CREATE INDEX IF NOT EXISTS idx_ocr_jobs_profile_recent
            ON ocr_jobs(profile_id, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS ocr_job_events (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id TEXT NOT NULL,
            type TEXT NOT NULL,
            payload_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES ocr_jobs(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_ocr_job_events_job_sequence
            ON ocr_job_events(job_id, sequence);
        """,
        """
        CREATE TABLE IF NOT EXISTS ocr_smoke_results (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL DEFAULT 'default',
            session_id TEXT NOT NULL DEFAULT '',
            language TEXT NOT NULL,
            provider TEXT NOT NULL,
            engine TEXT NOT NULL,
            content_type TEXT NOT NULL DEFAULT '',
            preference TEXT NOT NULL DEFAULT '',
            preprocessing_preset TEXT NOT NULL DEFAULT '',
            ocr_event_id TEXT NOT NULL DEFAULT '',
            expected_text TEXT NOT NULL DEFAULT '',
            recognized_text TEXT NOT NULL DEFAULT '',
            exact_match INTEGER NOT NULL DEFAULT 0 CHECK (exact_match IN (0, 1)),
            contains_expected INTEGER NOT NULL DEFAULT 0 CHECK (contains_expected IN (0, 1)),
            similarity REAL NOT NULL DEFAULT 0,
            edit_distance INTEGER NOT NULL DEFAULT 0,
            latency_ms INTEGER NOT NULL DEFAULT 0,
            structure_json TEXT NOT NULL DEFAULT '{"pageCount":0,"blockCount":0,"textBlockCount":0,"tableBlockCount":0,"formulaBlockCount":0,"tableCellCount":0,"translatableCellCount":0,"invalidTableCellCount":0,"missingTableCellCount":0,"overlappingTableCellCount":0,"passThroughBlockCount":0}',
            structure_assertion_json TEXT NOT NULL DEFAULT '{"expected":null,"hasExpected":false,"passed":true,"mismatches":[]}',
            succeeded INTEGER NOT NULL DEFAULT 1 CHECK (succeeded IN (0, 1)),
            error_code TEXT NOT NULL DEFAULT '0',
            error_message TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_ocr_smoke_results_profile_recent
            ON ocr_smoke_results(profile_id, created_at);
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
        CREATE TABLE IF NOT EXISTS speech_video_sessions (
            id TEXT PRIMARY KEY,
            status TEXT NOT NULL,
            profile_id TEXT NOT NULL DEFAULT 'default',
            session_id TEXT NOT NULL DEFAULT '',
            source_url TEXT NOT NULL DEFAULT '',
            platform TEXT NOT NULL DEFAULT '',
            video_id TEXT NOT NULL DEFAULT '',
            title TEXT NOT NULL DEFAULT '',
            duration_seconds REAL NOT NULL DEFAULT 0,
            language TEXT NOT NULL,
            provider TEXT NOT NULL,
            captions_used INTEGER NOT NULL DEFAULT 0 CHECK (captions_used IN (0, 1)),
            segment_count INTEGER NOT NULL DEFAULT 0,
            request_json TEXT NOT NULL DEFAULT '{}',
            error_code TEXT NOT NULL DEFAULT '',
            error_message TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_speech_video_sessions_profile_recent
            ON speech_video_sessions(profile_id, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS speech_video_events (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL,
            type TEXT NOT NULL,
            payload_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL,
            FOREIGN KEY (session_id) REFERENCES speech_video_sessions(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_speech_video_events_session_sequence
            ON speech_video_events(session_id, sequence);
        """,
        """
        CREATE TABLE IF NOT EXISTS speech_video_buffers (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            start_seconds REAL NOT NULL,
            end_seconds REAL NOT NULL,
            file_path TEXT NOT NULL DEFAULT '',
            audio_mime_type TEXT NOT NULL DEFAULT '',
            byte_length INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL,
            error_code TEXT NOT NULL DEFAULT '',
            error_message TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (session_id) REFERENCES speech_video_sessions(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_speech_video_buffers_covering
            ON speech_video_buffers(session_id, start_seconds, end_seconds);
        """,
        """
        CREATE TABLE IF NOT EXISTS speech_video_window_tasks (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            start_seconds REAL NOT NULL,
            end_seconds REAL NOT NULL,
            priority INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL,
            error_code TEXT NOT NULL DEFAULT '',
            error_message TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (session_id) REFERENCES speech_video_sessions(id) ON DELETE CASCADE,
            UNIQUE (session_id, start_seconds, end_seconds)
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_speech_video_window_tasks_status_priority
            ON speech_video_window_tasks(session_id, status, priority);
        """,
        """
        CREATE TABLE IF NOT EXISTS speech_video_segments (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            segment_index INTEGER NOT NULL DEFAULT 0,
            start_seconds REAL NOT NULL,
            end_seconds REAL NOT NULL,
            text TEXT NOT NULL DEFAULT '',
            confidence REAL NOT NULL DEFAULT 1,
            speaker TEXT,
            language TEXT,
            provider TEXT NOT NULL DEFAULT '',
            engine TEXT NOT NULL DEFAULT '',
            window_start_seconds REAL NOT NULL DEFAULT 0,
            window_end_seconds REAL NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            FOREIGN KEY (session_id) REFERENCES speech_video_sessions(id) ON DELETE CASCADE,
            UNIQUE (session_id, start_seconds, end_seconds, text)
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_speech_video_segments_time
            ON speech_video_segments(session_id, start_seconds, end_seconds);
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
        await EnsureContextAuditDiagnosticColumnsAsync(connection, cancellationToken);
        await EnsureContextAuditPrincipalColumnsAsync(connection, cancellationToken);
        await MarkSecurityMigrationAsync(connection, cancellationToken);
        await MarkAuditMigrationAsync(connection, cancellationToken);
        await MarkVideoSpeechMigrationAsync(connection, cancellationToken);
        await MarkAuditDiagnosticsMigrationAsync(connection, cancellationToken);
        await EnsureOcrDocumentColumnAsync(connection, cancellationToken);
        await MarkOcrDocumentMigrationAsync(connection, cancellationToken);
        await EnsureOcrResultsTableAsync(connection, cancellationToken);
        await MarkOcrResultCacheMigrationAsync(connection, cancellationToken);
        await EnsureOcrJobsTableAsync(connection, cancellationToken);
        await MarkOcrJobsMigrationAsync(connection, cancellationToken);
        await EnsureOcrJobProgressColumnsAsync(connection, cancellationToken);
        await MarkOcrJobProgressMigrationAsync(connection, cancellationToken);
        await EnsureOcrSmokeStructureColumnAsync(connection, cancellationToken);
        await MarkOcrSmokeStructureMigrationAsync(connection, cancellationToken);
        await EnsureMemoryPrincipalPermissionsTableAsync(connection, cancellationToken);
        await MarkMemoryPrincipalPermissionsMigrationAsync(connection, cancellationToken);
        await EnsureMemoryPrincipalPermissionMutationColumnsAsync(connection, cancellationToken);
        await MarkMemoryPrincipalPermissionMutationMigrationAsync(connection, cancellationToken);
        await EnsureMemoryPrincipalSessionsTableAsync(connection, cancellationToken);
        await MarkMemoryPrincipalSessionsMigrationAsync(connection, cancellationToken);
        await MarkContextAuditPrincipalMigrationAsync(connection, cancellationToken);
        await EnsureMemoryPrincipalCredentialsTableAsync(connection, cancellationToken);
        await MarkMemoryPrincipalCredentialsMigrationAsync(connection, cancellationToken);
        await EnsureMemoryPrincipalPermissionRoleColumnAsync(connection, cancellationToken);
        await MarkMemoryPrincipalPermissionRoleMigrationAsync(connection, cancellationToken);
        await EnsureMemoryOidcRefreshTokensTableAsync(connection, cancellationToken);
        await MarkMemoryOidcRefreshTokensMigrationAsync(connection, cancellationToken);
        await EnsureMemoryMaintenanceJobsTableAsync(connection, cancellationToken);
        await MarkMemoryMaintenanceJobsMigrationAsync(connection, cancellationToken);
        await EnsureOcrLanguageDetectionColumnAsync(connection, cancellationToken);
        await MarkOcrLanguageDetectionMigrationAsync(connection, cancellationToken);
        await EnsureDocumentJobsTableAsync(connection, cancellationToken);
        await MarkDocumentJobsMigrationAsync(connection, cancellationToken);
        await EnsureOcrBlockAnnotationsTableAsync(connection, cancellationToken);
        await MarkOcrBlockAnnotationsMigrationAsync(connection, cancellationToken);
        await EnsureOcrBlockLayoutTableAsync(connection, cancellationToken);
        await MarkOcrBlockLayoutMigrationAsync(connection, cancellationToken);
        await EnsureTranslationEventTokenColumnsAsync(connection, cancellationToken);
        await MarkTranslationEventTokensMigrationAsync(connection, cancellationToken);
        await EnsureTranslationEventSurfaceColumnAsync(connection, cancellationToken);
        await MarkTranslationEventSurfaceMigrationAsync(connection, cancellationToken);
    }

    private static async Task EnsureTranslationEventSurfaceColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // App surface / feature source (0 = unknown). See Models.TranslationSurface.
        await EnsureColumnAsync(connection, "translation_events", "surface", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_translation_events_usage_surface
                ON translation_events(profile_id, created_at, surface);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkTranslationEventSurfaceMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (23, 'translation_event_surface', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureTranslationEventTokenColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "translation_events", "input_tokens", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "translation_events", "output_tokens", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "translation_events", "total_tokens", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "translation_events", "token_source", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "translation_events", "token_estimated", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_translation_events_usage
                ON translation_events(profile_id, created_at, provider, model);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkTranslationEventTokensMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (22, 'translation_event_token_usage', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureOcrBlockAnnotationsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ocr_block_annotations (
                profile_id TEXT NOT NULL DEFAULT 'default',
                image_hash TEXT NOT NULL,
                block_id TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'translated' CHECK (status IN ('translated', 'edited', 'ignored', 'locked')),
                locked INTEGER NOT NULL DEFAULT 0 CHECK (locked IN (0, 1)),
                edited_translation TEXT NOT NULL DEFAULT '',
                edited_source TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                reading_order_override INTEGER,
                type_override TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL,
                PRIMARY KEY (profile_id, image_hash, block_id),
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_ocr_block_annotations_image
                ON ocr_block_annotations(profile_id, image_hash);

            CREATE TABLE IF NOT EXISTS ocr_block_history (
                id TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL DEFAULT 'default',
                image_hash TEXT NOT NULL,
                block_id TEXT NOT NULL,
                kind TEXT NOT NULL DEFAULT 'translation' CHECK (kind IN ('ocr', 'translation')),
                source_text TEXT NOT NULL DEFAULT '',
                translated_text TEXT NOT NULL DEFAULT '',
                engine TEXT NOT NULL DEFAULT '',
                provider TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_ocr_block_history_block
                ON ocr_block_history(profile_id, image_hash, block_id, created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkOcrBlockAnnotationsMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (21, 'ocr_block_annotations', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureOcrBlockLayoutTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // Per-block geometry + text-format overrides for the PDF overlay editor.
        // Sibling to ocr_block_annotations (which keeps status/locked/edited-text); kept
        // separate because layout is PDF-document-scoped (doc_key = "{jobId}:{pageIndex}")
        // and stores normalized 0..1 bbox so it survives re-render at any DPI/scale.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ocr_block_layout (
                profile_id TEXT NOT NULL DEFAULT 'default',
                doc_key TEXT NOT NULL,
                block_id TEXT NOT NULL,
                nx REAL,
                ny REAL,
                nw REAL,
                nh REAL,
                font_size REAL,
                line_height REAL,
                text_align TEXT NOT NULL DEFAULT '',
                overflow TEXT NOT NULL DEFAULT 'shrink' CHECK (overflow IN ('shrink', 'wrap', 'retranslate-shorter')),
                updated_at TEXT NOT NULL,
                PRIMARY KEY (profile_id, doc_key, block_id),
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_ocr_block_layout_doc
                ON ocr_block_layout(profile_id, doc_key);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkOcrBlockLayoutMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (22, 'ocr_block_layout', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureOcrLanguageDetectionColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(
            connection,
            "ocr_results",
            "detection_json",
            "TEXT NOT NULL DEFAULT '{}'",
            cancellationToken);
    }

    private static async Task MarkOcrLanguageDetectionMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (19, 'ocr_language_detection', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureDocumentJobsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS document_jobs (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                profile_id TEXT NOT NULL DEFAULT 'default',
                session_id TEXT NOT NULL DEFAULT '',
                source_kind TEXT NOT NULL DEFAULT 'unknown',
                input_file_name TEXT NOT NULL DEFAULT '',
                input_mime_type TEXT NOT NULL DEFAULT 'application/octet-stream',
                input_hash TEXT NOT NULL DEFAULT '',
                stage TEXT NOT NULL DEFAULT '',
                total_units INTEGER NULL,
                completed_units INTEGER NOT NULL DEFAULT 0,
                progress REAL NOT NULL DEFAULT 0,
                artifact_count INTEGER NOT NULL DEFAULT 0,
                warning_count INTEGER NOT NULL DEFAULT 0,
                error_code TEXT NOT NULL DEFAULT '',
                error_message TEXT NOT NULL DEFAULT '',
                request_json TEXT NOT NULL DEFAULT '{}',
                artifacts_json TEXT NOT NULL DEFAULT '[]',
                warnings_json TEXT NOT NULL DEFAULT '[]',
                created_at TEXT NOT NULL,
                started_at TEXT,
                completed_at TEXT,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_document_jobs_profile_recent
                ON document_jobs(profile_id, created_at);

            CREATE TABLE IF NOT EXISTS document_job_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                type TEXT NOT NULL,
                payload_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                FOREIGN KEY (job_id) REFERENCES document_jobs(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_document_job_events_job_sequence
                ON document_job_events(job_id, sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkDocumentJobsMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (20, 'document_jobs', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task EnsureContextAuditDiagnosticColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(
            connection,
            "rag_context_audit",
            "context_character_count",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "rag_context_audit",
            "selected_memory_count",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "rag_context_audit",
            "selected_recent_event_count",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
    }

    private static async Task EnsureContextAuditPrincipalColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(
            connection,
            "rag_context_audit",
            "principal_id",
            "TEXT NOT NULL DEFAULT 'local'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "rag_context_audit",
            "decision",
            "TEXT NOT NULL DEFAULT 'used'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "rag_context_audit",
            "reason",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_rag_context_audit_principal_recent
                ON rag_context_audit(profile_id, principal_id, created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task MarkVideoSpeechMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (4, 'speech_video_sessions', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkAuditDiagnosticsMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (5, 'rag_context_audit_diagnostics', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureOcrDocumentColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(
            connection,
            "ocr_events",
            "document_json",
            "TEXT NOT NULL DEFAULT '{\"version\":\"ocr-ir-v1\",\"pages\":[]}'",
            cancellationToken);
    }

    private static async Task MarkOcrDocumentMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (6, 'ocr_document_ir', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureOcrResultsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ocr_results (
                key TEXT PRIMARY KEY,
                image_hash TEXT NOT NULL,
                image_mime_type TEXT NOT NULL,
                provider TEXT NOT NULL,
                engine TEXT NOT NULL,
                engine_model_version TEXT NOT NULL,
                language TEXT NOT NULL,
                normalize_whitespace INTEGER NOT NULL CHECK (normalize_whitespace IN (0, 1)),
                correction_hash TEXT NOT NULL,
                raw_text TEXT NOT NULL DEFAULT '',
                corrected_text TEXT NOT NULL DEFAULT '',
                blocks_json TEXT NOT NULL DEFAULT '[]',
                document_json TEXT NOT NULL DEFAULT '{"version":"ocr-ir-v1","pages":[]}',
                corrections_json TEXT NOT NULL DEFAULT '[]',
                latency_ms INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                last_used_at TEXT NOT NULL,
                use_count INTEGER NOT NULL DEFAULT 0,
                detection_json TEXT NOT NULL DEFAULT '{}'
            );

            CREATE INDEX IF NOT EXISTS idx_ocr_results_lookup
                ON ocr_results(image_hash, provider, language, engine_model_version, correction_hash);

            CREATE INDEX IF NOT EXISTS idx_ocr_results_last_used
                ON ocr_results(last_used_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkOcrResultCacheMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (7, 'ocr_result_cache', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureOcrJobsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ocr_jobs (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                profile_id TEXT NOT NULL DEFAULT 'default',
                session_id TEXT NOT NULL DEFAULT '',
                image_hash TEXT NOT NULL DEFAULT '',
                image_mime_type TEXT NOT NULL DEFAULT '',
                language TEXT NOT NULL,
                provider TEXT NOT NULL,
                engine TEXT NOT NULL DEFAULT '',
                block_count INTEGER NOT NULL DEFAULT 0,
                progress REAL NOT NULL DEFAULT 0,
                result_event_id TEXT NOT NULL DEFAULT '',
                cache_hit INTEGER NOT NULL DEFAULT 0 CHECK (cache_hit IN (0, 1)),
                error_code TEXT NOT NULL DEFAULT '',
                error_message TEXT NOT NULL DEFAULT '',
                request_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                started_at TEXT,
                completed_at TEXT,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_ocr_jobs_profile_recent
                ON ocr_jobs(profile_id, created_at);

            CREATE TABLE IF NOT EXISTS ocr_job_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                type TEXT NOT NULL,
                payload_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                FOREIGN KEY (job_id) REFERENCES ocr_jobs(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_ocr_job_events_job_sequence
                ON ocr_job_events(job_id, sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkOcrJobsMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (8, 'ocr_jobs', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureOcrJobProgressColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "ocr_jobs", "stage", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "ocr_jobs", "estimated_duration_ms", "INTEGER NULL", cancellationToken);
    }

    private static async Task MarkOcrJobProgressMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (16, 'ocr_job_stage_progress', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureOcrSmokeStructureColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(
            connection,
            "ocr_smoke_results",
            "structure_json",
            "TEXT NOT NULL DEFAULT '{\"pageCount\":0,\"blockCount\":0,\"textBlockCount\":0,\"tableBlockCount\":0,\"formulaBlockCount\":0,\"tableCellCount\":0,\"translatableCellCount\":0,\"invalidTableCellCount\":0,\"missingTableCellCount\":0,\"overlappingTableCellCount\":0,\"passThroughBlockCount\":0}'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "ocr_smoke_results",
            "structure_assertion_json",
            "TEXT NOT NULL DEFAULT '{\"expected\":null,\"hasExpected\":false,\"passed\":true,\"mismatches\":[]}'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "ocr_smoke_results",
            "succeeded",
            "INTEGER NOT NULL DEFAULT 1 CHECK (succeeded IN (0, 1))",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "ocr_smoke_results",
            "error_code",
            "TEXT NOT NULL DEFAULT '0'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "ocr_smoke_results",
            "error_message",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
    }

    private static async Task MarkOcrSmokeStructureMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (9, 'ocr_smoke_structure_summary', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureMemoryPrincipalPermissionsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_principal_permissions (
                principal_id TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                role TEXT NOT NULL DEFAULT 'custom' CHECK (role IN ('blocked', 'reader', 'contributor', 'reviewer', 'admin', 'custom')),
                can_read_shared_memory INTEGER NOT NULL DEFAULT 0 CHECK (can_read_shared_memory IN (0, 1)),
                can_write_memory INTEGER NOT NULL DEFAULT 0 CHECK (can_write_memory IN (0, 1)),
                can_approve_memory INTEGER NOT NULL DEFAULT 0 CHECK (can_approve_memory IN (0, 1)),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (principal_id, profile_id),
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_memory_principal_permissions_profile
                ON memory_principal_permissions(profile_id, principal_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureMemoryPrincipalPermissionMutationColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(
            connection,
            "memory_principal_permissions",
            "can_write_memory",
            "INTEGER NOT NULL DEFAULT 0 CHECK (can_write_memory IN (0, 1))",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "memory_principal_permissions",
            "can_approve_memory",
            "INTEGER NOT NULL DEFAULT 0 CHECK (can_approve_memory IN (0, 1))",
            cancellationToken);
    }

    private static async Task EnsureMemoryPrincipalPermissionRoleColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(
            connection,
            "memory_principal_permissions",
            "role",
            "TEXT NOT NULL DEFAULT 'custom' CHECK (role IN ('blocked', 'reader', 'contributor', 'reviewer', 'admin', 'custom'))",
            cancellationToken);
    }

    private static async Task MarkMemoryPrincipalPermissionsMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (10, 'memory_principal_permissions', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkMemoryPrincipalPermissionMutationMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (11, 'memory_principal_permission_mutation_acl', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkMemoryPrincipalPermissionRoleMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (15, 'memory_principal_permission_roles', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureMemoryPrincipalSessionsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_principal_sessions (
                id TEXT PRIMARY KEY,
                principal_id TEXT NOT NULL,
                token_hash TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                expires_at TEXT,
                revoked_at TEXT,
                last_seen_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_memory_principal_sessions_principal
                ON memory_principal_sessions(principal_id, revoked_at, created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkMemoryPrincipalSessionsMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (12, 'memory_principal_sessions', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureMemoryPrincipalCredentialsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_principal_credentials (
                id TEXT PRIMARY KEY,
                principal_id TEXT NOT NULL,
                label TEXT NOT NULL DEFAULT '',
                secret_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT,
                revoked_at TEXT,
                last_used_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_memory_principal_credentials_lookup
                ON memory_principal_credentials(principal_id, secret_hash, revoked_at);

            CREATE INDEX IF NOT EXISTS idx_memory_principal_credentials_principal
                ON memory_principal_credentials(principal_id, revoked_at, created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkMemoryPrincipalCredentialsMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (14, 'memory_principal_credentials', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureMemoryOidcRefreshTokensTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_oidc_refresh_tokens (
                id TEXT PRIMARY KEY,
                principal_id TEXT NOT NULL,
                nonce BLOB NOT NULL,
                tag BLOB NOT NULL,
                ciphertext BLOB NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                expires_at TEXT,
                revoked_at TEXT,
                last_used_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_memory_oidc_refresh_tokens_principal
                ON memory_oidc_refresh_tokens(principal_id, revoked_at, created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkMemoryOidcRefreshTokensMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (17, 'memory_oidc_refresh_tokens', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureMemoryMaintenanceJobsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_maintenance_jobs (
                id TEXT PRIMARY KEY,
                job_kind TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'running', 'completed', 'failed')),
                profile_id TEXT NOT NULL,
                session_id TEXT NOT NULL DEFAULT '',
                source_language TEXT NOT NULL,
                target_language TEXT NOT NULL,
                mode TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                error_message TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                started_at TEXT,
                completed_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_memory_maintenance_jobs_status
                ON memory_maintenance_jobs(status, updated_at, created_at);

            CREATE INDEX IF NOT EXISTS idx_memory_maintenance_jobs_profile
                ON memory_maintenance_jobs(profile_id, status, created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkMemoryMaintenanceJobsMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (18, 'memory_maintenance_jobs', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkContextAuditPrincipalMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (13, 'rag_context_audit_principal', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
