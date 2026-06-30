using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

/// <summary>
/// Merges adjacent translatable text blocks on each page into a single block so the
/// translation stage issues one request with full cross-line context instead of one
/// request per detected line. Intended for subtitle/region captures where the OCR
/// engine splits one utterance into multiple line blocks.
/// </summary>
public static class OcrTranslationBlockMerger
{
    public static OcrDocumentResult Merge(OcrDocumentResult document)
    {
        var changed = false;
        var pages = document.Pages
            .Select(page =>
            {
                var merged = MergePage(page);
                changed |= !ReferenceEquals(merged, page);
                return merged;
            })
            .ToArray();

        return changed ? document with { Pages = pages } : document;
    }

    private static OcrPageResult MergePage(OcrPageResult page)
    {
        if (page.Blocks.Count < 2)
        {
            return page;
        }

        var changed = false;
        var blocks = new List<OcrBlock>();
        var pending = new List<OcrBlock>();

        foreach (var block in page.Blocks.OrderBy(block => block.ReadingOrder))
        {
            if (IsMergeable(block))
            {
                pending.Add(block);
                continue;
            }

            FlushPending(pending, blocks, ref changed);
            blocks.Add(block);
        }

        FlushPending(pending, blocks, ref changed);
        return changed
            ? page with { Blocks = blocks.ToArray() }
            : page;
    }

    private static void FlushPending(
        List<OcrBlock> pending,
        List<OcrBlock> output,
        ref bool changed)
    {
        if (pending.Count == 0)
        {
            return;
        }

        if (pending.Count == 1)
        {
            output.Add(pending[0]);
            pending.Clear();
            return;
        }

        output.Add(MergeGroup(pending));
        pending.Clear();
        changed = true;
    }

    private static OcrBlock MergeGroup(IReadOnlyList<OcrBlock> blocks)
    {
        var union = UnionBoundingBox(blocks);
        return blocks[0] with
        {
            Text = string.Join("\n", blocks.Select(block => block.Text)),
            Confidence = blocks.Min(block => block.Confidence),
            BoundingBox = union,
            Polygon = ToPolygon(union)
        };
    }

    private static bool IsMergeable(OcrBlock block)
        => block.ShouldTranslate &&
           block.Table is null &&
           block.Formula is null &&
           block.Children.Count == 0 &&
           !string.IsNullOrWhiteSpace(block.Text);

    private static OcrBoundingBox? UnionBoundingBox(IReadOnlyList<OcrBlock> blocks)
    {
        var boxes = blocks
            .Select(block => block.BoundingBox)
            .OfType<OcrBoundingBox>()
            .ToArray();
        if (boxes.Length == 0)
        {
            return null;
        }

        var minX = boxes.Min(box => box.X);
        var minY = boxes.Min(box => box.Y);
        var maxX = boxes.Max(box => box.X + box.Width);
        var maxY = boxes.Max(box => box.Y + box.Height);
        return new OcrBoundingBox(minX, minY, maxX - minX, maxY - minY);
    }

    private static IReadOnlyList<OcrPoint> ToPolygon(OcrBoundingBox? box)
    {
        if (box is null)
        {
            return Array.Empty<OcrPoint>();
        }

        return
        [
            new OcrPoint(box.X, box.Y),
            new OcrPoint(box.X + box.Width, box.Y),
            new OcrPoint(box.X + box.Width, box.Y + box.Height),
            new OcrPoint(box.X, box.Y + box.Height)
        ];
    }
}
