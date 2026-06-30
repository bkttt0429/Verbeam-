using System.Text.RegularExpressions;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

/// <summary>
/// Multi-pass OCR helpers for unstable scans: a single full-image pass often
/// drops CJK text next to Latin anchors (e.g. "(Compile)" without the leading
/// "編譯") or misreads low-confidence blocks, while re-running OCR on the
/// cropped block with the text-line preset reads it correctly. These pure
/// functions pick the suspect blocks, map their boxes back to original image
/// pixels, and decide whether a refined reading should replace the original.
/// </summary>
public static class OcrBlockRefinement
{
    private static readonly Regex LatinAnchorPattern = new(
        @"\(\s*[A-Za-z][A-Za-z0-9 ._\-]*\s*\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public sealed record SuspectBlock(string Id, string Text, double Confidence, OcrBoundingBox BoundingBox);

    public static IReadOnlyList<SuspectBlock> FindSuspectBlocks(OcrDocumentResult document, OcrRefinementOptions options)
    {
        var suspects = new List<SuspectBlock>();
        foreach (var page in document.Pages)
        {
            CollectSuspects(page.Blocks, options, suspects);
        }

        return suspects
            .OrderBy(block => block.Confidence)
            .Take(Math.Max(0, options.MaxBlocks))
            .ToArray();
    }

    public static bool IsSuspectText(string text, double confidence, OcrRefinementOptions options)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (confidence < options.TriggerConfidence)
        {
            return true;
        }

        return HasLatinAnchor(text) && CountCjk(text) == 0;
    }

    public static bool HasLatinAnchor(string text)
    {
        try
        {
            return LatinAnchorPattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static int CountCjk(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if ((value >= 0x4E00 && value <= 0x9FFF) ||   // CJK Unified Ideographs
                (value >= 0x3400 && value <= 0x4DBF) ||   // Extension A
                (value >= 0xF900 && value <= 0xFAFF) ||   // Compatibility Ideographs
                (value >= 0x3040 && value <= 0x30FF) ||   // Hiragana + Katakana
                (value >= 0xAC00 && value <= 0xD7AF))     // Hangul
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Maps a block box (possibly in a preprocessed/scaled coordinate space) back
    /// to original-image pixels and pads it. Page width/height define the box
    /// coordinate space; when missing, the caller should pass the inferred extent.
    /// Returns null when the box cannot produce a usable crop.
    /// </summary>
    public static OcrBoundingBox? ComputeCropRectangle(
        OcrBoundingBox box,
        double pageWidth,
        double pageHeight,
        int imageWidth,
        int imageHeight,
        double paddingRatio)
    {
        if (pageWidth <= 0 || pageHeight <= 0 || imageWidth <= 0 || imageHeight <= 0 ||
            box.Width <= 0 || box.Height <= 0)
        {
            return null;
        }

        var scaleX = imageWidth / pageWidth;
        var scaleY = imageHeight / pageHeight;
        var x = box.X * scaleX;
        var y = box.Y * scaleY;
        var width = box.Width * scaleX;
        var height = box.Height * scaleY;

        var padX = Math.Max(4, width * paddingRatio);
        var padY = Math.Max(4, height * paddingRatio);
        var left = (int)Math.Floor(Math.Clamp(x - padX, 0, imageWidth - 1));
        var top = (int)Math.Floor(Math.Clamp(y - padY, 0, imageHeight - 1));
        var right = (int)Math.Ceiling(Math.Clamp(x + width + padX, left + 1, imageWidth));
        var bottom = (int)Math.Ceiling(Math.Clamp(y + height + padY, top + 1, imageHeight));

        var cropWidth = right - left;
        var cropHeight = bottom - top;
        return cropWidth >= 4 && cropHeight >= 4
            ? new OcrBoundingBox(left, top, cropWidth, cropHeight)
            : null;
    }

    /// <summary>
    /// Infers the coordinate space of block boxes: explicit page size when
    /// present, otherwise the maximum box extent across the page.
    /// </summary>
    public static (double Width, double Height)? InferPageSize(OcrPageResult page)
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

    public static bool ShouldAcceptRefinement(
        string originalText,
        double originalConfidence,
        string refinedText,
        double refinedConfidence,
        OcrRefinementOptions options)
    {
        var refined = refinedText?.Trim() ?? string.Empty;
        if (refined.Length == 0)
        {
            return false;
        }

        // Anchor merge: the original captured only the Latin anchor (no CJK);
        // accept a refined reading that keeps the anchor and recovers CJK text.
        var original = originalText?.Trim() ?? string.Empty;
        if (HasLatinAnchor(original) && CountCjk(original) == 0 &&
            CountCjk(refined) > 0 && ContainsAnchorOf(original, refined))
        {
            return true;
        }

        return refinedConfidence >= originalConfidence + options.MinGain;
    }

    private static bool ContainsAnchorOf(string original, string refined)
    {
        try
        {
            foreach (Match match in LatinAnchorPattern.Matches(original))
            {
                var anchorWord = new string(match.Value.Where(char.IsLetterOrDigit).ToArray());
                if (anchorWord.Length == 0)
                {
                    continue;
                }

                var refinedCompact = new string(refined.Where(char.IsLetterOrDigit).ToArray());
                if (refinedCompact.Contains(anchorWord, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        return false;
    }

    public static OcrDocumentResult ApplyRefinement(
        OcrDocumentResult document,
        string blockId,
        string refinedText,
        double refinedConfidence)
    {
        var pages = document.Pages
            .Select(page => page with
            {
                Blocks = page.Blocks.Select(block => RefineBlock(block, blockId, refinedText, refinedConfidence)).ToArray()
            })
            .ToArray();
        return document with { Pages = pages };
    }

    private static OcrBlock RefineBlock(OcrBlock block, string blockId, string refinedText, double refinedConfidence)
    {
        if (string.Equals(block.Id, blockId, StringComparison.Ordinal))
        {
            return block with
            {
                Text = refinedText,
                Confidence = refinedConfidence,
                Engine = string.IsNullOrWhiteSpace(block.Engine) ? "refined" : block.Engine + "+refined"
            };
        }

        if (block.Children.Count == 0)
        {
            return block;
        }

        return block with
        {
            Children = block.Children.Select(child => RefineBlock(child, blockId, refinedText, refinedConfidence)).ToArray()
        };
    }

    private static void CollectSuspects(
        IReadOnlyList<OcrBlock> blocks,
        OcrRefinementOptions options,
        List<SuspectBlock> suspects)
    {
        foreach (var block in blocks)
        {
            if (block.BoundingBox is { Width: > 0, Height: > 0 } box &&
                block.ShouldTranslate &&
                IsSuspectText(block.Text, block.Confidence, options))
            {
                suspects.Add(new SuspectBlock(block.Id, block.Text, block.Confidence, box));
            }

            if (block.Children.Count > 0)
            {
                CollectSuspects(block.Children, options, suspects);
            }
        }
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
}
