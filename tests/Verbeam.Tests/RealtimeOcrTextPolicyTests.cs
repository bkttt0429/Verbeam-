using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class RealtimeOcrTextPolicyTests
{
    private static OcrBlock Block(string id, string text, OcrBoundingBox? box = null)
        => new()
        {
            Id = id,
            Type = OcrBlockTypes.Text,
            Text = text,
            BoundingBox = box,
            ShouldTranslate = true
        };

    private static OcrDocumentResult Document(params OcrBlock[] blocks)
        => new()
        {
            Pages = [new OcrPageResult { PageIndex = 0, Width = 1000, Height = 500, Blocks = blocks }]
        };

    private static RealtimeOcrTextPolicy.Options Options(
        IReadOnlyList<OcrNormalizedRegion>? regions = null,
        IReadOnlyList<string>? patterns = null)
        => RealtimeOcrTextPolicy.Options.Create(regions, patterns);

    [Fact]
    public void DropBlocks_RemovesBlockWhoseCenterIsInsideExcludeRegion()
    {
        var subtitle = Block("b1", "字幕本文", new OcrBoundingBox(100, 400, 600, 60));
        var watermark = Block("b2", "麦迪带你看漫画", new OcrBoundingBox(850, 10, 140, 40));
        var options = Options(regions: [new OcrNormalizedRegion(0.8, 0, 0.2, 0.2)]);

        var (document, dropped) = RealtimeOcrTextPolicy.DropBlocks(Document(subtitle, watermark), options);

        var blocks = document.Pages[0].Blocks;
        Assert.Single(blocks);
        Assert.Equal("b1", blocks[0].Id);
        var droppedSegment = Assert.Single(dropped);
        Assert.Equal("b2", droppedSegment.Id);
        Assert.Equal("policy:drop", droppedSegment.Engine);
        Assert.False(droppedSegment.Translated);
    }

    [Fact]
    public void DropBlocks_RemovesBlockWithMajorityOverlap()
    {
        // Block center sits outside the region, but >=50% of its area overlaps.
        var block = Block("b1", "watermark", new OcrBoundingBox(150, 0, 100, 100));
        var options = Options(regions: [new OcrNormalizedRegion(0, 0, 0.2, 0.2)]);

        var (document, dropped) = RealtimeOcrTextPolicy.DropBlocks(Document(block), options);

        Assert.Empty(document.Pages[0].Blocks);
        Assert.Single(dropped);
    }

    [Fact]
    public void DropBlocks_KeepsBlockOutsideExcludeRegion()
    {
        var block = Block("b1", "字幕", new OcrBoundingBox(100, 400, 600, 60));
        var options = Options(regions: [new OcrNormalizedRegion(0.8, 0, 0.2, 0.2)]);

        var (document, dropped) = RealtimeOcrTextPolicy.DropBlocks(Document(block), options);

        Assert.Single(document.Pages[0].Blocks);
        Assert.Empty(dropped);
    }

    [Fact]
    public void DropBlocks_IgnoresRegionForBlockWithoutBoundingBox()
    {
        var block = Block("b1", "字幕");
        var options = Options(regions: [new OcrNormalizedRegion(0, 0, 1, 1)]);

        var (document, dropped) = RealtimeOcrTextPolicy.DropBlocks(Document(block), options);

        Assert.Single(document.Pages[0].Blocks);
        Assert.Empty(dropped);
    }

    [Fact]
    public void DropBlocks_RemovesBlockWhoseWholeTextMatchesPattern()
    {
        var subtitle = Block("b1", "隐藏任务调查石山高校背后的真相");
        var watermark = Block("b2", "麦迪带你看漫画");
        var options = Options(patterns: ["麦迪带你看漫画"]);

        var (document, dropped) = RealtimeOcrTextPolicy.DropBlocks(Document(subtitle, watermark), options);

        Assert.Single(document.Pages[0].Blocks);
        Assert.Equal("b1", document.Pages[0].Blocks[0].Id);
        Assert.Equal("b2", Assert.Single(dropped).Id);
    }

    [Fact]
    public void StripText_RemovesPatternConcatenatedIntoSubtitleBlock()
    {
        var merged = Block("b1", "隐藏任务调查石山高校背后的真相 麦迪带你看漫画");
        var options = Options(patterns: ["麦迪带你看漫画"]);

        var document = RealtimeOcrTextPolicy.StripText(Document(merged), options);

        var block = document.Pages[0].Blocks[0];
        Assert.Equal("隐藏任务调查石山高校背后的真相", block.Text);
        Assert.Equal("隐藏任务调查石山高校背后的真相 麦迪带你看漫画", block.SourceText);
        Assert.True(block.ShouldTranslate);
    }

    [Fact]
    public void StripText_RemovesWholeMatchingLine()
    {
        var merged = Block("b1", "字幕第一句\n麦迪带你看漫画\n字幕第二句");
        var options = Options(patterns: ["麦迪带你看漫画"]);

        var document = RealtimeOcrTextPolicy.StripText(Document(merged), options);

        Assert.Equal("字幕第一句\n字幕第二句", document.Pages[0].Blocks[0].Text);
    }

    [Fact]
    public void StripText_EmptiesBlockWhenEverythingMatches()
    {
        var watermark = Block("b1", "麦迪带你看漫画");
        var options = Options(patterns: ["麦迪带你看漫画"]);

        var document = RealtimeOcrTextPolicy.StripText(Document(watermark), options);

        var block = document.Pages[0].Blocks[0];
        Assert.Equal(string.Empty, block.Text);
        Assert.False(block.ShouldTranslate);
        Assert.Equal("麦迪带你看漫画", block.SourceText);
    }

    [Fact]
    public void StripText_TreatsInvalidRegexAsLiteral()
    {
        var block = Block("b1", "字幕 [watermark( 結尾");
        var options = Options(patterns: ["[watermark("]);

        var document = RealtimeOcrTextPolicy.StripText(Document(block), options);

        Assert.Equal("字幕 結尾", document.Pages[0].Blocks[0].Text);
    }

    [Fact]
    public void StripText_SupportsRegexPatterns()
    {
        var block = Block("b1", "字幕本文 訂閱頻道12345");
        var options = Options(patterns: [@"訂閱頻道\d+"]);

        var document = RealtimeOcrTextPolicy.StripText(Document(block), options);

        Assert.Equal("字幕本文", document.Pages[0].Blocks[0].Text);
    }

    [Fact]
    public void EmptyOptions_AreNoOps()
    {
        var block = Block("b1", "字幕", new OcrBoundingBox(0, 0, 10, 10));
        var options = Options();

        Assert.True(options.IsEmpty);
        var (document, dropped) = RealtimeOcrTextPolicy.DropBlocks(Document(block), options);
        Assert.Single(document.Pages[0].Blocks);
        Assert.Empty(dropped);
        Assert.Equal("字幕", RealtimeOcrTextPolicy.StripText(document, options).Pages[0].Blocks[0].Text);
    }

    [Fact]
    public void DropBlocks_InfersPageSizeFromBoxesWhenPageHasNoSize()
    {
        var subtitle = Block("b1", "字幕", new OcrBoundingBox(0, 800, 500, 100));
        var watermark = Block("b2", "watermark", new OcrBoundingBox(900, 0, 100, 50));
        var document = new OcrDocumentResult
        {
            Pages = [new OcrPageResult { PageIndex = 0, Blocks = [subtitle, watermark] }]
        };
        var options = Options(regions: [new OcrNormalizedRegion(0.8, 0, 0.2, 0.2)]);

        var (filtered, dropped) = RealtimeOcrTextPolicy.DropBlocks(document, options);

        Assert.Single(filtered.Pages[0].Blocks);
        Assert.Equal("b2", Assert.Single(dropped).Id);
    }
}
