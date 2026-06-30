using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

/// <summary>
/// Cross-frame watermark detector for realtime (region) OCR sessions.
/// Subtitles dwell a few seconds and change; channel watermarks persist across
/// frames even when OCR misreads them slightly differently each time. Text that
/// keeps reappearing (fuzzy-matched, so per-frame misreads cluster together)
/// across a long-enough window of *distinct* frames is flagged and removed from
/// the OCR output before it reaches translation, regardless of where it sits or
/// moves in the frame. Manual exclude regions / drop patterns
/// (<see cref="RealtimeOcrTextPolicy"/>) stay independent of this.
/// </summary>
public sealed class RecurringTextSuppressor
{
    public sealed record Result(
        string Text,
        IReadOnlyList<OcrTextBlock> Blocks,
        OcrDocumentResult? Document,
        IReadOnlyList<string> SuppressedText);

    private sealed class Cluster
    {
        public required string Canonical { get; init; }
        public required List<string> Variants { get; init; }
        public required DateTimeOffset FirstSeenUtc { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public long FirstFrame { get; init; }
        public long LastFrame { get; set; }
        public required Queue<long> SeenFrames { get; init; }
        public bool Flagged { get; set; }
    }

    private sealed class Session
    {
        public string LastImageHash = string.Empty;
        public long FrameIndex;
        public DateTimeOffset LastTouchedUtc;
        public readonly List<Cluster> Clusters = [];
    }

    private const int MaxVariantsPerCluster = 12;

    private readonly OcrRealtimeAutoSuppressOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public RecurringTextSuppressor(VerbeamOptions options, Func<DateTimeOffset>? clock = null)
    {
        _options = options.Ocr.RealtimeAutoSuppress;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Observes one realtime frame and returns the response fields with flagged
    /// recurring text removed. The frame counter only advances when
    /// <paramref name="imageHash"/> differs from the previous frame, so a paused
    /// video (identical frames) never accumulates evidence against its subtitle.
    /// </summary>
    public Result Process(
        string sessionId,
        string imageHash,
        string text,
        IReadOnlyList<OcrTextBlock> blocks,
        OcrDocumentResult? document)
    {
        lock (_lock)
        {
            var now = _clock();
            var session = GetSession(sessionId, now);
            var advance = !string.Equals(session.LastImageHash, imageHash, StringComparison.Ordinal);
            if (advance)
            {
                session.LastImageHash = imageHash;
                session.FrameIndex++;
                Observe(session, CollectLines(text, blocks), now);
            }

            session.LastTouchedUtc = now;
            if (!session.Clusters.Any(cluster => cluster.Flagged))
            {
                return new Result(text, blocks, document, Array.Empty<string>());
            }

            // Blocks first: the flat text is often whitespace-collapsed into a
            // single line, so flagged block texts are then removed from it as
            // substrings rather than whole lines.
            var suppressed = new List<string>();
            var filteredBlocks = FilterBlocks(blocks, session, suppressed);
            var filteredDocument = document is null ? null : FilterDocument(document, session, suppressed);
            var filteredText = FilterText(text, session, suppressed);
            return new Result(filteredText, filteredBlocks, filteredDocument, suppressed);
        }
    }

    private Session GetSession(string sessionId, DateTimeOffset now)
    {
        // Idle sessions are reaped lazily; capture runs mint fresh session ids,
        // so stale entries only ever cost memory, never correctness.
        if (_sessions.Count >= Math.Max(1, _options.MaxSessions))
        {
            var idleCutoff = now - TimeSpan.FromSeconds(Math.Max(1, _options.SessionIdleExpireSeconds));
            foreach (var key in _sessions.Where(pair => pair.Value.LastTouchedUtc < idleCutoff).Select(pair => pair.Key).ToArray())
            {
                _sessions.Remove(key);
            }
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            if (_sessions.Count >= Math.Max(1, _options.MaxSessions))
            {
                var oldest = _sessions.MinBy(pair => pair.Value.LastTouchedUtc);
                _sessions.Remove(oldest.Key);
            }

            session = new Session { LastTouchedUtc = now };
            _sessions[sessionId] = session;
        }

        return session;
    }

    private void Observe(Session session, IReadOnlyCollection<string> lines, DateTimeOffset now)
    {
        var window = Math.Max(2, _options.WindowFrames);
        foreach (var line in lines)
        {
            var normalized = Normalize(line);
            if (normalized.Length == 0)
            {
                continue;
            }

            var cluster = MatchCluster(session, normalized);
            if (cluster is null)
            {
                if (session.Clusters.Count >= Math.Max(1, _options.MaxClustersPerSession))
                {
                    var oldest = session.Clusters.MinBy(item => item.LastSeenUtc);
                    if (oldest is not null)
                    {
                        session.Clusters.Remove(oldest);
                    }
                }

                session.Clusters.Add(new Cluster
                {
                    Canonical = normalized,
                    Variants = [normalized],
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    FirstFrame = session.FrameIndex,
                    LastFrame = session.FrameIndex,
                    SeenFrames = new Queue<long>([session.FrameIndex])
                });
                continue;
            }

            if (cluster.LastFrame != session.FrameIndex)
            {
                cluster.SeenFrames.Enqueue(session.FrameIndex);
            }

            cluster.LastFrame = session.FrameIndex;
            cluster.LastSeenUtc = now;
            if (!cluster.Variants.Contains(normalized, StringComparer.Ordinal) &&
                cluster.Variants.Count < MaxVariantsPerCluster)
            {
                cluster.Variants.Add(normalized);
            }
        }

        var expireCutoff = now - TimeSpan.FromSeconds(Math.Max(1, _options.ClusterExpireSeconds));
        session.Clusters.RemoveAll(cluster => cluster.LastSeenUtc < expireCutoff);

        foreach (var cluster in session.Clusters)
        {
            while (cluster.SeenFrames.Count > 0 && cluster.SeenFrames.Peek() <= session.FrameIndex - window)
            {
                cluster.SeenFrames.Dequeue();
            }

            // Flag only after a full window of distinct frames has elapsed since
            // first sighting AND the text was present in most of them AND it has
            // outlived the typical subtitle dwell time.
            var observedSpan = session.FrameIndex - cluster.FirstFrame + 1;
            var ageSeconds = (now - cluster.FirstSeenUtc).TotalSeconds;
            cluster.Flagged =
                observedSpan >= window &&
                cluster.SeenFrames.Count >= _options.PresenceRatio * window &&
                ageSeconds >= _options.MinAgeSeconds;
        }
    }

    private Cluster? MatchCluster(Session session, string normalized)
    {
        foreach (var cluster in session.Clusters)
        {
            foreach (var variant in cluster.Variants)
            {
                if (IsSimilar(normalized, variant))
                {
                    return cluster;
                }
            }
        }

        return null;
    }

    private bool IsSimilar(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        // Short strings have too few bigrams for a meaningful Dice score; a
        // 2-3 char subtitle word must not fuzzy-merge into a watermark cluster.
        if (left.Length < 4 || right.Length < 4)
        {
            return false;
        }

        return DiceCoefficient(left, right) >= _options.Similarity;
    }

    private static double DiceCoefficient(string left, string right)
    {
        var leftBigrams = Bigrams(left);
        var rightBigrams = Bigrams(right);
        if (leftBigrams.Count == 0 || rightBigrams.Count == 0)
        {
            return 0;
        }

        var intersection = leftBigrams.Count(rightBigrams.Contains);
        return 2.0 * intersection / (leftBigrams.Count + rightBigrams.Count);
    }

    private static HashSet<string> Bigrams(string value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < value.Length; i++)
        {
            result.Add(value.Substring(i, 2));
        }

        return result;
    }

    /// <summary>NFKC + lowercase + letters/digits only, so per-frame OCR noise in
    /// punctuation/spacing never splits a cluster.</summary>
    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private bool IsFlagged(Session session, string line)
    {
        var normalized = Normalize(line);
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (var cluster in session.Clusters)
        {
            if (!cluster.Flagged)
            {
                continue;
            }

            foreach (var variant in cluster.Variants)
            {
                if (IsSimilar(normalized, variant))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string FilterLines(string text, Session session, List<string> suppressed)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var kept = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (IsFlagged(session, line))
            {
                AddSuppressed(suppressed, line);
            }
            else
            {
                kept.Add(line);
            }
        }

        return string.Join("\n", kept);
    }

    private string FilterText(string text, Session session, List<string> suppressed)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var kept = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            // The whitespace-normalized flat text concatenates block texts into
            // one line; strip the suppressed block texts FIRST, then judge only
            // the remainder — a "watermark + short subtitle" line as a whole can
            // score similar to the watermark, but the subtitle must survive.
            var value = line;
            foreach (var fragment in suppressed)
            {
                int index;
                while (fragment.Length > 0 && (index = value.IndexOf(fragment, StringComparison.Ordinal)) >= 0)
                {
                    value = value.Remove(index, fragment.Length);
                }
            }

            if (!string.Equals(value, line, StringComparison.Ordinal))
            {
                value = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }

            if (IsFlagged(session, value))
            {
                AddSuppressed(suppressed, value);
                continue;
            }

            if (value.Trim().Length > 0)
            {
                kept.Add(value);
            }
        }

        return string.Join("\n", kept);
    }

    private IReadOnlyList<OcrTextBlock> FilterBlocks(
        IReadOnlyList<OcrTextBlock> blocks,
        Session session,
        List<string> suppressed)
    {
        if (blocks.Count == 0)
        {
            return blocks;
        }

        var kept = new List<OcrTextBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            var filtered = FilterLines(block.Text, session, suppressed);
            if (filtered.Trim().Length == 0 && block.Text.Trim().Length > 0)
            {
                continue;
            }

            kept.Add(string.Equals(filtered, block.Text, StringComparison.Ordinal)
                ? block
                : block with { Text = filtered });
        }

        return kept;
    }

    private OcrDocumentResult FilterDocument(
        OcrDocumentResult document,
        Session session,
        List<string> suppressed)
    {
        var pages = document.Pages
            .Select(page => page with
            {
                Blocks = page.Blocks
                    .Select(block => FilterDocumentBlock(block, session, suppressed))
                    .Where(block => block is not null)
                    .Select(block => block!)
                    .ToArray()
            })
            .ToList();
        return document with { Pages = pages };
    }

    private OcrBlock? FilterDocumentBlock(OcrBlock block, Session session, List<string> suppressed)
    {
        var children = block.Children.Count == 0
            ? block.Children
            : block.Children
                .Select(child => FilterDocumentBlock(child, session, suppressed))
                .Where(child => child is not null)
                .Select(child => child!)
                .ToArray();

        var filtered = FilterLines(block.Text, session, suppressed);
        if (string.Equals(filtered, block.Text, StringComparison.Ordinal))
        {
            return block with { Children = children };
        }

        if (filtered.Trim().Length == 0)
        {
            return children.Count == 0 ? null : block with
            {
                Children = children,
                SourceText = string.IsNullOrWhiteSpace(block.SourceText) ? block.Text : block.SourceText,
                Text = string.Empty,
                ShouldTranslate = false
            };
        }

        return block with
        {
            Children = children,
            SourceText = string.IsNullOrWhiteSpace(block.SourceText) ? block.Text : block.SourceText,
            Text = filtered
        };
    }

    private static void AddSuppressed(List<string> suppressed, string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length > 0 && !suppressed.Contains(trimmed, StringComparer.Ordinal))
        {
            suppressed.Add(trimmed);
        }
    }

    private static IReadOnlyCollection<string> CollectLines(string text, IReadOnlyList<OcrTextBlock> blocks)
    {
        var lines = new List<string>();
        if (blocks.Count > 0)
        {
            foreach (var block in blocks)
            {
                lines.AddRange(block.Text.Split('\n'));
            }
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            lines.AddRange(text.Split('\n'));
        }

        return lines;
    }
}
