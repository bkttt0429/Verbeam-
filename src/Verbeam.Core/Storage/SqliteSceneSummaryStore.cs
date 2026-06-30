using Verbeam.Core.Models;
using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

public sealed class SqliteSceneSummaryStore : ISceneSummaryStore
{
    private readonly string _databasePath;

    public SqliteSceneSummaryStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<SceneSummary> AddOrUpdateAsync(
        SceneSummaryUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var summaryText = PickRequired(request.SummaryText, "summaryText");
        var startEventId = PickRequired(request.StartEventId, "startEventId");
        var endEventId = PickRequired(request.EndEventId, "endEventId");
        var now = DateTimeOffset.UtcNow;

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        var start = await GetEventBoundaryAsync(connection, startEventId, cancellationToken)
            ?? throw new ArgumentException("startEventId was not found.");
        var end = await GetEventBoundaryAsync(connection, endEventId, cancellationToken)
            ?? throw new ArgumentException("endEventId was not found.");

        var profileId = Pick(request.Profile, start.ProfileId);
        var sessionId = Pick(request.SessionId, start.SessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required for scene summaries.");
        }

        ValidateBoundary(start, profileId, sessionId, "startEventId");
        ValidateBoundary(end, profileId, sessionId, "endEventId");

        var id = !string.IsNullOrWhiteSpace(request.Id)
            ? request.Id.Trim()
            : await FindExistingIdAsync(connection, profileId, sessionId, startEventId, endEventId, cancellationToken)
                ?? Guid.NewGuid().ToString("N");

        var existing = await GetByIdAsync(connection, id, cancellationToken);
        if (existing is null)
        {
            await InsertAsync(
                connection,
                id,
                sessionId,
                profileId,
                summaryText,
                startEventId,
                endEventId,
                now,
                cancellationToken);
        }
        else
        {
            if (!string.Equals(existing.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("scene summary id belongs to another profile or session.");
            }

            await UpdateAsync(
                connection,
                id,
                summaryText,
                startEventId,
                endEventId,
                now,
                cancellationToken);
        }

        return await GetByIdAsync(connection, id, cancellationToken)
            ?? throw new InvalidOperationException("Scene summary was not stored.");
    }

    public async Task<SceneSummary?> GetLatestAsync(
        string profileId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, profile_id, summary_text, start_event_id,
                   end_event_id, created_at, updated_at
            FROM scene_summaries
            WHERE profile_id = $profile_id
              AND session_id = $session_id
            ORDER BY updated_at DESC, created_at DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$session_id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSummary(reader) : null;
    }

    public async Task<IReadOnlyList<SceneSummary>> ListAsync(
        string profileId,
        string? sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var hasSession = !string.IsNullOrWhiteSpace(sessionId);
        command.CommandText = hasSession
            ? """
              SELECT id, session_id, profile_id, summary_text, start_event_id,
                     end_event_id, created_at, updated_at
              FROM scene_summaries
              WHERE profile_id = $profile_id
                AND session_id = $session_id
              ORDER BY updated_at DESC, created_at DESC
              LIMIT $limit
              """
            : """
              SELECT id, session_id, profile_id, summary_text, start_event_id,
                     end_event_id, created_at, updated_at
              FROM scene_summaries
              WHERE profile_id = $profile_id
              ORDER BY updated_at DESC, created_at DESC
              LIMIT $limit
              """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        if (hasSession)
        {
            command.Parameters.AddWithValue("$session_id", sessionId!.Trim());
        }

        var values = new List<SceneSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadSummary(reader));
        }

        return values;
    }

    private static async Task<EventBoundary?> GetEventBoundaryAsync(
        SqliteConnection connection,
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, IFNULL(session_id, ''), created_at
            FROM translation_events
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", eventId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EventBoundary(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)))
            : null;
    }

    private static async Task<string?> FindExistingIdAsync(
        SqliteConnection connection,
        string profileId,
        string sessionId,
        string startEventId,
        string endEventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM scene_summaries
            WHERE profile_id = $profile_id
              AND session_id = $session_id
              AND start_event_id = $start_event_id
              AND end_event_id = $end_event_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$start_event_id", startEventId);
        command.Parameters.AddWithValue("$end_event_id", endEventId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<SceneSummary?> GetByIdAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, profile_id, summary_text, start_event_id,
                   end_event_id, created_at, updated_at
            FROM scene_summaries
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSummary(reader) : null;
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        string id,
        string sessionId,
        string profileId,
        string summaryText,
        string startEventId,
        string endEventId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scene_summaries (
                id, session_id, profile_id, summary_text, start_event_id,
                end_event_id, created_at, updated_at
            )
            VALUES (
                $id, $session_id, $profile_id, $summary_text, $start_event_id,
                $end_event_id, $created_at, $updated_at
            )
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$summary_text", summaryText);
        command.Parameters.AddWithValue("$start_event_id", startEventId);
        command.Parameters.AddWithValue("$end_event_id", endEventId);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAsync(
        SqliteConnection connection,
        string id,
        string summaryText,
        string startEventId,
        string endEventId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scene_summaries
            SET summary_text = $summary_text,
                start_event_id = $start_event_id,
                end_event_id = $end_event_id,
                updated_at = $updated_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$summary_text", summaryText);
        command.Parameters.AddWithValue("$start_event_id", startEventId);
        command.Parameters.AddWithValue("$end_event_id", endEventId);
        command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SceneSummary ReadSummary(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7)));

    private static void ValidateBoundary(
        EventBoundary boundary,
        string profileId,
        string sessionId,
        string name)
    {
        if (!string.Equals(boundary.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(boundary.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{name} does not match the requested profile/session.");
        }
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string PickRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.");
        }

        return value.Trim();
    }

    private sealed record EventBoundary(
        string ProfileId,
        string SessionId,
        DateTimeOffset CreatedAt);
}
