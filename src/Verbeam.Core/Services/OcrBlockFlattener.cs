using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

/// <summary>
/// Flattens a page's block tree into the leaf translation units the PDF overlay editor and
/// exporters actually place: standalone blocks, recursively-nested child blocks, and table
/// cells. Container blocks (those with children or a table) are not emitted themselves — only
/// their leaves — so nothing is double-drawn. Table cells become synthetic blocks keyed
/// <c>{blockId}#{cellId}</c> carrying the cell's bbox + translated text.
/// </summary>
public static class OcrBlockFlattener
{
    public static IEnumerable<OcrBlock> Flatten(OcrPageResult page)
    {
        foreach (var block in page.Blocks)
        {
            foreach (var unit in FlattenBlock(block))
            {
                yield return unit;
            }
        }
    }

    private static IEnumerable<OcrBlock> FlattenBlock(OcrBlock block)
    {
        var isContainer = block.Children.Count > 0 || block.Table is not null;
        if (!isContainer)
        {
            yield return block;
            yield break;
        }

        foreach (var child in block.Children)
        {
            foreach (var unit in FlattenBlock(child))
            {
                yield return unit;
            }
        }

        if (block.Table is null)
        {
            yield break;
        }

        foreach (var cell in block.Table.Cells)
        {
            if (cell.BoundingBox is null || string.IsNullOrWhiteSpace(cell.Text))
            {
                continue;
            }

            var cellId = string.IsNullOrWhiteSpace(cell.Id) ? $"r{cell.RowIndex}c{cell.ColumnIndex}" : cell.Id;
            yield return new OcrBlock
            {
                Id = $"{block.Id}#{cellId}",
                Type = "table-cell",
                Text = cell.Text,
                SourceText = cell.SourceText,
                Confidence = cell.Confidence,
                BoundingBox = cell.BoundingBox,
                Polygon = cell.Polygon,
                ReadingOrder = block.ReadingOrder,
                Engine = block.Engine,
                ShouldTranslate = cell.ShouldTranslate,
                DetectedLanguage = cell.DetectedLanguage
            };
        }
    }
}
