using System.Text;
using System.Text.Json;
using YomiBridge.Core.Models;
using Microsoft.Data.Sqlite;

namespace YomiBridge.Core.Storage;

public sealed class SqliteMemoryStore : IMemoryStore
{
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteMemoryStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<MemoryItem> AddOrUpdateAsync(
        MemoryUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var profileId = Pick(request.Profile, "default");
        var memoryKind = Pick(request.MemoryKind, "translation").ToLowerInvariant();
        var sourceLanguage = Pick(request.Source, "ja");
        var targetLanguage = Pick(request.Target, "zh-TW");
        var sourceText = PickRequired(request.SourceText, "sourceText");
        var targetText = PickRequired(request.TargetText, "targetText");
        var normalized = NormalizeKey(sourceText);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var metadata = BuildMetadataJson(request.Origin, request, _jsonOptions);

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await EnsureProfileAsync(
            connection,
            profileId,
            sourceLanguage,
            targetLanguage,
            defaultMode: string.Empty,
            defaultProvider: string.Empty,
            now,
            cancellationToken);

        var existing = await FindByUniqueKeyAsync(
            connection,
            profileId,
            memoryKind,
            sourceLanguage,
            targetLanguage,
            normalized,
            cancellationToken);

        if (existing is null)
        {
            await InsertAsync(
                connection,
                id,
                profileId,
                memoryKind,
                sourceLanguage,
                targetLanguage,
                sourceText,
                normalized,
                targetText,
                request.Note?.Trim() ?? string.Empty,
                request.Priority ?? 0,
                Math.Clamp(request.Confidence ?? 1.0, 0.0, 1.0),
                tagsJson: "[]",
                metadata,
                now,
                cancellationToken);
        }
        else
        {
            await UpdateAsync(
                connection,
                existing.Id,
                sourceText,
                targetText,
                request.Note?.Trim() ?? existing.Note,
                request.Priority ?? existing.Priority,
                Math.Clamp(request.Confidence ?? existing.Confidence, 0.0, 1.0),
                tagsJson: existing.TagsJson,
                metadata,
                now,
                cancellationToken);
        }

        return await GetByUniqueKeyAsync(
            connection,
            profileId,
            memoryKind,
            sourceLanguage,
            targetLanguage,
            normalized,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryItem>> ListAsync(
        string profileId,
        string? memoryKind,
        int limit,
        bool activeOnly,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var hasKind = !string.IsNullOrWhiteSpace(memoryKind);
        command.CommandText = activeOnly
            ? hasKind
                ? """
                  SELECT id, profile_id, memory_kind, source_language, target_language,
                         source_text, target_text, note, priority, confidence,
                         tags_json, metadata_json, is_active, created_at, updated_at,
                         last_used_at, use_count
                  FROM memory_items
                  WHERE profile_id = $profile_id
                    AND memory_kind = $memory_kind
                    AND is_active = 1
                  ORDER BY priority DESC, confidence DESC, updated_at DESC
                  LIMIT $limit
                  """
                : """
                  SELECT id, profile_id, memory_kind, source_language, target_language,
                         source_text, target_text, note, priority, confidence,
                         tags_json, metadata_json, is_active, created_at, updated_at,
                         last_used_at, use_count
                  FROM memory_items
                  WHERE profile_id = $profile_id
                    AND is_active = 1
                  ORDER BY priority DESC, confidence DESC, updated_at DESC
                  LIMIT $limit
                  """
            : hasKind
                ? """
                  SELECT id, profile_id, memory_kind, source_language, target_language,
                         source_text, target_text, note, priority, confidence,
                         tags_json, metadata_json, is_active, created_at, updated_at,
                         last_used_at, use_count
                  FROM memory_items
                  WHERE profile_id = $profile_id
                    AND memory_kind = $memory_kind
                  ORDER BY is_active DESC, priority DESC, confidence DESC, updated_at DESC
                  LIMIT $limit
                  """
                : """
                  SELECT id, profile_id, memory_kind, source_language, target_language,
                         source_text, target_text, note, priority, confidence,
                         tags_json, metadata_json, is_active, created_at, updated_at,
                         last_used_at, use_count
                  FROM memory_items
                  WHERE profile_id = $profile_id
                  ORDER BY is_active DESC, priority DESC, confidence DESC, updated_at DESC
                  LIMIT $limit
                  """;

        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        if (hasKind)
        {
            command.Parameters.AddWithValue("$memory_kind", memoryKind!.Trim().ToLowerInvariant());
        }

        var values = new List<MemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadMemoryItem(reader));
        }

        return values;
    }

    public async Task<MemoryItem?> FindExactAsync(
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceText,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        return await FindByUniqueKeyAsync(
            connection,
            profileId,
            memoryKind.Trim().ToLowerInvariant(),
            sourceLanguage,
            targetLanguage,
            NormalizeKey(sourceText),
            cancellationToken);
    }

    public async Task RecordUseAsync(
        IReadOnlyList<string> memoryIds,
        CancellationToken cancellationToken = default)
    {
        if (memoryIds.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        foreach (var id in memoryIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE memory_items
                SET use_count = use_count + 1,
                    last_used_at = $last_used_at,
                    updated_at = $last_used_at
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$last_used_at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureProfileAsync(
        SqliteConnection connection,
        string profileId,
        string sourceLanguage,
        string targetLanguage,
        string defaultMode,
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
        command.Parameters.AddWithValue("$target_language", targetLanguage);
        command.Parameters.AddWithValue("$default_mode", defaultMode);
        command.Parameters.AddWithValue("$default_provider", defaultProvider);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        string id,
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceText,
        string sourceTextNormalized,
        string targetText,
        string note,
        int priority,
        double confidence,
        string tagsJson,
        string metadataJson,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_items (
                id, profile_id, memory_kind, source_language, target_language,
                source_text, source_text_normalized, target_text, note, priority,
                confidence, tags_json, metadata_json, is_active, created_at, updated_at
            )
            VALUES (
                $id, $profile_id, $memory_kind, $source_language, $target_language,
                $source_text, $source_text_normalized, $target_text, $note, $priority,
                $confidence, $tags_json, $metadata_json, 1, $created_at, $updated_at
            )
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$memory_kind", memoryKind);
        command.Parameters.AddWithValue("$source_language", sourceLanguage);
        command.Parameters.AddWithValue("$target_language", targetLanguage);
        command.Parameters.AddWithValue("$source_text", sourceText);
        command.Parameters.AddWithValue("$source_text_normalized", sourceTextNormalized);
        command.Parameters.AddWithValue("$target_text", targetText);
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$confidence", confidence);
        command.Parameters.AddWithValue("$tags_json", tagsJson);
        command.Parameters.AddWithValue("$metadata_json", metadataJson);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAsync(
        SqliteConnection connection,
        string id,
        string sourceText,
        string targetText,
        string note,
        int priority,
        double confidence,
        string tagsJson,
        string metadataJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_items
            SET source_text = $source_text,
                target_text = $target_text,
                note = $note,
                priority = $priority,
                confidence = $confidence,
                tags_json = $tags_json,
                metadata_json = $metadata_json,
                is_active = 1,
                updated_at = $updated_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source_text", sourceText);
        command.Parameters.AddWithValue("$target_text", targetText);
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$confidence", confidence);
        command.Parameters.AddWithValue("$tags_json", tagsJson);
        command.Parameters.AddWithValue("$metadata_json", metadataJson);
        command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<MemoryItem> GetByUniqueKeyAsync(
        SqliteConnection connection,
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceTextNormalized,
        CancellationToken cancellationToken)
        => await FindByUniqueKeyAsync(
            connection,
            profileId,
            memoryKind,
            sourceLanguage,
            targetLanguage,
            sourceTextNormalized,
            cancellationToken)
            ?? throw new InvalidOperationException("Memory item was not stored.");

    private static async Task<MemoryItem?> FindByUniqueKeyAsync(
        SqliteConnection connection,
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceTextNormalized,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, memory_kind, source_language, target_language,
                   source_text, target_text, note, priority, confidence,
                   tags_json, metadata_json, is_active, created_at, updated_at,
                   last_used_at, use_count
            FROM memory_items
            WHERE profile_id = $profile_id
              AND memory_kind = $memory_kind
              AND source_language = $source_language
              AND target_language = $target_language
              AND source_text_normalized = $source_text_normalized
              AND is_active = 1
            ORDER BY priority DESC, confidence DESC, updated_at DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$memory_kind", memoryKind);
        command.Parameters.AddWithValue("$source_language", sourceLanguage);
        command.Parameters.AddWithValue("$target_language", targetLanguage);
        command.Parameters.AddWithValue("$source_text_normalized", sourceTextNormalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMemoryItem(reader) : null;
    }

    private static MemoryItem ReadMemoryItem(SqliteDataReader reader)
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
            reader.GetDouble(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetInt32(12) == 1,
            DateTimeOffset.Parse(reader.GetString(13)),
            DateTimeOffset.Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)),
            reader.GetInt32(16));

    public static string NormalizeKey(string text)
        => text.Normalize(NormalizationForm.FormKC).Trim();

    private static string BuildMetadataJson(
        string? origin,
        MemoryUpsertRequest request,
        JsonSerializerOptions jsonOptions)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["origin"] = Pick(origin, "user-verified"),
            ["review_status"] = "approved"
        };

        if (request is TranslationCorrectionMemoryUpsert translationCorrection)
        {
            metadata["created_from"] = "translation-correction";
            metadata["source_event_ids"] = new[] { translationCorrection.SourceEventId };
            metadata["source_table"] = "translation_events";
        }

        return JsonSerializer.Serialize(metadata, jsonOptions);
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

public sealed record TranslationCorrectionMemoryUpsert : MemoryUpsertRequest
{
    public required string SourceEventId { get; init; }
}
