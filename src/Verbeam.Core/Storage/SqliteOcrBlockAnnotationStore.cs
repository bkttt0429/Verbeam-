using System.Globalization;
using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class SqliteOcrBlockAnnotationStore : IOcrBlockAnnotationStore
{
    private readonly string _databasePath;

    public SqliteOcrBlockAnnotationStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<IReadOnlyList<OcrBlockAnnotation>> ListByImageAsync(
        string profileId,
        string imageHash,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, image_hash, block_id, status, locked,
                   edited_translation, edited_source, note, reading_order_override,
                   type_override, updated_at
            FROM ocr_block_annotations
            WHERE profile_id = $profile_id AND image_hash = $image_hash
            ORDER BY block_id
            """;
        command.Parameters.AddWithValue("$profile_id", Normalize(profileId, "default"));
        command.Parameters.AddWithValue("$image_hash", imageHash ?? string.Empty);

        var results = new List<OcrBlockAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadAnnotation(reader));
        }

        return results;
    }

    public async Task<OcrBlockAnnotation> UpsertAsync(
        OcrBlockAnnotation annotation,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var profileId = Normalize(annotation.ProfileId, "default");
        var status = NormalizeStatus(annotation.Status);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_block_annotations (
                profile_id, image_hash, block_id, status, locked,
                edited_translation, edited_source, note, reading_order_override,
                type_override, updated_at)
            VALUES (
                $profile_id, $image_hash, $block_id, $status, $locked,
                $edited_translation, $edited_source, $note, $reading_order_override,
                $type_override, $updated_at)
            ON CONFLICT (profile_id, image_hash, block_id) DO UPDATE SET
                status = excluded.status,
                locked = excluded.locked,
                edited_translation = excluded.edited_translation,
                edited_source = excluded.edited_source,
                note = excluded.note,
                reading_order_override = excluded.reading_order_override,
                type_override = excluded.type_override,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$image_hash", annotation.ImageHash ?? string.Empty);
        command.Parameters.AddWithValue("$block_id", annotation.BlockId ?? string.Empty);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$locked", annotation.Locked ? 1 : 0);
        command.Parameters.AddWithValue("$edited_translation", annotation.EditedTranslation ?? string.Empty);
        command.Parameters.AddWithValue("$edited_source", annotation.EditedSource ?? string.Empty);
        command.Parameters.AddWithValue("$note", annotation.Note ?? string.Empty);
        command.Parameters.AddWithValue(
            "$reading_order_override",
            annotation.ReadingOrderOverride.HasValue ? annotation.ReadingOrderOverride.Value : DBNull.Value);
        command.Parameters.AddWithValue("$type_override", annotation.TypeOverride ?? string.Empty);
        command.Parameters.AddWithValue("$updated_at", annotation.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return annotation with { ProfileId = profileId, Status = status };
    }

    public async Task AppendHistoryAsync(
        OcrBlockHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_block_history (
                id, profile_id, image_hash, block_id, kind,
                source_text, translated_text, engine, provider, created_at)
            VALUES (
                $id, $profile_id, $image_hash, $block_id, $kind,
                $source_text, $translated_text, $engine, $provider, $created_at)
            """;
        command.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id);
        command.Parameters.AddWithValue("$profile_id", Normalize(entry.ProfileId, "default"));
        command.Parameters.AddWithValue("$image_hash", entry.ImageHash ?? string.Empty);
        command.Parameters.AddWithValue("$block_id", entry.BlockId ?? string.Empty);
        command.Parameters.AddWithValue("$kind", NormalizeKind(entry.Kind));
        command.Parameters.AddWithValue("$source_text", entry.SourceText ?? string.Empty);
        command.Parameters.AddWithValue("$translated_text", entry.TranslatedText ?? string.Empty);
        command.Parameters.AddWithValue("$engine", entry.Engine ?? string.Empty);
        command.Parameters.AddWithValue("$provider", entry.Provider ?? string.Empty);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OcrBlockHistoryEntry>> ListHistoryAsync(
        string profileId,
        string imageHash,
        string blockId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, image_hash, block_id, kind,
                   source_text, translated_text, engine, provider, created_at
            FROM ocr_block_history
            WHERE profile_id = $profile_id AND image_hash = $image_hash AND block_id = $block_id
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", Normalize(profileId, "default"));
        command.Parameters.AddWithValue("$image_hash", imageHash ?? string.Empty);
        command.Parameters.AddWithValue("$block_id", blockId ?? string.Empty);
        command.Parameters.AddWithValue("$limit", limit <= 0 ? 50 : limit);

        var results = new List<OcrBlockHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new OcrBlockHistoryEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return results;
    }

    private static OcrBlockAnnotation ReadAnnotation(SqliteDataReader reader)
    {
        return new OcrBlockAnnotation(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4) != 0,
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
        {
            EditedSource = reader.GetString(6),
            Note = reader.GetString(7),
            ReadingOrderOverride = reader.IsDBNull(8) ? null : (int)reader.GetInt64(8),
            TypeOverride = reader.GetString(9)
        };
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeStatus(string? status)
    {
        var value = (status ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            OcrBlockStatuses.Edited => OcrBlockStatuses.Edited,
            OcrBlockStatuses.Ignored => OcrBlockStatuses.Ignored,
            OcrBlockStatuses.Locked => OcrBlockStatuses.Locked,
            _ => OcrBlockStatuses.Translated
        };
    }

    private static string NormalizeKind(string? kind)
    {
        var value = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return value == OcrBlockHistoryKinds.Ocr ? OcrBlockHistoryKinds.Ocr : OcrBlockHistoryKinds.Translation;
    }
}
