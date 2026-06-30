using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class OcrTranslationBlockMergerTests
{
    [Fact]
    public void Merge_CombinesTranslatableTextBlocksIntoOne()
    {
        var document = new OcrDocumentResult
        {
            Pages =
            [
                new OcrPageResult
                {
                    PageIndex = 0,
                    Blocks =
                    [
                        TextBlock("b0", "今日はいい天気ですね", 0, new OcrBoundingBox(10, 10, 200, 24), confidence: 0.9),
                        TextBlock("b1", "散歩に行きましょう", 1, new OcrBoundingBox(10, 40, 180, 24), confidence: 0.8)
                    ]
                }
            ]
        };

        var merged = OcrTranslationBlockMerger.Merge(document);
        var blocks = merged.Pages[0].Blocks;

        Assert.Single(blocks);
        Assert.Equal("今日はいい天気ですね\n散歩に行きましょう", blocks[0].Text);
        Assert.Equal(0.8, blocks[0].Confidence);
        Assert.Equal(new OcrBoundingBox(10, 10, 200, 54), blocks[0].BoundingBox);
        Assert.Equal(4, blocks[0].Polygon.Count);
    }

    [Fact]
    public void Merge_KeepsNonTranslatableBlocksSeparate()
    {
        var document = new OcrDocumentResult
        {
            Pages =
            [
                new OcrPageResult
                {
                    PageIndex = 0,
                    Blocks =
                    [
                        TextBlock("b0", "line one", 0, null),
                        TextBlock("b1", "line two", 1, null),
                        TextBlock("b2", "x = 1", 2, null) with
                        {
                            Type = OcrBlockTypes.Formula,
                            ShouldTranslate = false
                        }
                    ]
                }
            ]
        };

        var merged = OcrTranslationBlockMerger.Merge(document);
        var blocks = merged.Pages[0].Blocks;

        Assert.Equal(2, blocks.Count);
        Assert.Equal("line one\nline two", blocks[0].Text);
        Assert.Equal("x = 1", blocks[1].Text);
        Assert.False(blocks[1].ShouldTranslate);
    }

    [Fact]
    public void Merge_DoesNotCrossStructureBoundaries()
    {
        var document = new OcrDocumentResult
        {
            Pages =
            [
                new OcrPageResult
                {
                    PageIndex = 0,
                    Blocks =
                    [
                        TextBlock("b0", "before one", 0, null),
                        TextBlock("b1", "before two", 1, null),
                        TextBlock("f0", "x = 1", 2, null) with
                        {
                            Type = OcrBlockTypes.Formula,
                            ShouldTranslate = false
                        },
                        TextBlock("b2", "after one", 3, null),
                        TextBlock("b3", "after two", 4, null)
                    ]
                }
            ]
        };

        var merged = OcrTranslationBlockMerger.Merge(document);
        var blocks = merged.Pages[0].Blocks;

        Assert.Equal(3, blocks.Count);
        Assert.Equal("before one\nbefore two", blocks[0].Text);
        Assert.Equal("x = 1", blocks[1].Text);
        Assert.Equal("after one\nafter two", blocks[2].Text);
        Assert.False(blocks[1].ShouldTranslate);
    }

    [Fact]
    public void Merge_LeavesSingleBlockDocumentsUntouched()
    {
        var document = new OcrDocumentResult
        {
            Pages =
            [
                new OcrPageResult
                {
                    PageIndex = 0,
                    Blocks = [TextBlock("b0", "only line", 0, new OcrBoundingBox(0, 0, 50, 10))]
                }
            ]
        };

        var merged = OcrTranslationBlockMerger.Merge(document);

        Assert.Same(document, merged);
    }

    [Fact]
    public void Merge_OrdersByReadingOrderBeforeJoining()
    {
        var document = new OcrDocumentResult
        {
            Pages =
            [
                new OcrPageResult
                {
                    PageIndex = 0,
                    Blocks =
                    [
                        TextBlock("b1", "second", 1, null),
                        TextBlock("b0", "first", 0, null)
                    ]
                }
            ]
        };

        var merged = OcrTranslationBlockMerger.Merge(document);

        Assert.Equal("first\nsecond", merged.Pages[0].Blocks[0].Text);
    }

    private static OcrBlock TextBlock(
        string id,
        string text,
        int readingOrder,
        OcrBoundingBox? box,
        double confidence = 1.0)
        => new()
        {
            Id = id,
            Type = OcrBlockTypes.Text,
            Text = text,
            Confidence = confidence,
            BoundingBox = box,
            ReadingOrder = readingOrder,
            Engine = "test",
            ShouldTranslate = true
        };
}
