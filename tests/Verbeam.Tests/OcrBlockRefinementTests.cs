using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class OcrBlockRefinementTests
{
    private static OcrRefinementOptions Options() => new();

    private static OcrBlock Block(string id, string text, double confidence, OcrBoundingBox? box)
        => new()
        {
            Id = id,
            Type = OcrBlockTypes.Text,
            Text = text,
            Confidence = confidence,
            BoundingBox = box,
            ShouldTranslate = true,
            Engine = "rapidocr-ppocrv5"
        };

    private static OcrDocumentResult Document(int? width, int? height, params OcrBlock[] blocks)
        => new()
        {
            Pages = [new OcrPageResult { PageIndex = 0, Width = width, Height = height, Blocks = blocks }]
        };

    [Fact]
    public void FindSuspectBlocks_FlagsLowConfidence()
    {
        var document = Document(
            1000,
            500,
            Block("b1", "編譯(Compile)", 0.95, new OcrBoundingBox(10, 10, 200, 40)),
            Block("b2", "紅譯(Assemble)", 0.6, new OcrBoundingBox(10, 60, 200, 40)));

        var suspects = OcrBlockRefinement.FindSuspectBlocks(document, Options());

        Assert.Equal("b2", Assert.Single(suspects).Id);
    }

    [Fact]
    public void FindSuspectBlocks_FlagsAnchorWithoutCjkEvenAtHighConfidence()
    {
        var document = Document(
            1000,
            500,
            Block("b1", "(Compile)", 0.97, new OcrBoundingBox(10, 10, 200, 40)));

        var suspects = OcrBlockRefinement.FindSuspectBlocks(document, Options());

        Assert.Equal("b1", Assert.Single(suspects).Id);
    }

    [Fact]
    public void FindSuspectBlocks_IgnoresHealthyAndBoxlessBlocks()
    {
        var document = Document(
            1000,
            500,
            Block("b1", "編譯(Compile)", 0.97, new OcrBoundingBox(10, 10, 200, 40)),
            Block("b2", "(Assemble)", 0.3, null));

        var suspects = OcrBlockRefinement.FindSuspectBlocks(document, Options());

        Assert.Empty(suspects);
    }

    [Fact]
    public void FindSuspectBlocks_CapsAtMaxBlocksLowestConfidenceFirst()
    {
        var blocks = Enumerable.Range(0, 10)
            .Select(i => Block($"b{i}", $"text {i}", 0.1 + (i * 0.05), new OcrBoundingBox(0, i * 20, 100, 18)))
            .ToArray();
        var options = Options();
        options.MaxBlocks = 3;

        var suspects = OcrBlockRefinement.FindSuspectBlocks(Document(1000, 500, blocks), options);

        Assert.Equal(3, suspects.Count);
        Assert.Equal(new[] { "b0", "b1", "b2" }, suspects.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void ComputeCropRectangle_MapsAndPads()
    {
        var rect = OcrBlockRefinement.ComputeCropRectangle(
            new OcrBoundingBox(100, 100, 200, 50),
            pageWidth: 1000,
            pageHeight: 500,
            imageWidth: 1000,
            imageHeight: 500,
            paddingRatio: 0.1);

        Assert.NotNull(rect);
        Assert.True(rect!.X < 100 && rect.Y < 100);
        Assert.True(rect.X + rect.Width > 300 && rect.Y + rect.Height > 150);
    }

    [Fact]
    public void ComputeCropRectangle_ScalesFromPreprocessedSpaceToImagePixels()
    {
        // Boxes reported in a 2x-upscaled space; the crop must land on original pixels.
        var rect = OcrBlockRefinement.ComputeCropRectangle(
            new OcrBoundingBox(200, 200, 400, 100),
            pageWidth: 2000,
            pageHeight: 1000,
            imageWidth: 1000,
            imageHeight: 500,
            paddingRatio: 0);

        Assert.NotNull(rect);
        Assert.InRange(rect!.X, 90, 100);
        Assert.InRange(rect.Y, 90, 100);
        Assert.InRange(rect.Width, 200, 220);
        Assert.InRange(rect.Height, 50, 70);
    }

    [Fact]
    public void ComputeCropRectangle_ClampsToImageBounds()
    {
        var rect = OcrBlockRefinement.ComputeCropRectangle(
            new OcrBoundingBox(950, 480, 100, 60),
            pageWidth: 1000,
            pageHeight: 500,
            imageWidth: 1000,
            imageHeight: 500,
            paddingRatio: 0.5);

        Assert.NotNull(rect);
        Assert.True(rect!.X >= 0 && rect.Y >= 0);
        Assert.True(rect.X + rect.Width <= 1000);
        Assert.True(rect.Y + rect.Height <= 500);
    }

    [Fact]
    public void ShouldAcceptRefinement_AcceptsConfidenceGain()
    {
        Assert.True(OcrBlockRefinement.ShouldAcceptRefinement("紅譯(Assemble)", 0.6, "組譯(Assemble)", 0.96, Options()));
    }

    [Fact]
    public void ShouldAcceptRefinement_AcceptsAnchorMergeRecoveringCjk()
    {
        // Original pass captured only "(Assemble)"; the crop pass recovered the
        // CJK prefix while keeping the anchor — accept regardless of confidence.
        Assert.True(OcrBlockRefinement.ShouldAcceptRefinement("(Assemble)", 0.97, "組譯 (Assemble)", 0.5, Options()));
    }

    [Fact]
    public void ShouldAcceptRefinement_RejectsConfidenceRegression()
    {
        Assert.False(OcrBlockRefinement.ShouldAcceptRefinement("編譯(Compile)", 0.9, "稱譯 (Complle)", 0.7, Options()));
    }

    [Fact]
    public void ShouldAcceptRefinement_RejectsAnchorLoss()
    {
        Assert.False(OcrBlockRefinement.ShouldAcceptRefinement("(Assemble)", 0.9, "組譯", 0.92, Options()));
    }

    [Fact]
    public void ShouldAcceptRefinement_RejectsEmptyRefinedText()
    {
        Assert.False(OcrBlockRefinement.ShouldAcceptRefinement("(Assemble)", 0.2, "  ", 0.99, Options()));
    }

    [Fact]
    public void ApplyRefinement_UpdatesTextConfidenceAndEngine()
    {
        var document = Document(
            1000,
            500,
            Block("b1", "紅譯(Assemble)", 0.6, new OcrBoundingBox(10, 10, 200, 40)));

        var refined = OcrBlockRefinement.ApplyRefinement(document, "b1", "組譯(Assemble)", 0.96);

        var block = refined.Pages[0].Blocks[0];
        Assert.Equal("組譯(Assemble)", block.Text);
        Assert.Equal(0.96, block.Confidence);
        Assert.Equal("rapidocr-ppocrv5+refined", block.Engine);
    }

    [Fact]
    public void ApplyRefinement_ReachesNestedChildren()
    {
        var child = Block("c1", "(Compile)", 0.5, new OcrBoundingBox(10, 10, 100, 20));
        var parent = Block("p1", string.Empty, 1, new OcrBoundingBox(0, 0, 500, 100)) with { Children = [child] };
        var document = Document(1000, 500, parent);

        var refined = OcrBlockRefinement.ApplyRefinement(document, "c1", "編譯 (Compile)", 0.9);

        Assert.Equal("編譯 (Compile)", refined.Pages[0].Blocks[0].Children[0].Text);
    }

    [Fact]
    public void InferPageSize_PrefersExplicitSizeAndFallsBackToExtent()
    {
        var sized = Document(1200, 800, Block("b1", "x", 1, new OcrBoundingBox(0, 0, 100, 50)));
        Assert.Equal((1200d, 800d), OcrBlockRefinement.InferPageSize(sized.Pages[0]));

        var unsized = Document(null, null, Block("b1", "x", 1, new OcrBoundingBox(100, 200, 300, 100)));
        Assert.Equal((400d, 300d), OcrBlockRefinement.InferPageSize(unsized.Pages[0]));
    }
}
