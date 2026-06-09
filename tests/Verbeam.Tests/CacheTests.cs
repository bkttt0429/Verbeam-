using Verbeam.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Verbeam.Tests;

public sealed class CacheTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "verbeam-cache-tests-" + Guid.NewGuid());

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
        Assert.Contains("rag_context_audit", tables);
        Assert.Contains("scene_summaries", tables);
        Assert.Contains("translations", tables);

        var migrationVersion = await ExecuteScalarLongAsync(
            connection,
            "SELECT MAX(version) FROM schema_migrations");
        var defaultProfileCount = await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM profiles WHERE id = 'default'");
        var journalMode = await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode");
        var memoryColumns = await QueryStringsAsync(
            connection,
            """
            SELECT name
            FROM pragma_table_info('memory_items')
            ORDER BY name
            """);

        Assert.Equal(3, migrationVersion);
        Assert.Equal(1, defaultProfileCount);
        Assert.Equal("wal", journalMode);
        Assert.Contains("trust_level", memoryColumns);
        Assert.Contains("source_uri", memoryColumns);
        Assert.Contains("source_hash", memoryColumns);
        Assert.Contains("created_by", memoryColumns);
        Assert.Contains("approved_by", memoryColumns);
        Assert.Contains("security_flags_json", memoryColumns);
        Assert.Contains("classification", memoryColumns);
        Assert.Contains("visibility", memoryColumns);
    }

    [Fact]
    public async Task SqliteSchema_AddsMemorySecurityColumnsToExistingDatabase()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "legacy.sqlite");

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteNonQueryAsync(
                connection,
                """
                CREATE TABLE memory_items (
                    id TEXT PRIMARY KEY,
                    profile_id TEXT NOT NULL,
                    memory_kind TEXT NOT NULL,
                    source_language TEXT NOT NULL,
                    target_language TEXT NOT NULL,
                    source_text TEXT NOT NULL,
                    source_text_normalized TEXT NOT NULL DEFAULT '',
                    target_text TEXT NOT NULL,
                    note TEXT NOT NULL DEFAULT '',
                    priority INTEGER NOT NULL DEFAULT 0,
                    confidence REAL NOT NULL DEFAULT 1.0,
                    tags_json TEXT NOT NULL DEFAULT '[]',
                    metadata_json TEXT NOT NULL DEFAULT '{}',
                    is_active INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_used_at TEXT,
                    use_count INTEGER NOT NULL DEFAULT 0
                );

                INSERT INTO memory_items (
                    id, profile_id, memory_kind, source_language, target_language,
                    source_text, source_text_normalized, target_text,
                    created_at, updated_at
                )
                VALUES (
                    'legacy-1', 'default', 'translation', 'en', 'zh-TW',
                    'hello', 'hello', '你好',
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'
                );
                """);
        }

        var cache = new SqliteTranslationCache(databasePath);
        await cache.InitializeAsync();

        await using var verified = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ConnectionString);
        await verified.OpenAsync();

        var trustLevel = await ExecuteScalarStringAsync(
            verified,
            "SELECT trust_level FROM memory_items WHERE id = 'legacy-1'");
        var visibility = await ExecuteScalarStringAsync(
            verified,
            "SELECT visibility FROM memory_items WHERE id = 'legacy-1'");
        var migrationVersion = await ExecuteScalarLongAsync(
            verified,
            "SELECT MAX(version) FROM schema_migrations");

        Assert.Equal("user_verified", trustLevel);
        Assert.Equal("profile", visibility);
        Assert.Equal(3, migrationVersion);
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

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
