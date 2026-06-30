using System.Text;
using System.Text.RegularExpressions;

namespace Verbeam.Core.Services;

/// <summary>
/// Realtime-only translation savers for chat-like lines (live streams, game trade
/// spam) where most of a line repeats and only an ID or numbers change.
///
/// Chat prefix split: a leading "ID:" token is passed through verbatim — small
/// models tend to mangle player IDs, and splitting lets every sender share one
/// cache entry for the same message body.
///
/// Digit-slot templates: a translated line whose digit runs survive translation
/// verbatim (validated by sequence alignment, never by trusting the LLM with
/// placeholders) is stored as a template; later lines differing only in numbers
/// substitute the new values at zero cost. Numbers always reflect the current
/// frame — a changed value is substituted, never served stale.
/// </summary>
public sealed class RealtimeTemplateCache
{
    private const int Capacity = 256;

    // Leading short ID token (CJK/alnum/_/-, no spaces). Bracketed form ("[ID] msg")
    // needs no colon; a bare token requires a half/fullwidth colon ("ID: msg").
    private static readonly Regex ChatPrefixPattern = new(
        @"^\s*(?:[\[（【(][^\s:：\[\]（）【】()]{1,24}[\]）】)]\s*[:：]?|[^\s:：\[\]（）【】()]{1,24}\s*[:：])\s*(?<body>\S.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DigitRunPattern = new(@"\d+", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<TemplateEntry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<TemplateEntry> _order = new();

    public static bool TrySplitChatPrefix(string text, out string prefix, out string body)
    {
        var match = ChatPrefixPattern.Match(text);
        if (match.Success)
        {
            var bodyGroup = match.Groups["body"];
            prefix = text[..bodyGroup.Index];
            body = text[bodyGroup.Index..];
            return true;
        }

        prefix = string.Empty;
        body = text;
        return false;
    }

    /// <summary>
    /// Returns a translation by substituting the text's digit runs into a stored
    /// template, or null when no template matches.
    /// </summary>
    public string? TryApply(string text, string scopeKey)
    {
        var numbers = ExtractDigitRuns(text);
        if (numbers.Count == 0)
        {
            return null;
        }

        var key = BuildKey(text, scopeKey);
        TemplateEntry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                return null;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            entry = node.Value;
        }

        if (entry.SlotCount != numbers.Count)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < entry.Pieces.Count; index++)
        {
            builder.Append(entry.Pieces[index]);
            if (index < entry.SlotOrder.Count)
            {
                builder.Append(numbers[entry.SlotOrder[index]]);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Attempts to learn a template from a finished translation. Succeeds only when
    /// the translation's digit-run sequence is exactly the source's (same order and
    /// values), which makes positional substitution safe without trusting the model.
    /// </summary>
    public bool TryLearn(string sourceText, string translatedText, string scopeKey)
    {
        var sourceNumbers = ExtractDigitRuns(sourceText);
        if (sourceNumbers.Count == 0 || string.IsNullOrWhiteSpace(translatedText))
        {
            return false;
        }

        var translationRuns = DigitRunPattern.Matches(translatedText);
        if (translationRuns.Count != sourceNumbers.Count)
        {
            return false;
        }

        for (var index = 0; index < translationRuns.Count; index++)
        {
            if (!string.Equals(translationRuns[index].Value, sourceNumbers[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        // Split the translation around its digit runs; slot i takes source number i.
        var pieces = new List<string>();
        var slotOrder = new List<int>();
        var position = 0;
        for (var index = 0; index < translationRuns.Count; index++)
        {
            var run = translationRuns[index];
            pieces.Add(translatedText[position..run.Index]);
            slotOrder.Add(index);
            position = run.Index + run.Length;
        }

        pieces.Add(translatedText[position..]);

        var entry = new TemplateEntry(BuildKey(sourceText, scopeKey), pieces, slotOrder, sourceNumbers.Count);
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Key, out var existing))
            {
                _order.Remove(existing);
            }

            var node = _order.AddFirst(entry);
            _entries[entry.Key] = node;

            while (_entries.Count > Capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
        }

        return true;
    }

    private static string BuildKey(string text, string scopeKey)
    {
        // Mask each digit run with a slot marker so "a1b" and "a12b" share a key
        // while "a1b2" (two slots) stays distinct.
        var masked = DigitRunPattern.Replace(text, "");
        return scopeKey + "" + TranslationCacheKey.NormalizeText(masked);
    }

    private static List<string> ExtractDigitRuns(string text)
    {
        var values = new List<string>();
        foreach (Match match in DigitRunPattern.Matches(text))
        {
            values.Add(match.Value);
        }

        return values;
    }

    private sealed record TemplateEntry(
        string Key,
        IReadOnlyList<string> Pieces,
        IReadOnlyList<int> SlotOrder,
        int SlotCount);
}
