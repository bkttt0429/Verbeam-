using System.Text.Json;
using Verbeam.Core.Models;
using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

public sealed class SqliteOcrJobStore : IOcrJobStore
{
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteOcrJobStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task AddJobAsync(
        OcrJobStatus job,
        OcrJobRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await EnsureProfileAsync(connection, job.ProfileId, job.Language, job.Provider, job.CreatedAt, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_jobs (
                id, status, profile_id, session_id, image_hash, image_mime_type,
                language, provider, engine, block_count, progress, result_event_id,
                cache_hit, error_code, error_message, request_json,
                created_at, started_at, completed_at, updated_at,
                stage, estimated_duration_ms
            )
            VALUES (
                $id, $status, $profile_id, $session_id, $image_hash, $image_mime_type,
                $language, $provider, $engine, $block_count, $progress, $result_event_id,
                $cache_hit, $error_code, $error_message, $request_json,
                $created_at, $started_at, $completed_at, $updated_at,
                $stage, $estimated_duration_ms
            )
            """;
        AddJobParameters(command, job);
        command.Parameters.AddWithValue("$request_json", JsonSerializer.Serialize(request, _jsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OcrJobStatus?> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectJobSql + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async Task<IReadOnlyList<OcrJobStatus>> ListJobsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectJobSql + """
            WHERE profile_id = $profile_id
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var jobs = new List<OcrJobStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    public async Task UpdateJobAsync(
        OcrJobStatus job,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ocr_jobs
            SET status = $status,
                profile_id = $profile_id,
                session_id = $session_id,
                image_hash = $image_hash,
                image_mime_type = $image_mime_type,
                language = $language,
                provider = $provider,
                engine = $engine,
                block_count = $block_count,
                progress = $progress,
                result_event_id = $result_event_id,
                cache_hit = $cache_hit,
                error_code = $error_code,
                error_message = $error_message,
                started_at = $started_at,
                completed_at = $completed_at,
                updated_at = $updated_at,
                stage = $stage,
                estimated_duration_ms = $estimated_duration_ms
            WHERE id = $id
            """;
        AddJobParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> AddEventAsync(
        string jobId,
        string type,
        object payload,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_job_events (job_id, type, payload_json, created_at)
            VALUES ($job_id, $type, $payload_json, $created_at);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(payload, _jsonOptions));
        command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<OcrJobEvent>> ListEventsAsync(
        string jobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, job_id, type, payload_json, created_at
            FROM ocr_job_events
            WHERE job_id = $job_id AND sequence > $after_sequence
            ORDER BY sequence
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$after_sequence", Math.Max(0, afterSequence));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var events = new List<OcrJobEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new OcrJobEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return events;
    }

    private const string SelectJobSql = """
        SELECT id, status, profile_id, session_id, image_hash, image_mime_type,
               language, provider, engine, block_count, progress, result_event_id,
               cache_hit, error_code, error_message, created_at, started_at,
               completed_at, updated_at, stage, estimated_duration_ms
        FROM ocr_jobs

        """;

    private static OcrJobStatus ReadJob(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetDouble(10),
            reader.GetString(11),
            reader.GetInt32(12) == 1,
            reader.GetString(13),
            reader.GetString(14),
            DateTimeOffset.Parse(reader.GetString(15)),
            reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : DateTimeOffset.Parse(reader.GetString(17)),
            DateTimeOffset.Parse(reader.GetString(18)))
        {
            Stage = reader.GetString(19),
            EstimatedDurationMs = reader.IsDBNull(20) ? null : reader.GetInt64(20)
        };

    private static void AddJobParameters(SqliteCommand command, OcrJobStatus job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$profile_id", job.ProfileId);
        command.Parameters.AddWithValue("$session_id", job.SessionId);
        command.Parameters.AddWithValue("$image_hash", job.ImageHash);
        command.Parameters.AddWithValue("$image_mime_type", job.ImageMimeType);
        command.Parameters.AddWithValue("$language", job.Language);
        command.Parameters.AddWithValue("$provider", job.Provider);
        command.Parameters.AddWithValue("$engine", job.Engine);
        command.Parameters.AddWithValue("$block_count", job.BlockCount);
        command.Parameters.AddWithValue("$progress", job.Progress);
        command.Parameters.AddWithValue("$result_event_id", job.ResultEventId);
        command.Parameters.AddWithValue("$cache_hit", job.CacheHit ? 1 : 0);
        command.Parameters.AddWithValue("$error_code", job.ErrorCode);
        command.Parameters.AddWithValue("$error_message", job.ErrorMessage);
        command.Parameters.AddWithValue("$created_at", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$started_at", ToDbValue(job.StartedAt));
        command.Parameters.AddWithValue("$completed_at", ToDbValue(job.CompletedAt));
        command.Parameters.AddWithValue("$updated_at", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$stage", job.Stage);
        command.Parameters.AddWithValue("$estimated_duration_ms", job.EstimatedDurationMs.HasValue ? (object)job.EstimatedDurationMs.Value : DBNull.Value);
    }

    private static async Task EnsureProfileAsync(
        SqliteConnection connection,
        string profileId,
        string sourceLanguage,
        string defaultProvider,
        DateTimeOffset now,
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
        command.Parameters.AddWithValue("$id", profileId);
        command.Parameters.AddWithValue("$display_name", profileId);
        command.Parameters.AddWithValue("$source_language", sourceLanguage);
        command.Parameters.AddWithValue("$target_language", string.Empty);
        command.Parameters.AddWithValue("$default_mode", string.Empty);
        command.Parameters.AddWithValue("$default_provider", defaultProvider);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object ToDbValue(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToString("O") : DBNull.Value;
}
