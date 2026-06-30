using System.Globalization;
using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class SqliteOcrBlockLayoutStore : IOcrBlockLayoutStore
{
    private readonly string _databasePath;

    public SqliteOcrBlockLayoutStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<IReadOnlyList<OcrBlockLayout>> ListByDocKeyAsync(
        string profileId,
        string docKey,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, doc_key, block_id, nx, ny, nw, nh,
                   font_size, line_height, text_align, overflow, updated_at
            FROM ocr_block_layout
            WHERE profile_id = $profile_id AND doc_key = $doc_key
            ORDER BY block_id
            """;
        command.Parameters.AddWithValue("$profile_id", Normalize(profileId, "default"));
        command.Parameters.AddWithValue("$doc_key", docKey ?? string.Empty);

        var results = new List<OcrBlockLayout>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadLayout(reader));
        }

        return results;
    }

    public async Task<OcrBlockLayout> UpsertLayoutAsync(
        OcrBlockLayout layout,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var profileId = Normalize(layout.ProfileId, "default");
        var overflow = NormalizeOverflow(layout.Overflow);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_block_layout (
                profile_id, doc_key, block_id, nx, ny, nw, nh,
                font_size, line_height, text_align, overflow, updated_at)
            VALUES (
                $profile_id, $doc_key, $block_id, $nx, $ny, $nw, $nh,
                $font_size, $line_height, $text_align, $overflow, $updated_at)
            ON CONFLICT (profile_id, doc_key, block_id) DO UPDATE SET
                nx = excluded.nx,
                ny = excluded.ny,
                nw = excluded.nw,
                nh = excluded.nh,
                font_size = excluded.font_size,
                line_height = excluded.line_height,
                text_align = excluded.text_align,
                overflow = excluded.overflow,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$doc_key", layout.DocKey ?? string.Empty);
        command.Parameters.AddWithValue("$block_id", layout.BlockId ?? string.Empty);
        command.Parameters.AddWithValue("$nx", NullableDouble(layout.Nx));
        command.Parameters.AddWithValue("$ny", NullableDouble(layout.Ny));
        command.Parameters.AddWithValue("$nw", NullableDouble(layout.Nw));
        command.Parameters.AddWithValue("$nh", NullableDouble(layout.Nh));
        command.Parameters.AddWithValue("$font_size", NullableDouble(layout.FontSize));
        command.Parameters.AddWithValue("$line_height", NullableDouble(layout.LineHeight));
        command.Parameters.AddWithValue("$text_align", layout.TextAlign ?? string.Empty);
        command.Parameters.AddWithValue("$overflow", overflow);
        command.Parameters.AddWithValue("$updated_at", layout.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return layout with { ProfileId = profileId, Overflow = overflow };
    }

    private static OcrBlockLayout ReadLayout(SqliteDataReader reader)
    {
        return new OcrBlockLayout(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
        {
            Nx = reader.IsDBNull(3) ? null : reader.GetDouble(3),
            Ny = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            Nw = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            Nh = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            FontSize = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            LineHeight = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            TextAlign = reader.GetString(9),
            Overflow = reader.GetString(10)
        };
    }

    private static object NullableDouble(double? value)
        => value.HasValue ? value.Value : DBNull.Value;

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeOverflow(string? overflow)
    {
        var value = (overflow ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            OcrBlockOverflowModes.Wrap => OcrBlockOverflowModes.Wrap,
            OcrBlockOverflowModes.RetranslateShorter => OcrBlockOverflowModes.RetranslateShorter,
            _ => OcrBlockOverflowModes.Shrink
        };
    }
}
