namespace Verbeam.Core.Storage;

/// <summary>
/// Resolves and caches per-game store instances. Each accessor maps a gameId
/// (≡ profileId) to that game's physical realtime database file via
/// <see cref="IDatabaseRouter"/> and returns a lazily-initialized store for it; a new
/// game auto-creates its file on first access. Every realtime store for one game shares
/// the same games/{gameId}/realtime.sqlite file — the whole schema is created in it, so
/// each store only touches its own tables.
/// </summary>
public sealed class GameScopedStores
{
    private readonly IDatabaseRouter _router;
    private readonly SqliteStoreCache<ITranslationCache> _caches;
    private readonly SqliteStoreCache<ITranslationEventStore> _events;
    private readonly SqliteStoreCache<IMemoryStore> _memories;
    private readonly SqliteStoreCache<IMemoryContextAuditStore> _audits;
    private readonly SqliteStoreCache<ISceneSummaryStore> _scenes;
    private readonly SqliteStoreCache<IMemoryMaintenanceJobStore> _maintenanceJobs;

    public GameScopedStores(
        IDatabaseRouter router,
        Func<string, ITranslationCache> translationCacheFactory,
        Func<string, ITranslationEventStore> eventStoreFactory,
        Func<string, IMemoryStore> memoryStoreFactory,
        Func<string, IMemoryContextAuditStore> auditStoreFactory,
        Func<string, ISceneSummaryStore> sceneSummaryStoreFactory,
        Func<string, IMemoryMaintenanceJobStore> maintenanceJobStoreFactory)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        _caches = new SqliteStoreCache<ITranslationCache>(translationCacheFactory);
        _events = new SqliteStoreCache<ITranslationEventStore>(eventStoreFactory);
        _memories = new SqliteStoreCache<IMemoryStore>(memoryStoreFactory);
        _audits = new SqliteStoreCache<IMemoryContextAuditStore>(auditStoreFactory);
        _scenes = new SqliteStoreCache<ISceneSummaryStore>(sceneSummaryStoreFactory);
        _maintenanceJobs = new SqliteStoreCache<IMemoryMaintenanceJobStore>(maintenanceJobStoreFactory);
    }

    /// <summary>The realtime translation cache for one game (its own LRU front + SQLite file).</summary>
    public Task<ITranslationCache> CacheFor(string? gameId, CancellationToken cancellationToken = default)
        => _caches.GetAsync(Resolve(gameId), cancellationToken);

    /// <summary>The translation event store (sessions/events) for one game.</summary>
    public Task<ITranslationEventStore> EventsFor(string? gameId, CancellationToken cancellationToken = default)
        => _events.GetAsync(Resolve(gameId), cancellationToken);

    /// <summary>The memory store (memory_items / embeddings) for one game.</summary>
    public Task<IMemoryStore> MemoryFor(string? gameId, CancellationToken cancellationToken = default)
        => _memories.GetAsync(Resolve(gameId), cancellationToken);

    /// <summary>The RAG context audit store for one game.</summary>
    public Task<IMemoryContextAuditStore> AuditFor(string? gameId, CancellationToken cancellationToken = default)
        => _audits.GetAsync(Resolve(gameId), cancellationToken);

    /// <summary>The scene-summary store for one game.</summary>
    public Task<ISceneSummaryStore> ScenesFor(string? gameId, CancellationToken cancellationToken = default)
        => _scenes.GetAsync(Resolve(gameId), cancellationToken);

    /// <summary>The memory-maintenance job queue for one game.</summary>
    public Task<IMemoryMaintenanceJobStore> MaintenanceJobsFor(string? gameId, CancellationToken cancellationToken = default)
        => _maintenanceJobs.GetAsync(Resolve(gameId), cancellationToken);

    private string Resolve(string? gameId) => _router.ResolvePath(DbDomain.Realtime, gameId);
}
