using System.Buffers.Binary;
using System.Globalization;
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
            includeShared: true,
            cancellationToken: cancellationToken);

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
        EnsureSecurityFlagsAcknowledged(
            requestedTrustLevel,
            finalSecurityFlagsJson,
            request.AcknowledgeSecurityFlags,
            force: existing is null ||
                !string.IsNullOrWhiteSpace(request.TrustLevel) ||
                !string.IsNullOrWhiteSpace(request.Origin) ||
                !string.Equals(existing.SourceText, sourceText, StringComparison.Ordinal) ||
                !string.Equals(existing.TargetText, targetText, StringComparison.Ordinal) ||
                !string.Equals(existing.Note, finalNote, StringComparison.Ordinal));
        var finalTrustLevel = RagSecurityPolicy.QuarantineIfNeeded(requestedTrustLevel, finalSecurityFlagsJson);
        var finalSourceHash = string.IsNullOrWhiteSpace(request.SourceHash)
            ? RagSecurityPolicy.ComputeSourceHash(sourceText, targetText, finalNote, finalSourceUri)
            : request.SourceHash.Trim();
        var metadata = BuildMetadataJson(request.Origin, finalTrustLevel, request, _jsonOptions);

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
                normalized,
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
                isActive: true,
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

    public async Task<MemoryItem?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        return await FindByIdAsync(connection, id.Trim(), cancellationToken);
    }

    public async Task<MemoryItem?> UpdateTrustAsync(
        string id,
        MemoryTrustUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("id is required.");
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        var existing = await FindByIdAsync(connection, id.Trim(), cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var trustLevel = string.IsNullOrWhiteSpace(request.TrustLevel)
            ? existing.TrustLevel
            : RagSecurityPolicy.NormalizeTrustLevel(request.TrustLevel, origin: null);
        EnsureSecurityFlagsAcknowledged(
            trustLevel,
            existing.SecurityFlagsJson,
            request.AcknowledgeSecurityFlags,
            force: !string.IsNullOrWhiteSpace(request.TrustLevel));
        var approvedBy = string.IsNullOrWhiteSpace(request.ApprovedBy)
            ? existing.ApprovedBy
            : request.ApprovedBy.Trim();
        var classification = string.IsNullOrWhiteSpace(request.Classification)
            ? existing.Classification
            : RagSecurityPolicy.NormalizeClassification(request.Classification);
        var visibility = string.IsNullOrWhiteSpace(request.Visibility)
            ? existing.Visibility
            : RagSecurityPolicy.NormalizeVisibility(request.Visibility);
        var isActive = request.IsActive ?? existing.IsActive;
        var metadata = UpdateReviewStatusMetadataJson(
            existing.MetadataJson,
            trustLevel,
            ReviewStatusOverride(existing.TrustLevel, trustLevel, isActive));
        var updatedAt = DateTimeOffset.UtcNow;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_items
            SET trust_level = $trust_level,
                metadata_json = $metadata_json,
                approved_by = $approved_by,
                classification = $classification,
                visibility = $visibility,
                is_active = $is_active,
                updated_at = $updated_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", existing.Id);
        command.Parameters.AddWithValue("$trust_level", trustLevel);
        command.Parameters.AddWithValue("$metadata_json", metadata);
        command.Parameters.AddWithValue("$approved_by", approvedBy);
        command.Parameters.AddWithValue("$classification", classification);
        command.Parameters.AddWithValue("$visibility", visibility);
        command.Parameters.AddWithValue("$is_active", isActive ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await FindByIdAsync(connection, existing.Id, cancellationToken);
    }

    public async Task<MemoryItem?> UpdateAsync(
        string id,
        MemoryUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("id is required.");
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        var existing = await FindByIdAsync(connection, id.Trim(), cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var sourceText = PickOptionalRequired(request.SourceText, "sourceText") ?? existing.SourceText;
        var targetText = PickOptionalRequired(request.TargetText, "targetText") ?? existing.TargetText;
        var note = request.Note is null ? existing.Note : request.Note.Trim();
        var trustLevel = string.IsNullOrWhiteSpace(request.TrustLevel)
            ? existing.TrustLevel
            : RagSecurityPolicy.NormalizeTrustLevel(request.TrustLevel, origin: null);
        var sourceUri = request.SourceUri is null ? existing.SourceUri : request.SourceUri.Trim();
        var createdBy = request.CreatedBy is null ? existing.CreatedBy : request.CreatedBy.Trim();
        var approvedBy = request.ApprovedBy is null ? existing.ApprovedBy : request.ApprovedBy.Trim();
        var classification = string.IsNullOrWhiteSpace(request.Classification)
            ? existing.Classification
            : RagSecurityPolicy.NormalizeClassification(request.Classification);
        var visibility = string.IsNullOrWhiteSpace(request.Visibility)
            ? existing.Visibility
            : RagSecurityPolicy.NormalizeVisibility(request.Visibility);
        var securityFlagsJson = RagSecurityPolicy.BuildSecurityFlagsJson(sourceText, targetText, note);
        EnsureSecurityFlagsAcknowledged(
            trustLevel,
            securityFlagsJson,
            request.AcknowledgeSecurityFlags,
            force: !string.IsNullOrWhiteSpace(request.TrustLevel) ||
                request.SourceText is not null ||
                request.TargetText is not null ||
                request.Note is not null);
        var finalTrustLevel = RagSecurityPolicy.QuarantineIfNeeded(trustLevel, securityFlagsJson);
        var sourceHash = string.IsNullOrWhiteSpace(request.SourceHash)
            ? RagSecurityPolicy.ComputeSourceHash(sourceText, targetText, note, sourceUri)
            : request.SourceHash.Trim();
        var isActive = request.IsActive ?? existing.IsActive;
        var metadata = UpdateReviewStatusMetadataJson(
            existing.MetadataJson,
            finalTrustLevel,
            ReviewStatusOverride(existing.TrustLevel, finalTrustLevel, isActive));
        var updatedAt = DateTimeOffset.UtcNow;

        await UpdateAsync(
            connection,
            existing.Id,
            sourceText,
            NormalizeKey(sourceText),
            targetText,
            note,
            request.Priority ?? existing.Priority,
            Math.Clamp(request.Confidence ?? existing.Confidence, 0.0, 1.0),
            existing.TagsJson,
            metadata,
            finalTrustLevel,
            sourceUri,
            sourceHash,
            createdBy,
            approvedBy,
            securityFlagsJson,
            classification,
            visibility,
            isActive,
            updatedAt,
            cancellationToken);

        return await FindByIdAsync(connection, existing.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryItem>> ListAsync(
        string profileId,
        string? memoryKind,
        int limit,
        bool activeOnly,
        string? trustLevel = null,
        string? sourceLanguage = null,
        string? targetLanguage = null,
        string? visibility = null,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string> { "profile_id = $profile_id" };
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        if (activeOnly)
        {
            conditions.Add("is_active = 1");
        }

        if (!string.IsNullOrWhiteSpace(memoryKind))
        {
            conditions.Add("memory_kind = $memory_kind");
            command.Parameters.AddWithValue("$memory_kind", memoryKind!.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(trustLevel))
        {
            conditions.Add("trust_level = $trust_level");
            command.Parameters.AddWithValue("$trust_level", RagSecurityPolicy.NormalizeTrustLevel(trustLevel, origin: null));
        }

        if (!string.IsNullOrWhiteSpace(sourceLanguage))
        {
            conditions.Add("source_language = $source_language");
            command.Parameters.AddWithValue("$source_language", sourceLanguage.Trim());
        }

        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            conditions.Add("target_language = $target_language");
            command.Parameters.AddWithValue("$target_language", targetLanguage.Trim());
        }

        if (!string.IsNullOrWhiteSpace(visibility))
        {
            conditions.Add("visibility = $visibility");
            command.Parameters.AddWithValue("$visibility", RagSecurityPolicy.NormalizeVisibility(visibility));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            conditions.Add("(source_text LIKE $query OR target_text LIKE $query OR note LIKE $query OR source_uri LIKE $query)");
            command.Parameters.AddWithValue("$query", "%" + query.Trim() + "%");
        }

        command.CommandText = $$"""
            SELECT id, profile_id, memory_kind, source_language, target_language,
                   source_text, target_text, note, priority, confidence,
                   tags_json, metadata_json, trust_level, source_uri, source_hash,
                   created_by, approved_by, security_flags_json, classification,
                   visibility, is_active, created_at, updated_at, last_used_at, use_count
            FROM memory_items
            WHERE {{string.Join("\n              AND ", conditions)}}
            ORDER BY is_active DESC, priority DESC, confidence DESC, updated_at DESC
            LIMIT $limit
            """;

        var values = new List<MemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadMemoryItem(reader));
        }

        return values;
    }

    public async Task<int> CountAsync(
        string profileId,
        bool activeOnly,
        string? trustLevel = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string> { "profile_id = $profile_id" };
        command.Parameters.AddWithValue("$profile_id", profileId);

        if (activeOnly)
        {
            conditions.Add("is_active = 1");
        }

        if (!string.IsNullOrWhiteSpace(trustLevel))
        {
            conditions.Add("trust_level = $trust_level");
            command.Parameters.AddWithValue("$trust_level", RagSecurityPolicy.NormalizeTrustLevel(trustLevel, origin: null));
        }

        command.CommandText = $$"""
            SELECT COUNT(*)
            FROM memory_items
            WHERE {{string.Join("\n              AND ", conditions)}}
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
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
              AND ($include_shared = 1 OR visibility <> 'shared')
            ORDER BY priority DESC, confidence DESC, use_count DESC, updated_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", request.ProfileId);
        command.Parameters.AddWithValue("$source_language", request.SourceLanguage);
        command.Parameters.AddWithValue("$target_language", request.TargetLanguage);
        command.Parameters.AddWithValue("$minimum_confidence", Math.Clamp(request.MinimumConfidence, 0.0, 1.0));
        command.Parameters.AddWithValue("$active_only", request.ActiveOnly ? 1 : 0);
        command.Parameters.AddWithValue("$trusted_only", request.TrustedOnly ? 1 : 0);
        command.Parameters.AddWithValue("$include_shared", request.IncludeShared ? 1 : 0);
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

    public async Task UpsertEmbeddingAsync(
        MemoryEmbedding embedding,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_embeddings (
                memory_id,
                embedding_model,
                dims,
                vector,
                content_hash,
                created_at
            )
            VALUES (
                $memory_id,
                $embedding_model,
                $dims,
                $vector,
                $content_hash,
                $created_at
            )
            ON CONFLICT(memory_id, embedding_model) DO UPDATE SET
                dims = excluded.dims,
                vector = excluded.vector,
                content_hash = excluded.content_hash,
                created_at = excluded.created_at
            """;
        command.Parameters.AddWithValue("$memory_id", embedding.MemoryId);
        command.Parameters.AddWithValue("$embedding_model", embedding.EmbeddingModel);
        command.Parameters.AddWithValue("$dims", embedding.Dimensions);
        command.Parameters.AddWithValue("$vector", EncodeVector(embedding.Vector));
        command.Parameters.AddWithValue("$content_hash", embedding.ContentHash);
        command.Parameters.AddWithValue("$created_at", embedding.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryEmbedding>> ListEmbeddingsAsync(
        IReadOnlyList<string> memoryIds,
        string embeddingModel,
        CancellationToken cancellationToken = default)
    {
        var ids = memoryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(embeddingModel))
        {
            return [];
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var idParameters = string.Join(", ", ids.Select((_, index) => "$memory_id_" + index));
        command.CommandText = $$"""
            SELECT memory_id, embedding_model, dims, vector, content_hash, created_at
            FROM memory_embeddings
            WHERE embedding_model = $embedding_model
              AND memory_id IN ({{idParameters}})
            """;
        command.Parameters.AddWithValue("$embedding_model", embeddingModel.Trim());
        for (var index = 0; index < ids.Length; index++)
        {
            command.Parameters.AddWithValue("$memory_id_" + index, ids[index]);
        }

        var values = new List<MemoryEmbedding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var vectorBytes = (byte[])reader["vector"];
            values.Add(new MemoryEmbedding(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                DecodeVector(vectorBytes),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }

        return values;
    }

    public async Task<MemoryItem?> FindExactAsync(
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceText,
        CancellationToken cancellationToken = default,
        bool includeShared = false)
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
            includeShared: includeShared,
            cancellationToken: cancellationToken);
    }

    public async Task<MemoryItem?> FindByKeyAsync(
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
            trustedOnly: false,
            includeShared: true,
            cancellationToken: cancellationToken);
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
        bool isActive,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_items
            SET source_text = $source_text,
                source_text_normalized = $source_text_normalized,
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
                is_active = $is_active,
                updated_at = $updated_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
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
        command.Parameters.AddWithValue("$is_active", isActive ? 1 : 0);
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
            includeShared: true,
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Memory item was not stored.");

    private static async Task<MemoryItem?> FindByIdAsync(
        SqliteConnection connection,
        string id,
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
            WHERE id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMemoryItem(reader) : null;
    }

    private static async Task<MemoryItem?> FindByUniqueKeyAsync(
        SqliteConnection connection,
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceTextNormalized,
        bool trustedOnly,
        bool includeShared,
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
              AND ($include_shared = 1 OR visibility <> 'shared')
            ORDER BY priority DESC, confidence DESC, updated_at DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$memory_kind", memoryKind);
        command.Parameters.AddWithValue("$source_language", sourceLanguage);
        command.Parameters.AddWithValue("$target_language", targetLanguage);
        command.Parameters.AddWithValue("$source_text_normalized", sourceTextNormalized);
        command.Parameters.AddWithValue("$trusted_only", trustedOnly ? 1 : 0);
        command.Parameters.AddWithValue("$include_shared", includeShared ? 1 : 0);

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

    private static byte[] EncodeVector(IReadOnlyList<float> vector)
    {
        var buffer = new byte[vector.Count * sizeof(float)];
        for (var index = 0; index < vector.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                buffer.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(vector[index]));
        }

        return buffer;
    }

    private static float[] DecodeVector(byte[] buffer)
    {
        if (buffer.Length % sizeof(float) != 0)
        {
            throw new InvalidOperationException("Stored memory embedding vector has an invalid byte length.");
        }

        var vector = new float[buffer.Length / sizeof(float)];
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(index * sizeof(float), sizeof(float))));
        }

        return vector;
    }

    public static string NormalizeKey(string text)
        => text.Normalize(NormalizationForm.FormKC).Trim();

    private static string BuildMetadataJson(
        string? origin,
        string trustLevel,
        MemoryUpsertRequest request,
        JsonSerializerOptions jsonOptions)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["origin"] = Pick(origin, "user-verified"),
            ["review_status"] = ReviewStatusFromTrustLevel(trustLevel)
        };

        if (request is TranslationCorrectionMemoryUpsert translationCorrection)
        {
            metadata["created_from"] = "translation-correction";
            metadata["source_event_ids"] = new[] { translationCorrection.SourceEventId };
            metadata["source_table"] = "translation_events";
        }
        else if (request is AutoExtractedMemoryUpsert autoExtracted)
        {
            metadata["created_from"] = autoExtracted.CreatedFrom;
            metadata["source_event_ids"] = autoExtracted.SourceEventIds;
            metadata["source_table"] = autoExtracted.SourceTable;
            metadata["extractor"] = autoExtracted.Extractor;
            metadata["observation_count"] = autoExtracted.ObservationCount;
        }

        return JsonSerializer.Serialize(metadata, jsonOptions);
    }

    private static string ReviewStatusFromTrustLevel(string trustLevel)
        => trustLevel switch
        {
            RagSecurityPolicy.UserVerified or RagSecurityPolicy.TrustedImport => "approved",
            RagSecurityPolicy.LocalGenerated => "candidate",
            RagSecurityPolicy.Quarantined => "quarantined",
            _ => "pending_review"
        };

    private static string? ReviewStatusOverride(
        string previousTrustLevel,
        string trustLevel,
        bool isActive)
        => !isActive &&
           previousTrustLevel == RagSecurityPolicy.LocalGenerated &&
           trustLevel == RagSecurityPolicy.LocalGenerated
            ? "rejected"
            : null;

    private static string UpdateReviewStatusMetadataJson(
        string metadataJson,
        string trustLevel,
        string? overrideStatus = null)
    {
        Dictionary<string, object?> metadata;
        try
        {
            metadata = string.IsNullOrWhiteSpace(metadataJson)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson) ?? [];
        }
        catch (JsonException)
        {
            metadata = [];
        }

        metadata["review_status"] = string.IsNullOrWhiteSpace(overrideStatus)
            ? ReviewStatusFromTrustLevel(trustLevel)
            : overrideStatus;
        return JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web));
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

    private static void EnsureSecurityFlagsAcknowledged(
        string trustLevel,
        string securityFlagsJson,
        bool? acknowledged,
        bool force)
    {
        if (!force ||
            acknowledged == true ||
            !HasSecurityFlags(securityFlagsJson) ||
            !RagSecurityPolicy.CanUseForExactMemory(trustLevel))
        {
            return;
        }

        throw new ArgumentException("acknowledgeSecurityFlags is required before approving memory with security flags.");
    }

    private static bool HasSecurityFlags(string securityFlagsJson)
        => !string.IsNullOrWhiteSpace(securityFlagsJson) &&
           securityFlagsJson.Trim() != "[]";

    private static string? PickOptionalRequired(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} cannot be blank.");
        }

        return value.Trim();
    }
}

public sealed record TranslationCorrectionMemoryUpsert : MemoryUpsertRequest
{
    public required string SourceEventId { get; init; }
}

public sealed record AutoExtractedMemoryUpsert : MemoryUpsertRequest
{
    public required IReadOnlyList<string> SourceEventIds { get; init; }
    public required int ObservationCount { get; init; }
    public string CreatedFrom { get; init; } = "auto-translation-memory";
    public string SourceTable { get; init; } = "translation_events";
    public string Extractor { get; init; } = "memory-maintenance-v1";
}
