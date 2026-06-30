using System.Text.Json;
using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class SqliteDocumentJobStore : IDocumentJobStore
{
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteDocumentJobStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task AddJobAsync(
        DocumentJobStatus job,
        DocumentJobRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await EnsureProfileAsync(connection, job.ProfileId, request.Source ?? string.Empty, request.TranslationProvider ?? string.Empty, job.CreatedAt, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO document_jobs (
                id, status, profile_id, session_id, source_kind, input_file_name,
                input_mime_type, input_hash, stage, total_units, completed_units,
                progress, artifact_count, warning_count, error_code, error_message,
                request_json, artifacts_json, warnings_json, created_at, started_at,
                completed_at, updated_at
            )
            VALUES (
                $id, $status, $profile_id, $session_id, $source_kind, $input_file_name,
                $input_mime_type, $input_hash, $stage, $total_units, $completed_units,
                $progress, $artifact_count, $warning_count, $error_code, $error_message,
                $request_json, $artifacts_json, $warnings_json, $created_at, $started_at,
                $completed_at, $updated_at
            )
            """;
        AddJobParameters(command, job);
        command.Parameters.AddWithValue("$request_json", JsonSerializer.Serialize(request, _jsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DocumentJobStatus?> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectJobSql + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader, _jsonOptions) : null;
    }

    public async Task<DocumentJobRequest?> GetRequestAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_json FROM document_jobs WHERE id = $id";
        command.Parameters.AddWithValue("$id", jobId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DocumentJobRequest>(json, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DocumentJobStatus>> ListJobsAsync(
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
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var jobs = new List<DocumentJobStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader, _jsonOptions));
        }

        return jobs;
    }

    public async Task UpdateJobAsync(
        DocumentJobStatus job,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE document_jobs
            SET status = $status,
                profile_id = $profile_id,
                session_id = $session_id,
                source_kind = $source_kind,
                input_file_name = $input_file_name,
                input_mime_type = $input_mime_type,
                input_hash = $input_hash,
                stage = $stage,
                total_units = $total_units,
                completed_units = $completed_units,
                progress = $progress,
                artifact_count = $artifact_count,
                warning_count = $warning_count,
                error_code = $error_code,
                error_message = $error_message,
                artifacts_json = $artifacts_json,
                warnings_json = $warnings_json,
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
            INSERT INTO document_job_events (job_id, type, payload_json, created_at)
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

    public async Task<IReadOnlyList<DocumentJobEvent>> ListEventsAsync(
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
            FROM document_job_events
            WHERE job_id = $job_id AND sequence > $after_sequence
            ORDER BY sequence
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$after_sequence", Math.Max(0, afterSequence));
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var events = new List<DocumentJobEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new DocumentJobEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return events;
    }

    private const string SelectJobSql = """
        SELECT id, status, profile_id, session_id, source_kind, input_file_name,
               input_mime_type, input_hash, stage, total_units, completed_units,
               progress, artifact_count, warning_count, error_code, error_message,
               artifacts_json, warnings_json, created_at, started_at, completed_at, updated_at
        FROM document_jobs

        """;

    private static DocumentJobStatus ReadJob(SqliteDataReader reader, JsonSerializerOptions jsonOptions)
    {
        var artifacts = DeserializeArray<DocumentJobArtifact>(reader.GetString(16), jsonOptions);
        var warnings = DeserializeArray<DocumentJobWarning>(reader.GetString(17), jsonOptions);
        return new DocumentJobStatus(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetDouble(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetString(14),
            reader.GetString(15),
            DateTimeOffset.Parse(reader.GetString(18)),
            reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19)),
            reader.IsDBNull(20) ? null : DateTimeOffset.Parse(reader.GetString(20)),
            DateTimeOffset.Parse(reader.GetString(21)))
        {
            Artifacts = artifacts,
            Warnings = warnings
        };
    }

    private static IReadOnlyList<T> DeserializeArray<T>(string json, JsonSerializerOptions jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<T>();
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<T>>(json, jsonOptions) ?? Array.Empty<T>();
        }
        catch (JsonException)
        {
            return Array.Empty<T>();
        }
    }

    private void AddJobParameters(SqliteCommand command, DocumentJobStatus job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$profile_id", job.ProfileId);
        command.Parameters.AddWithValue("$session_id", job.SessionId);
        command.Parameters.AddWithValue("$source_kind", job.SourceKind);
        command.Parameters.AddWithValue("$input_file_name", job.InputFileName);
        command.Parameters.AddWithValue("$input_mime_type", job.InputMimeType);
        command.Parameters.AddWithValue("$input_hash", job.InputHash);
        command.Parameters.AddWithValue("$stage", job.Stage);
        command.Parameters.AddWithValue("$total_units", job.TotalUnits.HasValue ? job.TotalUnits.Value : DBNull.Value);
        command.Parameters.AddWithValue("$completed_units", job.CompletedUnits);
        command.Parameters.AddWithValue("$progress", job.Progress);
        command.Parameters.AddWithValue("$artifact_count", job.ArtifactCount);
        command.Parameters.AddWithValue("$warning_count", job.WarningCount);
        command.Parameters.AddWithValue("$error_code", job.ErrorCode);
        command.Parameters.AddWithValue("$error_message", job.ErrorMessage);
        command.Parameters.AddWithValue("$artifacts_json", JsonSerializer.Serialize(job.Artifacts, _jsonOptions));
        command.Parameters.AddWithValue("$warnings_json", JsonSerializer.Serialize(job.Warnings, _jsonOptions));
        command.Parameters.AddWithValue("$created_at", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$started_at", ToDbValue(job.StartedAt));
        command.Parameters.AddWithValue("$completed_at", ToDbValue(job.CompletedAt));
        command.Parameters.AddWithValue("$updated_at", job.UpdatedAt.ToString("O"));
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
                id, display_name, source_language, target_language, default_mode,
                default_provider, created_at, updated_at
            )
            VALUES (
                $id, $display_name, $source_language, $target_language, $default_mode,
                $default_provider, $created_at, $updated_at
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
