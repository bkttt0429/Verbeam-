using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class SqliteMemoryMaintenanceJobStore : IMemoryMaintenanceJobStore
{
    private readonly string _databasePath;

    public SqliteMemoryMaintenanceJobStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<string> EnqueueAsync(
        string jobKind,
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_maintenance_jobs (
                id,
                job_kind,
                "status",
                profile_id,
                session_id,
                source_language,
                target_language,
                mode,
                attempts,
                error_message,
                created_at,
                updated_at,
                started_at,
                completed_at
            )
            VALUES (
                $id,
                $job_kind,
                'pending',
                $profile_id,
                $session_id,
                $source_language,
                $target_language,
                $mode,
                0,
                '',
                $created_at,
                $updated_at,
                NULL,
                NULL
            )
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$job_kind", jobKind.Trim());
        command.Parameters.AddWithValue("$profile_id", profileId.Trim());
        command.Parameters.AddWithValue("$session_id", sessionId.Trim());
        command.Parameters.AddWithValue("$source_language", sourceLanguage.Trim());
        command.Parameters.AddWithValue("$target_language", targetLanguage.Trim());
        command.Parameters.AddWithValue("$mode", mode.Trim());
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return id;
    }

    public async Task<IReadOnlyList<MemoryMaintenanceJob>> ListAsync(
        string? profileId = null,
        string? status = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            conditions.Add("profile_id = $profile_id");
            command.Parameters.AddWithValue("$profile_id", profileId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            conditions.Add("\"status\" = $status");
            command.Parameters.AddWithValue("$status", status.Trim());
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.CommandText =
            SelectSql + "\n" +
            (conditions.Count == 0 ? string.Empty : "WHERE " + string.Join("\n  AND ", conditions) + "\n") +
            """
            ORDER BY created_at DESC
            LIMIT $limit
            """;

        var jobs = new List<MemoryMaintenanceJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    public async Task<int> CountAsync(
        string? profileId = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            conditions.Add("profile_id = $profile_id");
            command.Parameters.AddWithValue("$profile_id", profileId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            conditions.Add("\"status\" = $status");
            command.Parameters.AddWithValue("$status", status.Trim());
        }

        command.CommandText =
            "SELECT COUNT(*) FROM memory_maintenance_jobs\n" +
            (conditions.Count == 0 ? string.Empty : "WHERE " + string.Join("\n  AND ", conditions));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<MemoryMaintenanceJob>> ClaimAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.Subtract(staleAfter);
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = """
            SELECT id
            FROM memory_maintenance_jobs
            WHERE "status" = 'pending'
               OR ("status" = 'running' AND updated_at < $stale_before)
            ORDER BY created_at
            LIMIT $limit
            """;
        select.Parameters.AddWithValue("$stale_before", staleBefore.ToString("O"));
        select.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));

        var ids = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        foreach (var id in ids)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE memory_maintenance_jobs
                SET "status" = 'running',
                    attempts = attempts + 1,
                    started_at = COALESCE(started_at, $started_at),
                    updated_at = $updated_at
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$started_at", now.ToString("O"));
            update.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return [];
        }

        await using var readConnection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = readConnection.CreateCommand();
        var placeholders = ids.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText = SelectSql + $"\nWHERE id IN ({string.Join(", ", placeholders)}) ORDER BY created_at";
        for (var index = 0; index < ids.Count; index++)
        {
            command.Parameters.AddWithValue(placeholders[index], ids[index]);
        }

        var jobs = new List<MemoryMaintenanceJob>();
        await using var jobReader = await command.ExecuteReaderAsync(cancellationToken);
        while (await jobReader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(jobReader));
        }

        return jobs;
    }

    public async Task CompleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_maintenance_jobs
            SET "status" = 'completed',
                error_message = '',
                completed_at = $completed_at,
                updated_at = $updated_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$completed_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FailAsync(
        string id,
        string errorMessage,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_maintenance_jobs
            SET "status" = CASE WHEN attempts >= $max_attempts THEN 'failed' ELSE 'pending' END,
                error_message = $error_message,
                updated_at = $updated_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$max_attempts", Math.Max(1, maxAttempts));
        command.Parameters.AddWithValue("$error_message", (errorMessage ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SelectSql = """
        SELECT id, job_kind, "status", profile_id, session_id, source_language,
               target_language, mode, attempts, error_message, created_at,
               updated_at, started_at, completed_at
        FROM memory_maintenance_jobs
        """;

    private static MemoryMaintenanceJob ReadJob(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetString(9),
            DateTimeOffset.Parse(reader.GetString(10)),
            DateTimeOffset.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)));
}
