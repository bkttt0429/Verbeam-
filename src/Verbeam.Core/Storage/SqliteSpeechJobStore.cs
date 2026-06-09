using System.Text.Json;
using Verbeam.Core.Models;
using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

public sealed class SqliteSpeechJobStore : ISpeechJobStore
{
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteSpeechJobStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task AddJobAsync(
        SpeechJobStatus job,
        SpeechJobRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO speech_jobs (
                id, status, profile_id, session_id, source_kind, source_uri,
                language, provider, engine, captions_used, segment_count, progress,
                result_event_id, error_code, error_message, request_json,
                created_at, started_at, completed_at, updated_at
            )
            VALUES (
                $id, $status, $profile_id, $session_id, $source_kind, $source_uri,
                $language, $provider, $engine, $captions_used, $segment_count, $progress,
                $result_event_id, $error_code, $error_message, $request_json,
                $created_at, $started_at, $completed_at, $updated_at
            )
            """;
        AddJobParameters(command, job);
        command.Parameters.AddWithValue("$request_json", JsonSerializer.Serialize(request, _jsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SpeechJobStatus?> GetJobAsync(
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

    public async Task<IReadOnlyList<SpeechJobStatus>> ListJobsAsync(
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

        var jobs = new List<SpeechJobStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    public async Task UpdateJobAsync(
        SpeechJobStatus job,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE speech_jobs
            SET status = $status,
                profile_id = $profile_id,
                session_id = $session_id,
                source_kind = $source_kind,
                source_uri = $source_uri,
                language = $language,
                provider = $provider,
                engine = $engine,
                captions_used = $captions_used,
                segment_count = $segment_count,
                progress = $progress,
                result_event_id = $result_event_id,
                error_code = $error_code,
                error_message = $error_message,
                started_at = $started_at,
                completed_at = $completed_at,
                updated_at = $updated_at
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
            INSERT INTO speech_job_events (job_id, type, payload_json, created_at)
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

    public async Task<IReadOnlyList<SpeechJobEvent>> ListEventsAsync(
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
            FROM speech_job_events
            WHERE job_id = $job_id AND sequence > $after_sequence
            ORDER BY sequence
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$after_sequence", Math.Max(0, afterSequence));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var events = new List<SpeechJobEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new SpeechJobEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return events;
    }

    private const string SelectJobSql = """
        SELECT id, status, profile_id, session_id, source_kind, source_uri,
               language, provider, engine, captions_used, segment_count, progress,
               result_event_id, error_code, error_message, created_at, started_at,
               completed_at, updated_at
        FROM speech_jobs
        
        """;

    private static SpeechJobStatus ReadJob(SqliteDataReader reader)
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
            reader.GetInt32(9) == 1,
            reader.GetInt32(10),
            reader.GetDouble(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            DateTimeOffset.Parse(reader.GetString(15)),
            reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : DateTimeOffset.Parse(reader.GetString(17)),
            DateTimeOffset.Parse(reader.GetString(18)));

    private static void AddJobParameters(SqliteCommand command, SpeechJobStatus job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$profile_id", job.ProfileId);
        command.Parameters.AddWithValue("$session_id", job.SessionId);
        command.Parameters.AddWithValue("$source_kind", job.SourceKind);
        command.Parameters.AddWithValue("$source_uri", job.SourceUri);
        command.Parameters.AddWithValue("$language", job.Language);
        command.Parameters.AddWithValue("$provider", job.Provider);
        command.Parameters.AddWithValue("$engine", job.Engine);
        command.Parameters.AddWithValue("$captions_used", job.CaptionsUsed ? 1 : 0);
        command.Parameters.AddWithValue("$segment_count", job.SegmentCount);
        command.Parameters.AddWithValue("$progress", job.Progress);
        command.Parameters.AddWithValue("$result_event_id", job.ResultEventId);
        command.Parameters.AddWithValue("$error_code", job.ErrorCode);
        command.Parameters.AddWithValue("$error_message", job.ErrorMessage);
        command.Parameters.AddWithValue("$created_at", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$started_at", ToDbValue(job.StartedAt));
        command.Parameters.AddWithValue("$completed_at", ToDbValue(job.CompletedAt));
        command.Parameters.AddWithValue("$updated_at", job.UpdatedAt.ToString("O"));
    }

    private static object ToDbValue(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToString("O") : DBNull.Value;
}
