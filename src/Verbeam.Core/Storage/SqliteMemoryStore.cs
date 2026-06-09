using System.Text;
using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Services;
using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

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
        var note = request.Note?.Trim() ?? string.Empty;
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
            trustedOnly: false,
            cancellationToken);

        var finalNote = existing is null || !string.IsNullOrWhiteSpace(request.Note) ? note : existing.Note;
        var finalSourceUri = existing is null || !string.IsNullOrWhiteSpace(request.SourceUri)
            ? request.SourceUri?.Trim() ?? string.Empty
            : existing.SourceUri;
        var finalCreatedBy = existing is null || !string.IsNullOrWhiteSpace(request.CreatedBy)
            ? request.CreatedBy?.Trim() ?? string.Empty
            : existing.CreatedBy;
        var finalApprovedBy = existing is null || !string.IsNullOrWhiteSpace(request.ApprovedBy)
            ? request.ApprovedBy?.Trim() ?? string.Empty
            : existing.ApprovedBy;
        var finalClassification = existing is null || !string.IsNullOrWhiteSpace(request.Classification)
            ? RagSecurityPolicy.NormalizeClassification(request.Classification)
            : existing.Classification;
        var finalVisibility = existing is null || !string.IsNullOrWhiteSpace(request.Visibility)
            ? RagSecurityPolicy.NormalizeVisibility(request.Visibility)
            : existing.Visibility;
        var requestedTrustLevel = existing is not null &&
            string.IsNullOrWhiteSpace(request.TrustLevel) &&
            string.IsNullOrWhiteSpace(request.Origin)
                ? existing.TrustLevel
                : RagSecurityPolicy.NormalizeTrustLevel(request.TrustLevel, request.Origin);
        var finalSecurityFlagsJson = RagSecurityPolicy.BuildSecurityFlagsJson(sourceText, targetText, finalNote);
        var finalTrustLevel = RagSecurityPolicy.QuarantineIfNeeded(requestedTrustLevel, finalSecurityFlagsJson);
        var finalSourceHash = string.IsNullOrWhiteSpace(request.SourceHash)
            ? RagSecurityPolicy.ComputeSourceHash(sourceText, targetText, finalNote, finalSourceUri)
            : request.SourceHash.Trim();

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
                note,
                request.Priority ?? 0,
                Math.Clamp(request.Confidence ?? 1.0, 0.0, 1.0),
                tagsJson: "[]",
                metadata,
                finalTrustLevel,
                finalSourceUri,
                finalSourceHash,
                finalCreatedBy,
                finalApprovedBy,
                finalSecurityFlagsJson,
                finalClassification,
                finalVisibility,
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
                finalNote,
                request.Priority ?? existing.Priority,
                Math.Clamp(request.Confidence ?? existing.Confidence, 0.0, 1.0),
                tagsJson: existing.TagsJson,
                metadata,
                finalTrustLevel,
                finalSourceUri,
                finalSourceHash,
                finalCreatedBy,
                finalApprovedBy,
                finalSecurityFlagsJson,
                finalClassification,
                finalVisibility,
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
                         tags_json, metadata_json, trust_level, source_uri, source_hash,
                         created_by, approved_by, security_flags_json, classification,
                         visibility, is_active, created_at, updated_at, last_used_at, use_count
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
                         tags_json, metadata_json, trust_level, source_uri, source_hash,
                         created_by, approved_by, security_flags_json, classification,
                         visibility, is_active, created_at, updated_at, last_used_at, use_count
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
                         tags_json, metadata_json, trust_level, source_uri, source_hash,
                         created_by, approved_by, security_flags_json, classification,
                         visibility, is_active, created_at, updated_at, last_used_at, use_count
                  FROM memory_items
                  WHERE profile_id = $profile_id
                    AND memory_kind = $memory_kind
                  ORDER BY is_active DESC, priority DESC, confidence DESC, updated_at DESC
                  LIMIT $limit
                  """
                : """
                  SELECT id, profile_id, memory_kind, source_language, target_language,
                         source_text, target_text, note, priority, confidence,
                         tags_json, metadata_json, trust_level, source_uri, source_hash,
                         created_by, approved_by, security_flags_json, classification,
                         visibility, is_active, created_at, updated_at, last_used_at, use_count
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

    public async Task<IReadOnlyList<MemoryItem>> SearchAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var kinds = request.MemoryKinds
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Select(kind => kind.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (kinds.Length == 0)
        {
            return [];
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var kindParameters = string.Join(", ", kinds.Select((_, index) => "$memory_kind_" + index));
        command.CommandText = $$"""
            SELECT id, profile_id, memory_kind, source_language, target_language,
                   source_text, target_text, note, priority, confidence,
                   tags_json, metadata_json, trust_level, source_uri, source_hash,
                   created_by, approved_by, security_flags_json, classification,
                   visibility, is_active, created_at, updated_at, last_used_at, use_count
            FROM memory_items
            WHERE profile_id = $profile_id
              AND source_language = $source_language
              AND target_language = $target_language
              AND memory_kind IN ({{kindParameters}})
              AND confidence >= $minimum_confidence
              AND ($active_only = 0 OR is_active = 1)
              AND ($trusted_only = 0 OR trust_level IN ('user_verified', 'trusted_import'))
            ORDER BY priority DESC, confidence DESC, use_count DESC, updated_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", request.ProfileId);
        command.Parameters.AddWithValue("$source_language", request.SourceLanguage);
        command.Parameters.AddWithValue("$target_language", request.TargetLanguage);
        command.Parameters.AddWithValue("$minimum_confidence", Math.Clamp(request.MinimumConfidence, 0.0, 1.0));
        command.Parameters.AddWithValue("$active_only", request.ActiveOnly ? 1 : 0);
        command.Parameters.AddWithValue("$trusted_only", request.TrustedOnly ? 1 : 0);
        command.Parameters.AddWithValue("$limit", Math.Clamp(request.Limit, 1, 500));
        for (var index = 0; index < kinds.Length; index++)
        {
            command.Parameters.AddWithValue("$memory_kind_" + index, kinds[index]);
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
            trustedOnly: true,
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
                    last_used_at = $last_used_at
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
        string trustLevel,
        string sourceUri,
        string sourceHash,
        string createdBy,
        string approvedBy,
        string securityFlagsJson,
        string classification,
        string visibility,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_items (
                id, profile_id, memory_kind, source_language, target_language,
                source_text, source_text_normalized, target_text, note, priority,
                confidence, tags_json, metadata_json, trust_level, source_uri, source_hash,
                created_by, approved_by, security_flags_json, classification, visibility,
                is_active, created_at, updated_at
            )
            VALUES (
                $id, $profile_id, $memory_kind, $source_language, $target_language,
                $source_text, $source_text_normalized, $target_text, $note, $priority,
                $confidence, $tags_json, $metadata_json, $trust_level, $source_uri, $source_hash,
                $created_by, $approved_by, $security_flags_json, $classification, $visibility,
                1, $created_at, $updated_at
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
        command.Parameters.AddWithValue("$trust_level", trustLevel);
        command.Parameters.AddWithValue("$source_uri", sourceUri);
        command.Parameters.AddWithValue("$source_hash", sourceHash);
        command.Parameters.AddWithValue("$created_by", createdBy);
        command.Parameters.AddWithValue("$approved_by", approvedBy);
        command.Parameters.AddWithValue("$security_flags_json", securityFlagsJson);
        command.Parameters.AddWithValue("$classification", classification);
        command.Parameters.AddWithValue("$visibility", visibility);
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
        string trustLevel,
        string sourceUri,
        string sourceHash,
        string createdBy,
        string approvedBy,
        string securityFlagsJson,
        string classification,
        string visibility,
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
                trust_level = $trust_level,
                source_uri = $source_uri,
                source_hash = $source_hash,
                created_by = $created_by,
                approved_by = $approved_by,
                security_flags_json = $security_flags_json,
                classification = $classification,
                visibility = $visibility,
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
        command.Parameters.AddWithValue("$trust_level", trustLevel);
        command.Parameters.AddWithValue("$source_uri", sourceUri);
        command.Parameters.AddWithValue("$source_hash", sourceHash);
        command.Parameters.AddWithValue("$created_by", createdBy);
        command.Parameters.AddWithValue("$approved_by", approvedBy);
        command.Parameters.AddWithValue("$security_flags_json", securityFlagsJson);
        command.Parameters.AddWithValue("$classification", classification);
        command.Parameters.AddWithValue("$visibility", visibility);
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
            trustedOnly: false,
            cancellationToken)
            ?? throw new InvalidOperationException("Memory item was not stored.");

    private static async Task<MemoryItem?> FindByUniqueKeyAsync(
        SqliteConnection connection,
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceTextNormalized,
        bool trustedOnly,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, memory_kind, source_language, target_language,
                   source_text, target_text, note, priority, confidence,
                   tags_json, metadata_json, trust_level, source_uri, source_hash,
                   created_by, approved_by, security_flags_json, classification,
                   visibility, is_active, created_at, updated_at, last_used_at, use_count
            FROM memory_items
            WHERE profile_id = $profile_id
              AND memory_kind = $memory_kind
              AND source_language = $source_language
              AND target_language = $target_language
              AND source_text_normalized = $source_text_normalized
              AND is_active = 1
              AND ($trusted_only = 0 OR trust_level IN ('user_verified', 'trusted_import'))
            ORDER BY priority DESC, confidence DESC, updated_at DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$memory_kind", memoryKind);
        command.Parameters.AddWithValue("$source_language", sourceLanguage);
        command.Parameters.AddWithValue("$target_language", targetLanguage);
        command.Parameters.AddWithValue("$source_text_normalized", sourceTextNormalized);
        command.Parameters.AddWithValue("$trusted_only", trustedOnly ? 1 : 0);

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
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetInt32(20) == 1,
            DateTimeOffset.Parse(reader.GetString(21)),
            DateTimeOffset.Parse(reader.GetString(22)),
            reader.IsDBNull(23) ? null : DateTimeOffset.Parse(reader.GetString(23)),
            reader.GetInt32(24));

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
