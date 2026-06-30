using System.Text.RegularExpressions;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

/// <summary>
/// Rule-based cleanup between OCR and translation for realtime (region) pipelines.
/// Fixed overlays such as watermarks and channel names are dropped before the text
/// reaches the translation cache, so cache/template keys stay based on the actual
/// subtitle body. Two phases:
/// - <see cref="DropBlocks"/> runs BEFORE block merging and removes whole blocks
///   (exclude-ROI hits and full-text pattern hits).
/// - <see cref="StripText"/> runs AFTER block merging and removes pattern matches
///   that were concatenated into surviving block text.
/// </summary>
public static class RealtimeOcrTextPolicy
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public sealed record Options(
        IReadOnlyList<OcrNormalizedRegion> ExcludeRegions,
        IReadOnlyList<string> DropPatterns)
    {
        public bool IsEmpty => ExcludeRegions.Count == 0 && DropPatterns.Count == 0;

        public static Options Create(
            IReadOnlyList<OcrNormalizedRegion>? excludeRegions,
            IReadOnlyList<string>? dropPatterns)
            => new(
                (excludeRegions ?? Array.Empty<OcrNormalizedRegion>())
                    .Where(IsUsableRegion)
                    .ToArray(),
                (dropPatterns ?? Array.Empty<string>())
                    .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                    .Select(pattern => pattern.Trim())
                    .ToArray());
    }

    public static (OcrDocumentResult Document, IReadOnlyList<OcrSegmentTranslation> Dropped) DropBlocks(
        OcrDocumentResult document,
        Options options)
    {
        if (options.IsEmpty)
        {
            return (document, Array.Empty<OcrSegmentTranslation>());
        }

        var patterns = CompilePatterns(options.DropPatterns);
        var dropped = new List<OcrSegmentTranslation>();
        var pages = new List<OcrPageResult>();
        foreach (var page in document.Pages)
        {
            var pageSize = InferPageSize(page);
            var blocks = new List<OcrBlock>();
            foreach (var block in page.Blocks)
            {
                var kept = FilterBlock(block, pageSize, options.ExcludeRegions, patterns, dropped);
                if (kept is not null)
                {
                    blocks.Add(kept);
                }
            }

            pages.Add(page with { Blocks = blocks });
        }

        return (document with { Pages = pages }, dropped);
    }

    public static OcrDocumentResult StripText(OcrDocumentResult document, Options options)
    {
        if (options.DropPatterns.Count == 0)
        {
            return document;
        }

        var patterns = CompilePatterns(options.DropPatterns);
        if (patterns.Count == 0)
        {
            return document;
        }

        var pages = document.Pages
            .Select(page => page with { Blocks = page.Blocks.Select(block => StripBlock(block, patterns)).ToArray() })
            .ToList();
        return document with { Pages = pages };
    }

    private static OcrBlock? FilterBlock(
        OcrBlock block,
        (double Width, double Height)? pageSize,
        IReadOnlyList<OcrNormalizedRegion> excludeRegions,
        IReadOnlyList<Regex> patterns,
        List<OcrSegmentTranslation> dropped)
    {
        if (IsExcludedByRegion(block.BoundingBox, pageSize, excludeRegions) ||
            MatchesWholeText(block.Text, patterns))
        {
            dropped.Add(DroppedSegment(block.Id, block.Type, block.Text));
            return null;
        }

        if (block.Children.Count == 0)
        {
            return block;
        }

        var children = new List<OcrBlock>();
        foreach (var child in block.Children)
        {
            var kept = FilterBlock(child, pageSize, excludeRegions, patterns, dropped);
            if (kept is not null)
            {
                children.Add(kept);
            }
        }

        return block with { Children = children };
    }

    private static OcrBlock StripBlock(OcrBlock block, IReadOnlyList<Regex> patterns)
    {
        var children = block.Children.Count == 0
            ? block.Children
            : block.Children.Select(child => StripBlock(child, patterns)).ToArray();

        if (string.IsNullOrWhiteSpace(block.Text))
        {
            return block with { Children = children };
        }

        var stripped = StripPatterns(block.Text, patterns);
        if (string.Equals(stripped, block.Text, StringComparison.Ordinal))
        {
            return block with { Children = children };
        }

        var sourceText = string.IsNullOrWhiteSpace(block.SourceText) ? block.Text : block.SourceText;
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return block with
            {
                Children = children,
                SourceText = sourceText,
                Text = string.Empty,
                ShouldTranslate = false
            };
        }

        return block with { Children = children, SourceText = sourceText, Text = stripped };
    }

    private static string StripPatterns(string text, IReadOnlyList<Regex> patterns)
    {
        var lines = text.Split('\n');
        var result = new List<string>();
        foreach (var rawLine in lines)
        {
            var line = rawLine;
            foreach (var pattern in patterns)
            {
                try
                {
                    line = pattern.Replace(line, " ");
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }

            line = CollapseWhitespace(line);
            if (line.Length > 0)
            {
                result.Add(line);
            }
        }

        return string.Join("\n", result);
    }

    private static bool MatchesWholeText(string text, IReadOnlyList<Regex> patterns)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            try
            {
                var match = pattern.Match(trimmed);
                if (match.Success && match.Length == trimmed.Length)
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return false;
    }

    private static bool IsExcludedByRegion(
        OcrBoundingBox? box,
        (double Width, double Height)? pageSize,
        IReadOnlyList<OcrNormalizedRegion> excludeRegions)
    {
        if (box is null || pageSize is null || excludeRegions.Count == 0)
        {
            return false;
        }

        var (pageWidth, pageHeight) = pageSize.Value;
        var x = box.X / pageWidth;
        var y = box.Y / pageHeight;
        var width = Math.Max(box.Width, 1) / pageWidth;
        var height = Math.Max(box.Height, 1) / pageHeight;
        var centerX = x + (width / 2);
        var centerY = y + (height / 2);
        var area = width * height;

        foreach (var region in excludeRegions)
        {
            var centerInside = centerX >= region.X && centerX <= region.X + region.Width &&
                centerY >= region.Y && centerY <= region.Y + region.Height;
            if (centerInside)
            {
                return true;
            }

            var overlapWidth = Math.Min(x + width, region.X + region.Width) - Math.Max(x, region.X);
            var overlapHeight = Math.Min(y + height, region.Y + region.Height) - Math.Max(y, region.Y);
            if (overlapWidth > 0 && overlapHeight > 0 && area > 0 &&
                overlapWidth * overlapHeight / area >= 0.5)
            {
                return true;
            }
        }

        return false;
    }

    private static (double Width, double Height)? InferPageSize(OcrPageResult page)
    {
        double width = Math.Max(0, page.Width ?? 0);
        double height = Math.Max(0, page.Height ?? 0);
        foreach (var box in EnumerateBoxes(page.Blocks))
        {
            width = Math.Max(width, box.X + Math.Max(1, box.Width));
            height = Math.Max(height, box.Y + Math.Max(1, box.Height));
        }

        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static IEnumerable<OcrBoundingBox> EnumerateBoxes(IReadOnlyList<OcrBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.BoundingBox is { Width: > 0, Height: > 0 })
            {
                yield return block.BoundingBox;
            }

            foreach (var box in EnumerateBoxes(block.Children))
            {
                yield return box;
            }
        }
    }

    private static IReadOnlyList<Regex> CompilePatterns(IReadOnlyList<string> patterns)
    {
        var compiled = new List<Regex>(patterns.Count);
        foreach (var pattern in patterns)
        {
            try
            {
                compiled.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout));
            }
            catch (ArgumentException)
            {
                compiled.Add(new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout));
            }
        }

        return compiled;
    }

    private static string CollapseWhitespace(string value)
        => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static bool IsUsableRegion(OcrNormalizedRegion region)
        => region is { Width: > 0, Height: > 0 } &&
           region.X >= 0 && region.Y >= 0 &&
           region.X < 1 && region.Y < 1 &&
           double.IsFinite(region.X) && double.IsFinite(region.Y) &&
           double.IsFinite(region.Width) && double.IsFinite(region.Height);

    private static OcrSegmentTranslation DroppedSegment(string id, string type, string text)
        => new(
            id,
            type,
            text,
            string.Empty,
            Translated: false,
            "policy:drop",
            0,
            CacheHit: false,
            "0",
            string.Empty);
}
