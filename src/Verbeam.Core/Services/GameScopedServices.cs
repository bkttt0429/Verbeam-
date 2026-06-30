using Verbeam.Core.Options;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

/// <summary>
/// Builds per-game instances of the stateless RAG/memory services, each bound to one
/// game's stores (resolved and cached by <see cref="GameScopedStores"/>). These services
/// hold no mutable state, so a fresh lightweight instance is built per call; the expensive
/// part (opening + initializing the game's SQLite file) is amortized inside the store
/// caches. The OCR memory store stays shared until the OCR partitioning phase.
/// </summary>
public sealed class GameScopedServices
{
    private readonly GameScopedStores _stores;
    private readonly VerbeamOptions _options;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IOcrMemoryStore? _ocrMemoryStore;

    public GameScopedServices(
        GameScopedStores stores,
        VerbeamOptions options,
        IEmbeddingProvider? embeddingProvider = null,
        IOcrMemoryStore? ocrMemoryStore = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(options);
        _stores = stores;
        _options = options;
        _embeddingProvider = embeddingProvider;
        _ocrMemoryStore = ocrMemoryStore;
    }

    /// <summary>RAG context builder reading one game's memory, events and scene summaries.</summary>
    public async Task<MemoryContextBuilder> ContextBuilderFor(string? gameId, CancellationToken cancellationToken = default)
    {
        var memory = await _stores.MemoryFor(gameId, cancellationToken);
        var events = await _stores.EventsFor(gameId, cancellationToken);
        var scenes = await _stores.ScenesFor(gameId, cancellationToken);
        return new MemoryContextBuilder(memory, events, scenes, _options, _embeddingProvider);
    }

    /// <summary>Memory maintenance (auto-extraction, embeddings, job drain) for one game.</summary>
    public async Task<MemoryMaintenanceService> MaintenanceFor(string? gameId, CancellationToken cancellationToken = default)
    {
        var events = await _stores.EventsFor(gameId, cancellationToken);
        var memory = await _stores.MemoryFor(gameId, cancellationToken);
        var jobStore = await _stores.MaintenanceJobsFor(gameId, cancellationToken);
        return new MemoryMaintenanceService(events, memory, _ocrMemoryStore, _options, _embeddingProvider, jobStore);
    }

    /// <summary>Scene-summary maintenance for one game.</summary>
    public async Task<SceneSummaryMaintenanceService> SceneMaintenanceFor(string? gameId, CancellationToken cancellationToken = default)
    {
        var events = await _stores.EventsFor(gameId, cancellationToken);
        var scenes = await _stores.ScenesFor(gameId, cancellationToken);
        return new SceneSummaryMaintenanceService(events, scenes, _options);
    }
}
