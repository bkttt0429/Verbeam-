using Microsoft.Data.Sqlite;
using Verbeam.Core.Options;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class DatabasePartitionTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "verbeam-dbpartition-tests-" + Guid.NewGuid());

    private DatabaseRouter NewRouter(DatabaseRoutingOptions? options = null)
        => new(_tempDirectory, options ?? new DatabaseRoutingOptions());

    private GameScopedStores NewGameScopedStores(DatabaseRouter? router = null)
        => new(
            router ?? NewRouter(),
            path => new MemoryFrontedTranslationCache(new SqliteTranslationCache(path)),
            path => new SqliteTranslationEventStore(path),
            path => new SqliteMemoryStore(path),
            path => new SqliteMemoryContextAuditStore(path),
            path => new SqliteSceneSummaryStore(path),
            path => new SqliteMemoryMaintenanceJobStore(path));

    [Fact]
    public void Router_Global_ResolvesToCoreFileUnderDataDirectory()
    {
        var path = NewRouter().ResolvePath(DbDomain.Global);
        Assert.Equal(Path.GetFullPath(Path.Combine(_tempDirectory, "core.sqlite")), path);
    }

    [Fact]
    public void Router_Realtime_ResolvesToPerGameFile()
    {
        var path = NewRouter().ResolvePath(DbDomain.Realtime, "game-a");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_tempDirectory, "games", "game-a", "realtime.sqlite")),
            path);
    }

    [Fact]
    public void Router_Realtime_BlankGameId_FallsBackToDefaultGame()
    {
        var path = NewRouter().ResolvePath(DbDomain.Realtime, null);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_tempDirectory, "games", "default", "realtime.sqlite")),
            path);
    }

    [Fact]
    public void Router_Realtime_TraversalGameId_StaysUnderGamesDirectory()
    {
        var path = NewRouter().ResolvePath(DbDomain.Realtime, "../../escape");
        var gamesRoot = Path.GetFullPath(Path.Combine(_tempDirectory, "games"));
        Assert.StartsWith(gamesRoot + Path.DirectorySeparatorChar, path);
    }

    [Fact]
    public void Router_Document_DefaultsGlobal_FlippablePerGame()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_tempDirectory, "document.sqlite")),
            NewRouter().ResolvePath(DbDomain.Document, "game-a"));

        var perGame = NewRouter(new DatabaseRoutingOptions { DocumentPerGame = true });
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_tempDirectory, "games", "game-a", "document.sqlite")),
            perGame.ResolvePath(DbDomain.Document, "game-a"));
    }

    [Fact]
    public async Task StoreCache_SamePath_ReturnsSameInitializedInstance()
    {
        var cache = new SqliteStoreCache<ITranslationCache>(path => new SqliteTranslationCache(path));
        var dbPath = Path.Combine(_tempDirectory, "games", "game-a", "realtime.sqlite");

        var first = await cache.GetAsync(dbPath);
        var second = await cache.GetAsync(dbPath);

        Assert.Same(first, second);
        Assert.True(File.Exists(dbPath)); // init created the file + full schema
    }

    [Fact]
    public async Task StoreCache_DifferentPaths_ReturnDifferentInstances()
    {
        var cache = new SqliteStoreCache<ITranslationCache>(path => new SqliteTranslationCache(path));

        var a = await cache.GetAsync(Path.Combine(_tempDirectory, "games", "a", "realtime.sqlite"));
        var b = await cache.GetAsync(Path.Combine(_tempDirectory, "games", "b", "realtime.sqlite"));

        Assert.NotSame(a, b);
    }

    [Fact]
    public async Task GameScopedStores_IsolatesCacheBetweenGames()
    {
        var router = NewRouter();
        var stores = NewGameScopedStores(router);

        var cacheA = await stores.CacheFor("game-a");
        await cacheA.SetAsync(NewEntry("shared-key", "A-translation"));

        var cacheB = await stores.CacheFor("game-b");
        var leaked = await cacheB.GetAsync("shared-key");
        var ownEntry = await cacheA.GetAsync("shared-key");

        Assert.Null(leaked);                 // game-b's file never saw game-a's write
        Assert.NotNull(ownEntry);
        Assert.Equal("A-translation", ownEntry!.TranslatedText);
        Assert.True(File.Exists(router.ResolvePath(DbDomain.Realtime, "game-a")));
        Assert.True(File.Exists(router.ResolvePath(DbDomain.Realtime, "game-b")));
    }

    [Fact]
    public async Task GameScopedStores_SameGame_ReturnsCachedInstance()
    {
        var stores = NewGameScopedStores();

        var first = await stores.CacheFor("game-a");
        var second = await stores.CacheFor("game-a");

        Assert.Same(first, second);
    }

    [Fact]
    public async Task GameScopedStores_PerKindAccessorsRouteByGame()
    {
        var router = NewRouter();
        var stores = NewGameScopedStores(router);

        // Same game → one cached instance per store kind; different game → a different one.
        Assert.Same(await stores.EventsFor("game-a"), await stores.EventsFor("game-a"));
        Assert.NotSame(await stores.EventsFor("game-a"), await stores.EventsFor("game-b"));
        Assert.Same(await stores.MemoryFor("game-a"), await stores.MemoryFor("game-a"));
        Assert.NotSame(await stores.MemoryFor("game-a"), await stores.MemoryFor("game-b"));

        // All realtime kinds for one game share (and auto-create) that game's file.
        Assert.True(File.Exists(router.ResolvePath(DbDomain.Realtime, "game-a")));
    }

    private static CachedTranslation NewEntry(string key, string translated)
        => new(key, "source", translated, "ja", "zh-TW", "game_dialogue",
            "mock", "mock", "mock", "1", "hash", 5, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
