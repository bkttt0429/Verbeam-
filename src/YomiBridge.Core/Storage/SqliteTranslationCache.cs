namespace YomiBridge.Core.Storage;

public sealed class SqliteTranslationCache : ITranslationCache
{
    private readonly string _databasePath;

    public SqliteTranslationCache(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<CachedTranslation?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, source_text, translated_text, source_language, target_language, mode,
                   provider, engine, model, preset_version, glossary_hash, latency_ms, created_at
            FROM translations
            WHERE key = $key
            """;
        command.Parameters.AddWithValue("$key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CachedTranslation(
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
            reader.GetInt64(11),
            DateTimeOffset.Parse(reader.GetString(12)));
    }

    public async Task SetAsync(CachedTranslation entry, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO translations (
                key, source_text, translated_text, source_language, target_language, mode,
                provider, engine, model, preset_version, glossary_hash, latency_ms, created_at
            )
            VALUES (
                $key, $source_text, $translated_text, $source_language, $target_language, $mode,
                $provider, $engine, $model, $preset_version, $glossary_hash, $latency_ms, $created_at
            )
            ON CONFLICT(key) DO UPDATE SET
                translated_text = excluded.translated_text,
                engine = excluded.engine,
                latency_ms = excluded.latency_ms,
                created_at = excluded.created_at
            """;

        command.Parameters.AddWithValue("$key", entry.Key);
        command.Parameters.AddWithValue("$source_text", entry.SourceText);
        command.Parameters.AddWithValue("$translated_text", entry.TranslatedText);
        command.Parameters.AddWithValue("$source_language", entry.SourceLanguage);
        command.Parameters.AddWithValue("$target_language", entry.TargetLanguage);
        command.Parameters.AddWithValue("$mode", entry.Mode);
        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$engine", entry.Engine);
        command.Parameters.AddWithValue("$model", entry.Model);
        command.Parameters.AddWithValue("$preset_version", entry.PresetVersion);
        command.Parameters.AddWithValue("$glossary_hash", entry.GlossaryHash);
        command.Parameters.AddWithValue("$latency_ms", entry.LatencyMs);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
