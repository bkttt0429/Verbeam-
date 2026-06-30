namespace Verbeam.Core.Services;

/// <summary>
/// Rolling per-scope window of recent realtime source→translation pairs (live
/// subtitles, chat). The window text is injected into the LLM prompt on cache
/// misses so short lines translate with their surrounding dialogue, but it is
/// deliberately kept OUT of the translation cache key: repeated lines keep
/// hitting the cache (they are the context-insensitive ones), while unique
/// lines miss and get translated with fresh context.
/// </summary>
public sealed class RealtimeContextWindow
{
    // 1-2 prior lines are enough for realtime coreference (pronouns / continuity); keeping the
    // window small also keeps the injected prompt short so it fits a smaller per-slot context.
    public const int MaxPairsPerScope = 2;
    private const int MaxScopes = 64;
    private const int MaxLineLength = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<ScopeEntry>> _scopes = new(StringComparer.Ordinal);
    private readonly LinkedList<ScopeEntry> _scopeOrder = new();

    private sealed record ScopeEntry(string Key, List<(string Source, string Translated)> Pairs);

    /// <summary>
    /// Records a finished source→translation pair. Re-appending a source already
    /// in the window replaces it and moves it to the most-recent slot, so a
    /// subtitle line repeated across frames never floods the window.
    /// </summary>
    public void Append(string scopeKey, string sourceText, string translatedText)
    {
        var source = (sourceText ?? string.Empty).Trim();
        var translated = (translatedText ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(scopeKey) ||
            source.Length == 0 || translated.Length == 0 ||
            source.Length > MaxLineLength || translated.Length > MaxLineLength)
        {
            return;
        }

        lock (_gate)
        {
            var pairs = TouchScope(scopeKey).Pairs;
            pairs.RemoveAll(pair => string.Equals(pair.Source, source, StringComparison.Ordinal));
            pairs.Add((source, translated));
            if (pairs.Count > MaxPairsPerScope)
            {
                pairs.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Renders the window as a prompt block, oldest pair first, or null when
    /// there is nothing useful to show. The line currently being translated is
    /// excluded so it never appears as its own "preceding" context.
    /// </summary>
    public string? BuildContext(string scopeKey, string? excludeSource = null)
    {
        if (string.IsNullOrEmpty(scopeKey))
        {
            return null;
        }

        var exclude = excludeSource?.Trim();
        lock (_gate)
        {
            if (!_scopes.TryGetValue(scopeKey, out var node))
            {
                return null;
            }

            var lines = node.Value.Pairs
                .Where(pair => !string.Equals(pair.Source, exclude, StringComparison.Ordinal))
                .Select(pair => $"{Flatten(pair.Source)} => {Flatten(pair.Translated)}")
                .ToArray();
            return lines.Length == 0
                ? null
                : "Recent preceding lines (reference for tone and pronouns only; translate only the new text):\n"
                    + string.Join("\n", lines);
        }
    }

    private ScopeEntry TouchScope(string scopeKey)
    {
        if (_scopes.TryGetValue(scopeKey, out var node))
        {
            _scopeOrder.Remove(node);
            _scopeOrder.AddFirst(node);
            return node.Value;
        }

        var entry = new ScopeEntry(scopeKey, []);
        var created = _scopeOrder.AddFirst(entry);
        _scopes[scopeKey] = created;
        if (_scopes.Count > MaxScopes)
        {
            var oldest = _scopeOrder.Last!;
            _scopes.Remove(oldest.Value.Key);
            _scopeOrder.RemoveLast();
        }

        return entry;
    }

    private static string Flatten(string value) => value.ReplaceLineEndings(" ");
}
