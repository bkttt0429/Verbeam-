using System.Text.Json;
using System.Text;
using Verbeam.Core.Models;
using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

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

    public async Task<OcrCachedResult?> GetCachedResultAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, image_hash, image_mime_type, provider, engine, engine_model_version,
                   language, normalize_whitespace, correction_hash, raw_text, corrected_text,
                   blocks_json, document_json, corrections_json, latency_ms, created_at,
                   last_used_at, use_count, detection_json
            FROM ocr_results
            WHERE key = $key
            """;
        command.Parameters.AddWithValue("$key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var value = ReadCachedResult(reader);
        await reader.DisposeAsync();

        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE ocr_results
            SET last_used_at = $last_used_at,
                use_count = use_count + 1
            WHERE key = $key
            """;
        updateCommand.Parameters.AddWithValue("$key", key);
        updateCommand.Parameters.AddWithValue("$last_used_at", DateTimeOffset.UtcNow.ToString("O"));
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        return value;
    }

    public async Task SetCachedResultAsync(
        OcrCachedResult entry,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_results (
                key, image_hash, image_mime_type, provider, engine, engine_model_version,
                language, normalize_whitespace, correction_hash, raw_text, corrected_text,
                blocks_json, document_json, corrections_json, latency_ms, created_at,
                last_used_at, use_count, detection_json
            )
            VALUES (
                $key, $image_hash, $image_mime_type, $provider, $engine, $engine_model_version,
                $language, $normalize_whitespace, $correction_hash, $raw_text, $corrected_text,
                $blocks_json, $document_json, $corrections_json, $latency_ms, $created_at,
                $last_used_at, $use_count, $detection_json
            )
            ON CONFLICT(key) DO UPDATE SET
                image_hash = excluded.image_hash,
                image_mime_type = excluded.image_mime_type,
                provider = excluded.provider,
                engine = excluded.engine,
                engine_model_version = excluded.engine_model_version,
                language = excluded.language,
                normalize_whitespace = excluded.normalize_whitespace,
                correction_hash = excluded.correction_hash,
                raw_text = excluded.raw_text,
                corrected_text = excluded.corrected_text,
                blocks_json = excluded.blocks_json,
                document_json = excluded.document_json,
                corrections_json = excluded.corrections_json,
                latency_ms = excluded.latency_ms,
                last_used_at = excluded.last_used_at,
                detection_json = excluded.detection_json
            """;

        command.Parameters.AddWithValue("$key", entry.Key);
        command.Parameters.AddWithValue("$image_hash", entry.ImageHash);
        command.Parameters.AddWithValue("$image_mime_type", entry.ImageMimeType);
        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$engine", entry.Engine);
        command.Parameters.AddWithValue("$engine_model_version", entry.EngineModelVersion);
        command.Parameters.AddWithValue("$language", entry.Language);
        command.Parameters.AddWithValue("$normalize_whitespace", entry.NormalizeWhitespace ? 1 : 0);
        command.Parameters.AddWithValue("$correction_hash", entry.CorrectionHash);
        command.Parameters.AddWithValue("$raw_text", entry.RawText);
        command.Parameters.AddWithValue("$corrected_text", entry.CorrectedText);
        command.Parameters.AddWithValue("$blocks_json", JsonSerializer.Serialize(entry.Blocks, _jsonOptions));
        command.Parameters.AddWithValue("$document_json", JsonSerializer.Serialize(entry.Document, _jsonOptions));
        command.Parameters.AddWithValue("$corrections_json", JsonSerializer.Serialize(entry.AppliedCorrections, _jsonOptions));
        command.Parameters.AddWithValue("$latency_ms", entry.LatencyMs);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$last_used_at", entry.LastUsedAt.ToString("O"));
        command.Parameters.AddWithValue("$use_count", entry.UseCount);
        command.Parameters.AddWithValue("$detection_json", JsonSerializer.Serialize(entry.Detection, _jsonOptions));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddEventAsync(OcrEvent entry, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await EnsureProfileAsync(
            connection,
            entry.ProfileId,
            entry.Language,
            defaultProvider: entry.Provider,
            entry.CreatedAt,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_events (
                id, profile_id, session_id, image_hash, image_mime_type, language,
                provider, engine, raw_text, corrected_text, blocks_json, document_json, corrections_json,
                latency_ms, created_at
            )
            VALUES (
                $id, $profile_id, $session_id, $image_hash, $image_mime_type, $language,
                $provider, $engine, $raw_text, $corrected_text, $blocks_json, $document_json, $corrections_json,
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
        command.Parameters.AddWithValue("$document_json", JsonSerializer.Serialize(entry.Document ?? new OcrDocumentResult(), _jsonOptions));
        command.Parameters.AddWithValue("$corrections_json", JsonSerializer.Serialize(entry.AppliedCorrections, _jsonOptions));
        command.Parameters.AddWithValue("$latency_ms", entry.LatencyMs);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OcrEvent?> GetEventAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, session_id, image_hash, image_mime_type, language,
                   provider, engine, raw_text, corrected_text, blocks_json, document_json, corrections_json,
                   latency_ms, created_at
            FROM ocr_events
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", eventId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
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
                   provider, engine, raw_text, corrected_text, blocks_json, document_json, corrections_json,
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

    public async Task AddSmokeResultAsync(
        OcrSmokeTestRecord entry,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await EnsureProfileAsync(
            connection,
            entry.ProfileId,
            entry.Language,
            defaultProvider: entry.Provider,
            entry.CreatedAt,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_smoke_results (
                id, profile_id, session_id, language, provider, engine,
                content_type, preference, preprocessing_preset, ocr_event_id,
                expected_text, recognized_text, exact_match, contains_expected,
                similarity, edit_distance, latency_ms, structure_json, structure_assertion_json,
                succeeded, error_code, error_message, created_at
            )
            VALUES (
                $id, $profile_id, $session_id, $language, $provider, $engine,
                $content_type, $preference, $preprocessing_preset, $ocr_event_id,
                $expected_text, $recognized_text, $exact_match, $contains_expected,
                $similarity, $edit_distance, $latency_ms, $structure_json, $structure_assertion_json,
                $succeeded, $error_code, $error_message, $created_at
            )
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$profile_id", entry.ProfileId);
        command.Parameters.AddWithValue("$session_id", entry.SessionId);
        command.Parameters.AddWithValue("$language", entry.Language);
        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$engine", entry.Engine);
        command.Parameters.AddWithValue("$content_type", entry.ContentType);
        command.Parameters.AddWithValue("$preference", entry.Preference);
        command.Parameters.AddWithValue("$preprocessing_preset", entry.PreprocessingPreset);
        command.Parameters.AddWithValue("$ocr_event_id", entry.OcrEventId);
        command.Parameters.AddWithValue("$expected_text", entry.ExpectedText);
        command.Parameters.AddWithValue("$recognized_text", entry.RecognizedText);
        command.Parameters.AddWithValue("$exact_match", entry.ExactMatch ? 1 : 0);
        command.Parameters.AddWithValue("$contains_expected", entry.ContainsExpected ? 1 : 0);
        command.Parameters.AddWithValue("$similarity", entry.Similarity);
        command.Parameters.AddWithValue("$edit_distance", entry.EditDistance);
        command.Parameters.AddWithValue("$latency_ms", entry.LatencyMs);
        command.Parameters.AddWithValue("$structure_json", JsonSerializer.Serialize(entry.Structure, _jsonOptions));
        command.Parameters.AddWithValue("$structure_assertion_json", JsonSerializer.Serialize(entry.StructureAssertion, _jsonOptions));
        command.Parameters.AddWithValue("$succeeded", entry.Succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$error_code", entry.ErrorCode);
        command.Parameters.AddWithValue("$error_message", entry.ErrorMessage);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OcrSmokeTestRecord>> ListSmokeResultsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, session_id, language, provider, engine,
                   content_type, preference, preprocessing_preset, ocr_event_id,
                   expected_text, recognized_text, exact_match, contains_expected,
                   similarity, edit_distance, latency_ms, structure_json, structure_assertion_json,
                   succeeded, error_code, error_message, created_at
            FROM ocr_smoke_results
            WHERE profile_id = $profile_id
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var values = new List<OcrSmokeTestRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadSmokeResult(reader));
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
        await EnsureProfileAsync(
            connection,
            profileId,
            language,
            defaultProvider: Pick(request.Source, "ocr"),
            now,
            cancellationToken);

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

    public async Task<OcrCorrection?> UpdateCorrectionAsync(
        string correctionId,
        OcrCorrectionUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(correctionId))
        {
            throw new ArgumentException("correction id is required.", nameof(correctionId));
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        var current = await GetCorrectionByIdAsync(connection, correctionId.Trim(), cancellationToken);
        if (current is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ocr_corrections
            SET note = $note,
                priority = $priority,
                confidence = $confidence,
                source = $source,
                is_active = $is_active,
                updated_at = $updated_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$note", request.Note is null ? current.Note : request.Note.Trim());
        command.Parameters.AddWithValue("$priority", request.Priority ?? current.Priority);
        command.Parameters.AddWithValue("$confidence", Math.Clamp(request.Confidence ?? current.Confidence, 0.0, 1.0));
        command.Parameters.AddWithValue("$source", Pick(request.Source, current.Source));
        command.Parameters.AddWithValue("$is_active", (request.IsActive ?? current.IsActive) ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetCorrectionByIdAsync(connection, current.Id, cancellationToken);
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

    private static async Task<OcrCorrection?> GetCorrectionByIdAsync(
        SqliteConnection connection,
        string correctionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, language, wrong_text, corrected_text, note, priority,
                   confidence, source, is_active, created_at, updated_at, last_used_at, use_count
            FROM ocr_corrections
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", correctionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCorrection(reader) : null;
    }

    private OcrEvent ReadEvent(SqliteDataReader reader)
    {
        var blocks = JsonSerializer.Deserialize<OcrTextBlock[]>(reader.GetString(10), _jsonOptions)
            ?? Array.Empty<OcrTextBlock>();
        var document = JsonSerializer.Deserialize<OcrDocumentResult>(reader.GetString(11), _jsonOptions)
            ?? new OcrDocumentResult();
        var corrections = JsonSerializer.Deserialize<AppliedOcrCorrection[]>(reader.GetString(12), _jsonOptions)
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
            reader.GetInt64(13),
            DateTimeOffset.Parse(reader.GetString(14)),
            document);
    }

    private OcrCachedResult ReadCachedResult(SqliteDataReader reader)
    {
        var blocks = JsonSerializer.Deserialize<OcrTextBlock[]>(reader.GetString(11), _jsonOptions)
            ?? Array.Empty<OcrTextBlock>();
        var document = JsonSerializer.Deserialize<OcrDocumentResult>(reader.GetString(12), _jsonOptions)
            ?? new OcrDocumentResult();
        var corrections = JsonSerializer.Deserialize<AppliedOcrCorrection[]>(reader.GetString(13), _jsonOptions)
            ?? Array.Empty<AppliedOcrCorrection>();

        return new OcrCachedResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7) == 1,
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            blocks,
            corrections,
            document,
            reader.GetInt64(14),
            DateTimeOffset.Parse(reader.GetString(15)),
            DateTimeOffset.Parse(reader.GetString(16)),
            reader.GetInt32(17))
        {
            Detection = ReadDetection(reader.IsDBNull(18) ? "{}" : reader.GetString(18))
        };
    }

    private OcrLanguageDetection ReadDetection(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return OcrLanguageDetection.Empty;
        }

        try
        {
            var value = JsonSerializer.Deserialize<OcrLanguageDetection>(json, _jsonOptions);
            if (value is null)
            {
                return OcrLanguageDetection.Empty;
            }

            // Rows written before this column existed deserialize with null members.
            return new OcrLanguageDetection(
                value.RequestedLanguage ?? string.Empty,
                value.ResolvedOcrLanguage ?? string.Empty,
                value.DetectedLanguage ?? string.Empty,
                value.LanguageConfidence,
                value.Candidates ?? Array.Empty<OcrLanguageCandidate>());
        }
        catch (JsonException)
        {
            return OcrLanguageDetection.Empty;
        }
    }

    private OcrSmokeTestRecord ReadSmokeResult(SqliteDataReader reader)
        => new OcrSmokeTestRecord(
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
            reader.GetString(11),
            reader.GetInt32(12) == 1,
            reader.GetInt32(13) == 1,
            reader.GetDouble(14),
            reader.GetInt32(15),
            reader.GetInt64(16),
            DateTimeOffset.Parse(reader.GetString(22)))
        {
            Structure = ReadStructureSummary(reader.GetString(17)),
            StructureAssertion = ReadStructureAssertion(reader.GetString(18)),
            Succeeded = reader.GetInt32(19) == 1,
            ErrorCode = reader.GetString(20),
            ErrorMessage = reader.GetString(21)
        };

    private OcrStructureSummary ReadStructureSummary(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OcrStructureSummary>(json, _jsonOptions) ?? OcrStructureSummary.Empty;
        }
        catch (JsonException)
        {
            return OcrStructureSummary.Empty;
        }
    }

    private OcrStructureAssertion ReadStructureAssertion(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OcrStructureAssertion>(json, _jsonOptions) ?? OcrStructureAssertion.Empty;
        }
        catch (JsonException)
        {
            return OcrStructureAssertion.Empty;
        }
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
