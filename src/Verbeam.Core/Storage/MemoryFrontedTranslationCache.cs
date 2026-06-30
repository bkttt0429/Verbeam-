namespace Verbeam.Core.Storage;

/// <summary>
/// In-memory LRU front for a persistent translation cache. Steady-state region
/// loops hit the dictionary instead of opening a SQLite connection per frame;
/// the inner cache keeps cross-restart persistence.
/// </summary>
public sealed class MemoryFrontedTranslationCache : ITranslationCache
{
    private readonly ITranslationCache _inner;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<CachedTranslation>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<CachedTranslation> _order = new();

    public MemoryFrontedTranslationCache(ITranslationCache inner, int capacity = 512)
    {
        _inner = inner;
        _capacity = Math.Max(1, capacity);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _inner.InitializeAsync(cancellationToken);

    public async Task<CachedTranslation?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value;
            }
        }

        var fromInner = await _inner.GetAsync(key, cancellationToken);
        if (fromInner is not null)
        {
            Remember(fromInner);
        }

        return fromInner;
    }

    public async Task SetAsync(CachedTranslation entry, CancellationToken cancellationToken = default)
    {
        Remember(entry);
        await _inner.SetAsync(entry, cancellationToken);
    }

    private void Remember(CachedTranslation entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Key, out var existing))
            {
                _order.Remove(existing);
            }

            var node = _order.AddFirst(entry);
            _entries[entry.Key] = node;

            while (_entries.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
        }
    }
}
