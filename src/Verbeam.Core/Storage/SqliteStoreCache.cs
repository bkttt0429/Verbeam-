using System.Collections.Concurrent;

namespace Verbeam.Core.Storage;

/// <summary>
/// Caches one initialized store instance per resolved database path. The first caller
/// for a path constructs the store and runs its one-time <see cref="IInitializableStore.InitializeAsync"/>
/// (which creates the file + full schema); later callers reuse the same instance. Init
/// runs exactly once per path even under concurrent first-touch (the per-key
/// <see cref="Lazy{T}"/> gate), and a failed init is evicted so a subsequent call retries
/// instead of caching the fault forever.
/// </summary>
public sealed class SqliteStoreCache<T> where T : IInitializableStore
{
    private readonly Func<string, T> _factory;
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public SqliteStoreCache(Func<string, T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public Task<T> GetAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var key = Path.GetFullPath(databasePath);
        var entry = _entries.GetOrAdd(key, k => new Lazy<Task<T>>(() => CreateAsync(k)));
        return AwaitEntryAsync(key, entry, cancellationToken);
    }

    private async Task<T> CreateAsync(string databasePath)
    {
        var store = _factory(databasePath);
        // The shared init runs uncancelled; each caller bounds its own wait with its own
        // token in AwaitEntryAsync, so one caller cancelling can't poison the others.
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        return store;
    }

    private async Task<T> AwaitEntryAsync(string key, Lazy<Task<T>> entry, CancellationToken cancellationToken)
    {
        try
        {
            return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This caller bailed out; leave the shared init running for the others.
            throw;
        }
        catch
        {
            // Init faulted: drop the cached fault so the next caller re-attempts.
            _entries.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, entry));
            throw;
        }
    }
}
