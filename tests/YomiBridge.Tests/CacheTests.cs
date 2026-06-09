using YomiBridge.Core.Storage;
using Microsoft.Data.Sqlite;

namespace YomiBridge.Tests;

public sealed class CacheTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "yomibridge-cache-tests-" + Guid.NewGuid());

    [Fact]
    public async Task SqliteTranslationCache_RoundTripsEntry()
    {
        Directory.CreateDirectory(_tempDirectory);
        var cache = new SqliteTranslationCache(Path.Combine(_tempDirectory, "translations.sqlite"));
        await cache.InitializeAsync();

        var entry = new CachedTranslation(
            "key-1",
            "source text",
            "translated text",
            "ja",
            "zh-TW",
            "game_dialogue",
            "mock",
            "mock",
            "mock",
            "1",
            "hash",
            12,
            DateTimeOffset.UtcNow);

        await cache.SetAsync(entry);
        var loaded = await cache.GetAsync("key-1");

        Assert.NotNull(loaded);
        Assert.Equal("translated text", loaded.TranslatedText);
        Assert.Equal("mock", loaded.Provider);
    }

    [Fact]
    public async Task SqliteTranslationCache_InitializesRuntimeSchema()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "translations.sqlite");
        var cache = new SqliteTranslationCache(databasePath);
        await cache.InitializeAsync();

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ConnectionString);
        await connection.OpenAsync();

        var tables = await QueryStringsAsync(
            connection,
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
            ORDER BY name
            """);

        Assert.Contains("schema_migrations", tables);
        Assert.Contains("profiles", tables);
        Assert.Contains("translation_sessions", tables);
        Assert.Contains("translation_events", tables);
        Assert.Contains("glossary_sets", tables);
        Assert.Contains("glossary_terms", tables);
        Assert.Contains("memory_items", tables);
        Assert.Contains("memory_embeddings", tables);
        Assert.Contains("ocr_corrections", tables);
        Assert.Contains("ocr_events", tables);
        Assert.Contains("scene_summaries", tables);
        Assert.Contains("translations", tables);

        var migrationVersion = await ExecuteScalarLongAsync(
            connection,
            "SELECT version FROM schema_migrations WHERE version = 1");
        var defaultProfileCount = await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM profiles WHERE id = 'default'");
        var journalMode = await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode");

        Assert.Equal(1, migrationVersion);
        Assert.Equal(1, defaultProfileCount);
        Assert.Equal("wal", journalMode);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<string>> QueryStringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private static async Task<string> ExecuteScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToString(value) ?? string.Empty;
    }
}
