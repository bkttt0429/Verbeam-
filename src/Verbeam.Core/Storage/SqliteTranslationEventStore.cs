using Verbeam.Core.Models;
using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

public sealed class SqliteTranslationEventStore : ITranslationEventStore
{
    private readonly string _databasePath;

    public SqliteTranslationEventStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task AddEventAsync(TranslationEvent entry, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await EnsureProfileAsync(connection, entry, cancellationToken);
        await EnsureSessionAsync(connection, entry, cancellationToken);
        await InsertEventAsync(connection, entry, cancellationToken);
    }

    public async Task AddEventsAsync(IReadOnlyList<TranslationEvent> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        // Raw BEGIN/COMMIT keeps the shared Ensure*/Insert helpers usable as-is;
        // Microsoft.Data.Sqlite would otherwise require Transaction on every command.
        await ExecuteAsync(connection, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            foreach (var entry in entries)
            {
                await EnsureProfileAsync(connection, entry, cancellationToken);
                await EnsureSessionAsync(connection, entry, cancellationToken);
                await InsertEventAsync(connection, entry, cancellationToken);
            }

            await ExecuteAsync(connection, "COMMIT", cancellationToken);
        }
        catch
        {
            await ExecuteAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        TranslationEvent entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO translation_events (
                id, session_id, profile_id, translation_key, request_name,
                source_text, translated_text, source_language, target_language,
                mode, provider, glossary_id, glossary_hash, engine, model,
                latency_ms, cache_hit, error_code, error_message, created_at,
                input_tokens, output_tokens, total_tokens, token_source, token_estimated,
                surface
            )
            VALUES (
                $id, $session_id, $profile_id, $translation_key, $request_name,
                $source_text, $translated_text, $source_language, $target_language,
                $mode, $provider, $glossary_id, $glossary_hash, $engine, $model,
                $latency_ms, $cache_hit, $error_code, $error_message, $created_at,
                $input_tokens, $output_tokens, $total_tokens, $token_source, $token_estimated,
                $surface
            )
            """;

        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$session_id", string.IsNullOrWhiteSpace(entry.SessionId) ? DBNull.Value : entry.SessionId);
        command.Parameters.AddWithValue("$profile_id", entry.ProfileId);
        command.Parameters.AddWithValue("$translation_key", string.IsNullOrWhiteSpace(entry.TranslationKey) ? DBNull.Value : entry.TranslationKey);
        command.Parameters.AddWithValue("$request_name", entry.RequestName);
        command.Parameters.AddWithValue("$source_text", entry.SourceText);
        command.Parameters.AddWithValue("$translated_text", entry.TranslatedText);
        command.Parameters.AddWithValue("$source_language", entry.SourceLanguage);
        command.Parameters.AddWithValue("$target_language", entry.TargetLanguage);
        command.Parameters.AddWithValue("$mode", entry.Mode);
        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$glossary_id", entry.GlossaryId);
        command.Parameters.AddWithValue("$glossary_hash", entry.GlossaryHash);
        command.Parameters.AddWithValue("$engine", entry.Engine);
        command.Parameters.AddWithValue("$model", entry.Model);
        command.Parameters.AddWithValue("$latency_ms", entry.LatencyMs);
        command.Parameters.AddWithValue("$cache_hit", entry.CacheHit ? 1 : 0);
        command.Parameters.AddWithValue("$error_code", entry.ErrorCode);
        command.Parameters.AddWithValue("$error_message", entry.ErrorMessage);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$input_tokens", entry.InputTokens);
        command.Parameters.AddWithValue("$output_tokens", entry.OutputTokens);
        command.Parameters.AddWithValue("$total_tokens", entry.TotalTokens);
        command.Parameters.AddWithValue("$token_source", entry.TokenSource);
        command.Parameters.AddWithValue("$token_estimated", entry.TokenEstimated ? 1 : 0);
        command.Parameters.AddWithValue("$surface", entry.Surface);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TranslationEvent?> GetEventAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, profile_id, translation_key, request_name,
                   source_text, translated_text, source_language, target_language,
                   mode, provider, glossary_id, glossary_hash, engine, model,
                   latency_ms, cache_hit, error_code, error_message, created_at
            FROM translation_events
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    public async Task<IReadOnlyList<TranslationEvent>> ListEventsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, profile_id, translation_key, request_name,
                   source_text, translated_text, source_language, target_language,
                   mode, provider, glossary_id, glossary_hash, engine, model,
                   latency_ms, cache_hit, error_code, error_message, created_at
            FROM translation_events
            WHERE profile_id = $profile_id
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var values = new List<TranslationEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadEvent(reader));
        }

        return values;
    }

    public async Task<IReadOnlyList<TranslationEvent>> ListRecentContextAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        string excludeSourceText,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, profile_id, translation_key, request_name,
                   source_text, translated_text, source_language, target_language,
                   mode, provider, glossary_id, glossary_hash, engine, model,
                   latency_ms, cache_hit, error_code, error_message, created_at
            FROM translation_events
            WHERE profile_id = $profile_id
              AND IFNULL(session_id, '') = $session_id
              AND source_language = $source_language
              AND target_language = $target_language
              AND mode = $mode
              AND error_code = '0'
              AND translated_text <> ''
              AND source_text <> $exclude_source_text
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$source_language", sourceLanguage);
        command.Parameters.AddWithValue("$target_language", targetLanguage);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$exclude_source_text", excludeSourceText);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));

        var values = new List<TranslationEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadEvent(reader));
        }

        return values;
    }

    public async Task<IReadOnlyList<TranslationEvent>> ListSessionSuccessEventsAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, profile_id, translation_key, request_name,
                   source_text, translated_text, source_language, target_language,
                   mode, provider, glossary_id, glossary_hash, engine, model,
                   latency_ms, cache_hit, error_code, error_message, created_at
            FROM translation_events
            WHERE profile_id = $profile_id
              AND IFNULL(session_id, '') = $session_id
              AND source_language = $source_language
              AND target_language = $target_language
              AND mode = $mode
              AND error_code = '0'
              AND translated_text <> ''
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$source_language", sourceLanguage);
        command.Parameters.AddWithValue("$target_language", targetLanguage);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var values = new List<TranslationEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadEvent(reader));
        }

        values.Reverse();
        return values;
    }

    public async Task<TokenUsageSummary> GetUsageSummaryAsync(
        string profileId,
        string range,
        DateTimeOffset? sinceUtc,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        var sinceClause = sinceUtc.HasValue ? "AND created_at >= $since" : string.Empty;
        var sinceValue = sinceUtc?.ToString("O");

        // Per provider+model breakdown.
        var byProvider = new List<TokenUsageBreakdown>();
        long totalRequests = 0, totalInput = 0, totalOutput = 0, totalTokens = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT provider, model,
                       COUNT(*) AS requests,
                       COALESCE(SUM(input_tokens), 0) AS input_tokens,
                       COALESCE(SUM(output_tokens), 0) AS output_tokens,
                       COALESCE(SUM(total_tokens), 0) AS total_tokens
                FROM translation_events
                WHERE profile_id = $profile_id
                  AND error_code = '0'
                  {sinceClause}
                GROUP BY provider, model
                ORDER BY total_tokens DESC
                """;
            command.Parameters.AddWithValue("$profile_id", profileId);
            if (sinceUtc.HasValue)
            {
                command.Parameters.AddWithValue("$since", sinceValue!);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var requests = reader.GetInt64(2);
                var input = reader.GetInt64(3);
                var output = reader.GetInt64(4);
                var total = reader.GetInt64(5);
                byProvider.Add(new TokenUsageBreakdown(
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    requests,
                    input,
                    output,
                    total));
                totalRequests += requests;
                totalInput += input;
                totalOutput += output;
                totalTokens += total;
            }
        }

        // Daily trend.
        var daily = new List<TokenUsageDailyPoint>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT substr(created_at, 1, 10) AS day,
                       COUNT(*) AS requests,
                       COALESCE(SUM(total_tokens), 0) AS total_tokens
                FROM translation_events
                WHERE profile_id = $profile_id
                  AND error_code = '0'
                  {sinceClause}
                GROUP BY day
                ORDER BY day ASC
                """;
            command.Parameters.AddWithValue("$profile_id", profileId);
            if (sinceUtc.HasValue)
            {
                command.Parameters.AddWithValue("$since", sinceValue!);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                daily.Add(new TokenUsageDailyPoint(
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2)));
            }
        }

        // Per app-surface breakdown (feature source).
        var bySurface = new List<TokenUsageSurfaceBreakdown>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT surface,
                       COUNT(*) AS requests,
                       COALESCE(SUM(input_tokens), 0) AS input_tokens,
                       COALESCE(SUM(output_tokens), 0) AS output_tokens,
                       COALESCE(SUM(total_tokens), 0) AS total_tokens
                FROM translation_events
                WHERE profile_id = $profile_id
                  AND error_code = '0'
                  {sinceClause}
                GROUP BY surface
                ORDER BY total_tokens DESC
                """;
            command.Parameters.AddWithValue("$profile_id", profileId);
            if (sinceUtc.HasValue)
            {
                command.Parameters.AddWithValue("$since", sinceValue!);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                bySurface.Add(new TokenUsageSurfaceBreakdown(
                    TranslationSurface.ToKey(reader.GetInt32(0)),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }
        }

        return new TokenUsageSummary(
            profileId,
            range,
            totalRequests,
            totalInput,
            totalOutput,
            totalTokens,
            byProvider,
            daily)
        {
            BySurface = bySurface
        };
    }

    private static async Task EnsureProfileAsync(
        SqliteConnection connection,
        TranslationEvent entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
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
                $id,
                $display_name,
                $source_language,
                $target_language,
                $default_mode,
                $default_provider,
                $created_at,
                $updated_at
            )
            """;
        command.Parameters.AddWithValue("$id", entry.ProfileId);
        command.Parameters.AddWithValue("$display_name", entry.ProfileId);
        command.Parameters.AddWithValue("$source_language", entry.SourceLanguage);
        command.Parameters.AddWithValue("$target_language", entry.TargetLanguage);
        command.Parameters.AddWithValue("$default_mode", entry.Mode);
        command.Parameters.AddWithValue("$default_provider", entry.Provider);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", entry.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSessionAsync(
        SqliteConnection connection,
        TranslationEvent entry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.SessionId))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO translation_sessions (
                id,
                profile_id,
                source_language,
                target_language,
                mode,
                provider,
                started_at,
                last_seen_at
            )
            VALUES (
                $id,
                $profile_id,
                $source_language,
                $target_language,
                $mode,
                $provider,
                $started_at,
                $last_seen_at
            )
            """;
        command.Parameters.AddWithValue("$id", entry.SessionId);
        command.Parameters.AddWithValue("$profile_id", entry.ProfileId);
        command.Parameters.AddWithValue("$source_language", entry.SourceLanguage);
        command.Parameters.AddWithValue("$target_language", entry.TargetLanguage);
        command.Parameters.AddWithValue("$mode", entry.Mode);
        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$started_at", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$last_seen_at", entry.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TranslationEvent ReadEvent(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetInt64(15),
            reader.GetInt32(16) == 1,
            reader.GetString(17),
            reader.GetString(18),
            DateTimeOffset.Parse(reader.GetString(19)));
}
