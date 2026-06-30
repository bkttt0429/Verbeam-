using System.Globalization;

namespace Verbeam.Core.Services;

/// <summary>
/// Parses a human-entered, 1-based page range specification (for example
/// "1-20", "1,3,8-12", "5-", "-3") into a sorted, de-duplicated list of 0-based
/// page indices clamped to the document's page count.
/// <para>
/// Blank/null, "auto" or "all" select every page. A non-blank spec whose tokens
/// are <em>all</em> unparseable (e.g. "abc") is treated as "no filter" and also
/// selects every page, so a typo never silently drops the whole document. A
/// syntactically valid spec that resolves to no in-range page (e.g. "999" of a
/// 3-page file) returns an empty list — the caller decides how to surface that.
/// </para>
/// </summary>
public static class PageRangeParser
{
    public static IReadOnlyList<int> Parse(string? spec, int pageCount)
    {
        if (pageCount <= 0)
        {
            return Array.Empty<int>();
        }

        var all = Enumerable.Range(0, pageCount).ToArray();
        if (string.IsNullOrWhiteSpace(spec))
        {
            return all;
        }

        var normalized = spec.Trim();
        if (normalized.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return all;
        }

        var selected = new SortedSet<int>();
        var anyValidToken = false;
        foreach (var token in normalized.Split(
            new[] { ',', ';', ' ', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseToken(token.Trim(), pageCount, out var startOneBased, out var endOneBased))
            {
                continue;
            }

            anyValidToken = true;
            for (var page = startOneBased; page <= endOneBased; page++)
            {
                if (page >= 1 && page <= pageCount)
                {
                    selected.Add(page - 1);
                }
            }
        }

        return anyValidToken ? selected.ToArray() : all;
    }

    private static bool TryParseToken(string token, int pageCount, out int startOneBased, out int endOneBased)
    {
        startOneBased = 0;
        endOneBased = 0;
        if (token.Length == 0)
        {
            return false;
        }

        var dash = token.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
            {
                return false;
            }

            startOneBased = single;
            endOneBased = single;
            return true;
        }

        var leftText = token[..dash].Trim();
        var rightText = token[(dash + 1)..].Trim();
        var hasLeft = int.TryParse(leftText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var left);
        var hasRight = int.TryParse(rightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var right);
        if (!hasLeft && !hasRight)
        {
            return false;
        }

        if (!hasLeft)
        {
            left = 1;
        }

        if (!hasRight)
        {
            right = pageCount;
        }

        if (left > right)
        {
            (left, right) = (right, left);
        }

        startOneBased = left;
        endOneBased = right;
        return true;
    }
}
