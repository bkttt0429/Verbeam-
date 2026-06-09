using System.Text.Json;
using Verbeam.Core.Models;
using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

public sealed class SqliteSpeechEventStore : ISpeechEventStore
{
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteSpeechEventStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task AddEventAsync(SpeechEvent entry, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO speech_events (
                id, profile_id, session_id, source_kind, source_uri, audio_hash,
                audio_mime_type, language, provider, engine, text, segments_json,
                captions_used, latency_ms, created_at
            )
            VALUES (
                $id, $profile_id, $session_id, $source_kind, $source_uri, $audio_hash,
                $audio_mime_type, $language, $provider, $engine, $text, $segments_json,
                $captions_used, $latency_ms, $created_at
            )
            """;

        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$profile_id", entry.ProfileId);
        command.Parameters.AddWithValue("$session_id", entry.SessionId);
        command.Parameters.AddWithValue("$source_kind", entry.SourceKind);
        command.Parameters.AddWithValue("$source_uri", entry.SourceUri);
        command.Parameters.AddWithValue("$audio_hash", entry.AudioHash);
        command.Parameters.AddWithValue("$audio_mime_type", entry.AudioMimeType);
        command.Parameters.AddWithValue("$language", entry.Language);
        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$engine", entry.Engine);
        command.Parameters.AddWithValue("$text", entry.Text);
        command.Parameters.AddWithValue("$segments_json", JsonSerializer.Serialize(entry.Segments, _jsonOptions));
        command.Parameters.AddWithValue("$captions_used", entry.CaptionsUsed ? 1 : 0);
        command.Parameters.AddWithValue("$latency_ms", entry.LatencyMs);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SpeechEvent>> ListEventsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, session_id, source_kind, source_uri, audio_hash,
                   audio_mime_type, language, provider, engine, text, segments_json,
                   captions_used, latency_ms, created_at
            FROM speech_events
            WHERE profile_id = $profile_id
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var values = new List<SpeechEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadEvent(reader));
        }

        return values;
    }

    private SpeechEvent ReadEvent(SqliteDataReader reader)
    {
        var segments = JsonSerializer.Deserialize<SpeechSegment[]>(reader.GetString(11), _jsonOptions)
            ?? Array.Empty<SpeechSegment>();

        return new SpeechEvent(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            segments,
            reader.GetInt32(12) == 1,
            reader.GetInt64(13),
            DateTimeOffset.Parse(reader.GetString(14)));
    }
}
