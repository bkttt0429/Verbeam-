using System.Text.Json;
using System.Text;
using YomiBridge.Core.Models;
using Microsoft.Data.Sqlite;

namespace YomiBridge.Core.Storage;

public sealed class SqliteOcrMemoryStore : IOcrMemoryStore
{
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteOcrMemoryStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task AddEventAsync(OcrEvent entry, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_events (
                id, profile_id, session_id, image_hash, image_mime_type, language,
                provider, engine, raw_text, corrected_text, blocks_json, corrections_json,
                latency_ms, created_at
            )
            VALUES (
                $id, $profile_id, $session_id, $image_hash, $image_mime_type, $language,
                $provider, $engine, $raw_text, $corrected_text, $blocks_json, $corrections_json,
                $latency_ms, $created_at
            )
            """;

        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$profile_id", entry.ProfileId);
        command.Parameters.AddWithValue("$session_id", entry.SessionId);
        command.Parameters.AddWithValue("$image_hash", entry.ImageHash);
        command.Parameters.AddWithValue("$image_mime_type", entry.ImageMimeType);
        command.Parameters.AddWithValue("$language", entry.Language);
        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$engine", entry.Engine);
        command.Parameters.AddWithValue("$raw_text", entry.RawText);
        command.Parameters.AddWithValue("$corrected_text", entry.CorrectedText);
        command.Parameters.AddWithValue("$blocks_json", JsonSerializer.Serialize(entry.Blocks, _jsonOptions));
        command.Parameters.AddWithValue("$corrections_json", JsonSerializer.Serialize(entry.AppliedCorrections, _jsonOptions));
        command.Parameters.AddWithValue("$latency_ms", entry.LatencyMs);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OcrEvent>> ListEventsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, session_id, image_hash, image_mime_type, language,
                   provider, engine, raw_text, corrected_text, blocks_json, corrections_json,
                   latency_ms, created_at
            FROM ocr_events
            WHERE profile_id = $profile_id
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var values = new List<OcrEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadEvent(reader));
        }

        return values;
    }

    public async Task<OcrCorrection> AddOrUpdateCorrectionAsync(
        OcrCorrectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var profileId = Pick(request.Profile, "default");
        var language = Pick(request.Language, "ja");
        var wrongText = PickRequired(request.WrongText, "wrongText");
        var correctedText = PickRequired(request.CorrectedText, "correctedText");
        var normalized = NormalizeKey(wrongText);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_corrections (
                id, profile_id, language, wrong_text, wrong_text_normalized, corrected_text,
                note, priority, confidence, source, is_active, created_at, updated_at
            )
            VALUES (
                $id, $profile_id, $language, $wrong_text, $wrong_text_normalized, $corrected_text,
                $note, $priority, $confidence, $source, 1, $created_at, $updated_at
            )
            ON CONFLICT(profile_id, language, wrong_text_normalized, corrected_text) DO UPDATE SET
                wrong_text = excluded.wrong_text,
                note = excluded.note,
                priority = excluded.priority,
                confidence = excluded.confidence,
                source = excluded.source,
                is_active = 1,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$wrong_text", wrongText);
        command.Parameters.AddWithValue("$wrong_text_normalized", normalized);
        command.Parameters.AddWithValue("$corrected_text", correctedText);
        command.Parameters.AddWithValue("$note", request.Note?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$priority", request.Priority ?? 0);
        command.Parameters.AddWithValue("$confidence", Math.Clamp(request.Confidence ?? 1.0, 0.0, 1.0));
        command.Parameters.AddWithValue("$source", Pick(request.Source, "user"));
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetCorrectionByUniqueKeyAsync(
            connection,
            profileId,
            language,
            normalized,
            correctedText,
            cancellationToken);
    }

    public async Task<IReadOnlyList<OcrCorrection>> ListCorrectionsAsync(
        string profileId,
        string language,
        int limit,
        bool activeOnly,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = activeOnly
            ? """
              SELECT id, profile_id, language, wrong_text, corrected_text, note, priority,
                     confidence, source, is_active, created_at, updated_at, last_used_at, use_count
              FROM ocr_corrections
              WHERE profile_id = $profile_id
                AND language = $language
                AND is_active = 1
              ORDER BY priority DESC, confidence DESC, length(wrong_text) DESC, updated_at DESC
              LIMIT $limit
              """
            : """
              SELECT id, profile_id, language, wrong_text, corrected_text, note, priority,
                     confidence, source, is_active, created_at, updated_at, last_used_at, use_count
              FROM ocr_corrections
              WHERE profile_id = $profile_id
                AND language = $language
              ORDER BY priority DESC, confidence DESC, length(wrong_text) DESC, updated_at DESC
              LIMIT $limit
              """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var values = new List<OcrCorrection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadCorrection(reader));
        }

        return values;
    }

    public async Task RecordCorrectionUseAsync(
        IReadOnlyList<string> correctionIds,
        CancellationToken cancellationToken = default)
    {
        if (correctionIds.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        foreach (var correctionId in correctionIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ocr_corrections
                SET use_count = use_count + 1,
                    last_used_at = $last_used_at,
                    updated_at = $last_used_at
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", correctionId);
            command.Parameters.AddWithValue("$last_used_at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<OcrCorrection> GetCorrectionByUniqueKeyAsync(
        SqliteConnection connection,
        string profileId,
        string language,
        string wrongTextNormalized,
        string correctedText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, language, wrong_text, corrected_text, note, priority,
                   confidence, source, is_active, created_at, updated_at, last_used_at, use_count
            FROM ocr_corrections
            WHERE profile_id = $profile_id
              AND language = $language
              AND wrong_text_normalized = $wrong_text_normalized
              AND corrected_text = $corrected_text
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$wrong_text_normalized", wrongTextNormalized);
        command.Parameters.AddWithValue("$corrected_text", correctedText);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadCorrection(reader);
        }

        throw new InvalidOperationException("OCR correction was not stored.");
    }

    private OcrEvent ReadEvent(SqliteDataReader reader)
    {
        var blocks = JsonSerializer.Deserialize<OcrTextBlock[]>(reader.GetString(10), _jsonOptions)
            ?? Array.Empty<OcrTextBlock>();
        var corrections = JsonSerializer.Deserialize<AppliedOcrCorrection[]>(reader.GetString(11), _jsonOptions)
            ?? Array.Empty<AppliedOcrCorrection>();

        return new OcrEvent(
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
            blocks,
            corrections,
            reader.GetInt64(12),
            DateTimeOffset.Parse(reader.GetString(13)));
    }

    private static OcrCorrection ReadCorrection(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetDouble(7),
            reader.GetString(8),
            reader.GetInt32(9) == 1,
            DateTimeOffset.Parse(reader.GetString(10)),
            DateTimeOffset.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            reader.GetInt32(13));

    public static string NormalizeKey(string text)
        => text.Normalize(NormalizationForm.FormKC).Trim();

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
}
