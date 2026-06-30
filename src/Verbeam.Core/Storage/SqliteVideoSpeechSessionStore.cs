using System.Text.Json;
using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class SqliteVideoSpeechSessionStore : IVideoSpeechSessionStore
{
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteVideoSpeechSessionStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task AddSessionAsync(
        VideoSpeechSessionStatus session,
        VideoSpeechSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO speech_video_sessions (
                id, status, profile_id, session_id, source_url, platform, video_id,
                title, duration_seconds, language, provider, captions_used, segment_count,
                request_json, error_code, error_message, created_at, updated_at
            )
            VALUES (
                $id, $status, $profile_id, $session_id, $source_url, $platform, $video_id,
                $title, $duration_seconds, $language, $provider, $captions_used, $segment_count,
                $request_json, $error_code, $error_message, $created_at, $updated_at
            )
            """;
        AddSessionParameters(command, session);
        command.Parameters.AddWithValue("$request_json", JsonSerializer.Serialize(request, _jsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<VideoSpeechSessionStatus?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSessionSql + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    public async Task<VideoSpeechSessionRequest?> GetSessionRequestAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_json FROM speech_video_sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<VideoSpeechSessionRequest>(json, _jsonOptions);
    }

    public async Task<IReadOnlyList<VideoSpeechSessionStatus>> ListSessionsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSessionSql + """
            WHERE profile_id = $profile_id
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var values = new List<VideoSpeechSessionStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadSession(reader));
        }

        return values;
    }

    public async Task UpdateSessionAsync(
        VideoSpeechSessionStatus session,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE speech_video_sessions
            SET status = $status,
                profile_id = $profile_id,
                session_id = $session_id,
                source_url = $source_url,
                platform = $platform,
                video_id = $video_id,
                title = $title,
                duration_seconds = $duration_seconds,
                language = $language,
                provider = $provider,
                captions_used = $captions_used,
                segment_count = $segment_count,
                error_code = $error_code,
                error_message = $error_message,
                updated_at = $updated_at
            WHERE id = $id
            """;
        AddSessionParameters(command, session);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<long> AddEventAsync(
        string sessionId,
        string type,
        object payload,
        CancellationToken cancellationToken = default)
        => AddEventJsonAsync(sessionId, type, JsonSerializer.Serialize(payload, _jsonOptions), cancellationToken);

    public async Task<long> AddEventJsonAsync(
        string sessionId,
        string type,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO speech_video_events (session_id, type, payload_json, created_at)
            VALUES ($session_id, $type, $payload_json, $created_at);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<VideoSpeechSessionEvent>> ListEventsAsync(
        string sessionId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, session_id, type, payload_json, created_at
            FROM speech_video_events
            WHERE session_id = $session_id AND sequence > $after_sequence
            ORDER BY sequence
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$after_sequence", Math.Max(0, afterSequence));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var values = new List<VideoSpeechSessionEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new VideoSpeechSessionEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return values;
    }

    public async Task UpsertBufferAsync(
        VideoSpeechAudioBuffer buffer,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO speech_video_buffers (
                id, session_id, start_seconds, end_seconds, file_path, audio_mime_type,
                byte_length, status, error_code, error_message, created_at, updated_at
            )
            VALUES (
                $id, $session_id, $start_seconds, $end_seconds, $file_path, $audio_mime_type,
                $byte_length, $status, $error_code, $error_message, $created_at, $updated_at
            )
            ON CONFLICT(id) DO UPDATE SET
                start_seconds = excluded.start_seconds,
                end_seconds = excluded.end_seconds,
                file_path = excluded.file_path,
                audio_mime_type = excluded.audio_mime_type,
                byte_length = excluded.byte_length,
                status = excluded.status,
                error_code = excluded.error_code,
                error_message = excluded.error_message,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", buffer.Id);
        command.Parameters.AddWithValue("$session_id", buffer.SessionId);
        command.Parameters.AddWithValue("$start_seconds", buffer.StartSeconds);
        command.Parameters.AddWithValue("$end_seconds", buffer.EndSeconds);
        command.Parameters.AddWithValue("$file_path", buffer.FilePath);
        command.Parameters.AddWithValue("$audio_mime_type", buffer.AudioMimeType);
        command.Parameters.AddWithValue("$byte_length", buffer.ByteLength);
        command.Parameters.AddWithValue("$status", buffer.Status);
        command.Parameters.AddWithValue("$error_code", buffer.ErrorCode);
        command.Parameters.AddWithValue("$error_message", buffer.ErrorMessage);
        command.Parameters.AddWithValue("$created_at", buffer.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", buffer.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<VideoSpeechAudioBuffer?> FindCoveringBufferAsync(
        string sessionId,
        double startSeconds,
        double endSeconds,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, start_seconds, end_seconds, file_path, audio_mime_type,
                   byte_length, status, error_code, error_message, created_at, updated_at
            FROM speech_video_buffers
            WHERE session_id = $session_id
              AND status = 'succeeded'
              AND start_seconds <= $start_seconds
              AND end_seconds >= $end_seconds
            ORDER BY (end_seconds - start_seconds) ASC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$start_seconds", startSeconds);
        command.Parameters.AddWithValue("$end_seconds", endSeconds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBuffer(reader) : null;
    }

    public async Task<IReadOnlyList<VideoSpeechAudioBuffer>> ListBuffersAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, start_seconds, end_seconds, file_path, audio_mime_type,
                   byte_length, status, error_code, error_message, created_at, updated_at
            FROM speech_video_buffers
            WHERE session_id = $session_id
            ORDER BY updated_at
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);

        var values = new List<VideoSpeechAudioBuffer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadBuffer(reader));
        }

        return values;
    }

    public async Task DeleteBufferAsync(
        string bufferId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM speech_video_buffers WHERE id = $id";
        command.Parameters.AddWithValue("$id", bufferId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteBuffersAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM speech_video_buffers WHERE session_id = $session_id";
        command.Parameters.AddWithValue("$session_id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task TrimEventsAsync(
        string sessionId,
        int keepLast,
        CancellationToken cancellationToken = default)
    {
        if (keepLast <= 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM speech_video_events
            WHERE session_id = $session_id
              AND sequence < COALESCE((
                SELECT MIN(sequence) FROM (
                    SELECT sequence
                    FROM speech_video_events
                    WHERE session_id = $session_id
                    ORDER BY sequence DESC
                    LIMIT $keep_last
                )
              ), 0)
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$keep_last", keepLast);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryMarkWindowQueuedAsync(
        VideoSpeechWindowTask task,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO speech_video_window_tasks (
                id, session_id, start_seconds, end_seconds, priority, status,
                error_code, error_message, created_at, updated_at
            )
            VALUES (
                $id, $session_id, $start_seconds, $end_seconds, $priority, 'queued',
                '', '', $created_at, $updated_at
            )
            ON CONFLICT(session_id, start_seconds, end_seconds) DO UPDATE SET
                priority = MAX(priority, excluded.priority),
                status = 'queued',
                error_code = '',
                error_message = '',
                updated_at = excluded.updated_at
            WHERE speech_video_window_tasks.status NOT IN ('succeeded', 'running');
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$session_id", task.SessionId);
        command.Parameters.AddWithValue("$start_seconds", task.StartSeconds);
        command.Parameters.AddWithValue("$end_seconds", task.EndSeconds);
        command.Parameters.AddWithValue("$priority", task.Priority);
        command.Parameters.AddWithValue("$created_at", task.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", task.UpdatedAt.ToString("O"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result) > 0;
    }

    public async Task UpdateWindowTaskStatusAsync(
        string sessionId,
        double startSeconds,
        double endSeconds,
        string status,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE speech_video_window_tasks
            SET status = $status,
                error_code = $error_code,
                error_message = $error_message,
                updated_at = $updated_at
            WHERE session_id = $session_id
              AND start_seconds = $start_seconds
              AND end_seconds = $end_seconds
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$start_seconds", startSeconds);
        command.Parameters.AddWithValue("$end_seconds", endSeconds);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error_code", errorCode);
        command.Parameters.AddWithValue("$error_message", errorMessage);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VideoSpeechWindowTask>> ListWindowTasksAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, start_seconds, end_seconds, priority, status,
                   error_code, error_message, created_at, updated_at
            FROM speech_video_window_tasks
            WHERE session_id = $session_id
            ORDER BY start_seconds
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);

        var values = new List<VideoSpeechWindowTask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new VideoSpeechWindowTask(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8)),
                DateTimeOffset.Parse(reader.GetString(9))));
        }

        return values;
    }

    public async Task ResetStaleRunningWindowsAsync(
        string sessionId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE speech_video_window_tasks
            SET status = 'queued', updated_at = $now
            WHERE session_id = $session_id
              AND status = 'running'
              AND updated_at < $cutoff
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddSegmentsAsync(
        IReadOnlyList<VideoSpeechCachedSegment> segments,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var segment in segments)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO speech_video_segments (
                    id, session_id, segment_index, start_seconds, end_seconds, text,
                    confidence, speaker, language, provider, engine,
                    window_start_seconds, window_end_seconds, created_at
                )
                VALUES (
                    $id, $session_id, $segment_index, $start_seconds, $end_seconds, $text,
                    $confidence, $speaker, $language, $provider, $engine,
                    $window_start_seconds, $window_end_seconds, $created_at
                )
                """;
            command.Parameters.AddWithValue("$id", segment.Id);
            command.Parameters.AddWithValue("$session_id", segment.SessionId);
            command.Parameters.AddWithValue("$segment_index", segment.Index);
            command.Parameters.AddWithValue("$start_seconds", segment.StartSeconds);
            command.Parameters.AddWithValue("$end_seconds", segment.EndSeconds);
            command.Parameters.AddWithValue("$text", segment.Text);
            command.Parameters.AddWithValue("$confidence", segment.Confidence);
            command.Parameters.AddWithValue("$speaker", ToDbValue(segment.Speaker));
            command.Parameters.AddWithValue("$language", ToDbValue(segment.Language));
            command.Parameters.AddWithValue("$provider", segment.Provider);
            command.Parameters.AddWithValue("$engine", segment.Engine);
            command.Parameters.AddWithValue("$window_start_seconds", segment.WindowStartSeconds);
            command.Parameters.AddWithValue("$window_end_seconds", segment.WindowEndSeconds);
            command.Parameters.AddWithValue("$created_at", segment.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VideoSpeechCachedSegment>> ListSegmentsAsync(
        string sessionId,
        double startSeconds,
        double endSeconds,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, segment_index, start_seconds, end_seconds, text,
                   confidence, speaker, language, provider, engine,
                   window_start_seconds, window_end_seconds, created_at
            FROM speech_video_segments
            WHERE session_id = $session_id
              AND end_seconds >= $start_seconds
              AND start_seconds <= $end_seconds
            ORDER BY start_seconds, end_seconds
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$start_seconds", Math.Max(0, startSeconds));
        command.Parameters.AddWithValue("$end_seconds", Math.Max(startSeconds, endSeconds));

        var values = new List<VideoSpeechCachedSegment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadSegment(reader));
        }

        return values;
    }

    public async Task<int> CountSegmentsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM speech_video_segments WHERE session_id = $session_id";
        command.Parameters.AddWithValue("$session_id", sessionId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private const string SelectSessionSql = """
        SELECT id, status, profile_id, session_id, source_url, platform, video_id,
               title, duration_seconds, language, provider, captions_used, segment_count,
               error_code, error_message, created_at, updated_at
        FROM speech_video_sessions
        
        """;

    private static VideoSpeechSessionStatus ReadSession(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetDouble(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetInt32(11) == 1,
            reader.GetInt32(12),
            reader.GetString(13),
            reader.GetString(14),
            DateTimeOffset.Parse(reader.GetString(15)),
            DateTimeOffset.Parse(reader.GetString(16)));

    private static VideoSpeechAudioBuffer ReadBuffer(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            DateTimeOffset.Parse(reader.GetString(10)),
            DateTimeOffset.Parse(reader.GetString(11)));

    private static VideoSpeechCachedSegment ReadSegment(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetDouble(3),
            reader.GetDouble(4),
            reader.GetString(5),
            reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetDouble(11),
            reader.GetDouble(12),
            DateTimeOffset.Parse(reader.GetString(13)));

    private static void AddSessionParameters(SqliteCommand command, VideoSpeechSessionStatus session)
    {
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$status", session.Status);
        command.Parameters.AddWithValue("$profile_id", session.ProfileId);
        command.Parameters.AddWithValue("$session_id", session.SessionId);
        command.Parameters.AddWithValue("$source_url", session.SourceUrl);
        command.Parameters.AddWithValue("$platform", session.Platform);
        command.Parameters.AddWithValue("$video_id", session.VideoId);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$duration_seconds", session.DurationSeconds);
        command.Parameters.AddWithValue("$language", session.Language);
        command.Parameters.AddWithValue("$provider", session.Provider);
        command.Parameters.AddWithValue("$captions_used", session.CaptionsUsed ? 1 : 0);
        command.Parameters.AddWithValue("$segment_count", session.SegmentCount);
        command.Parameters.AddWithValue("$error_code", session.ErrorCode);
        command.Parameters.AddWithValue("$error_message", session.ErrorMessage);
        command.Parameters.AddWithValue("$created_at", session.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", session.UpdatedAt.ToString("O"));
    }

    private static object ToDbValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
