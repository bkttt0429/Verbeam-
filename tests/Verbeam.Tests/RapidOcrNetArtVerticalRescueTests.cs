using SkiaSharp;
using Verbeam.Core.Models;
using Verbeam.Core.Providers;

namespace Verbeam.Tests;

public sealed class RapidOcrNetArtVerticalRescueTests
{
    [Fact]
    public void SparseGlyphCandidate_DetectsHorizontalSparseKanaLikeMarks()
    {
        using var bitmap = new SKBitmap(260, 120, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = new SKColor(80, 80, 80), IsAntialias = false, StrokeWidth = 3 };
        canvas.Clear(SKColors.White);

        foreach (var x in new[] { 60, 130, 200 })
        {
            canvas.DrawLine(x, 48, x + 16, 48, paint);
            canvas.DrawLine(x + 12, 48, x + 4, 70, paint);
        }

        var found = RapidOcrNetProvider.TryBuildSparseGlyphCandidate(bitmap, out var candidate);

        Assert.True(found);
        Assert.Equal(RapidOcrNetProvider.SparseGlyphOrientation.Horizontal, candidate.Orientation);
        Assert.Equal(3, candidate.Centers.Count);
        Assert.True(candidate.Bounds.Width > candidate.Bounds.Height);
    }

    [Fact]
    public void SparseGlyphCandidate_DetectsVerticalSparseKanaLikeMarks()
    {
        using var bitmap = new SKBitmap(140, 260, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = new SKColor(80, 80, 80), IsAntialias = false, StrokeWidth = 3 };
        canvas.Clear(SKColors.White);

        foreach (var y in new[] { 50, 120, 190 })
        {
            canvas.DrawLine(68, y, 84, y, paint);
            canvas.DrawLine(80, y, 72, y + 22, paint);
        }

        var found = RapidOcrNetProvider.TryBuildSparseGlyphCandidate(bitmap, out var candidate);

        Assert.True(found);
        Assert.Equal(RapidOcrNetProvider.SparseGlyphOrientation.Vertical, candidate.Orientation);
        Assert.Equal(3, candidate.Centers.Count);
        Assert.True(candidate.Bounds.Height > candidate.Bounds.Width);
    }

    [Fact]
    public void SparseGlyphRescue_AllowsVerticalCandidates()
    {
        Assert.True(RapidOcrNetProvider.ShouldUseSparseGlyphRescueOrientation(
            RapidOcrNetProvider.SparseGlyphOrientation.Horizontal));
        Assert.True(RapidOcrNetProvider.ShouldUseSparseGlyphRescueOrientation(
            RapidOcrNetProvider.SparseGlyphOrientation.Vertical));
        Assert.False(RapidOcrNetProvider.ShouldUseSparseGlyphRescueOrientation(
            RapidOcrNetProvider.SparseGlyphOrientation.Unknown));
    }

    [Fact]
    public void SparseGlyphNormalizer_CollapsesSpacedJapaneseGlyphs()
    {
        var normalized = RapidOcrNetProvider.NormalizeJapaneseSparseGlyphTextForTests(
            "\u529b \u30ef \u30ad",
            "ja");

        Assert.Equal("\u30ab\u30ef\u30ad", normalized);
    }

    [Fact]
    public void SparseGlyphRescue_AllowsSameLengthConfusableSparseKana()
    {
        var candidate = new RapidOcrNetProvider.SparseGlyphCandidate(
            RapidOcrNetProvider.SparseGlyphOrientation.Horizontal,
            new[]
            {
                new SKPointI(75, 80),
                new SKPointI(155, 80),
                new SKPointI(235, 80)
            },
            54,
            new SKRectI(48, 53, 262, 107));

        Assert.True(RapidOcrNetProvider.ShouldAttemptSparseGlyphRescueForPrimary(
            "\u529b \u30ef \u30ad",
            primaryBlockCount: 3,
            candidate));
        Assert.True(RapidOcrNetProvider.ShouldAcceptSparseGlyphRescue(
            "\u529b \u30ef \u30ad",
            "\u30ab\u30ef\u30ad",
            candidate));
    }

    [Fact]
    public void SparseGlyphRescue_RejectsSameLengthContiguousKanaWithoutSparseSignal()
    {
        var candidate = new RapidOcrNetProvider.SparseGlyphCandidate(
            RapidOcrNetProvider.SparseGlyphOrientation.Horizontal,
            new[]
            {
                new SKPointI(75, 80),
                new SKPointI(155, 80),
                new SKPointI(235, 80)
            },
            54,
            new SKRectI(48, 53, 262, 107));

        Assert.False(RapidOcrNetProvider.ShouldAttemptSparseGlyphRescueForPrimary(
            "\u30ab\u30ef\u30ad",
            primaryBlockCount: 1,
            candidate));
    }

    [Fact]
    public void SparseGlyphRescue_RejectsWeakInterpolatedVerticalGain()
    {
        var candidate = new RapidOcrNetProvider.SparseGlyphCandidate(
            RapidOcrNetProvider.SparseGlyphOrientation.Vertical,
            new[]
            {
                new SKPointI(80, 80),
                new SKPointI(80, 138),
                new SKPointI(80, 196),
                new SKPointI(80, 254),
                new SKPointI(80, 312)
            },
            43,
            new SKRectI(58, 58, 102, 334));

        Assert.False(RapidOcrNetProvider.ShouldAcceptSparseGlyphRescue(
            "\u7f8e\n\u308c",
            "\u7f8e\u4e38\u3042\u3001\u308c",
            candidate));

        Assert.True(RapidOcrNetProvider.ShouldAcceptSparseGlyphRescue(
            "\u7f8e\n\u308c",
            "\u7f8e\u3057\u304f\u3042\u308c",
            candidate));
    }

    [Fact]
    public void SparseGlyphCandidateFromLines_UsesSmallGapPitchWhenMiddleAnchorExists()
    {
        using var bitmap = new SKBitmap(900, 620);
        var lines = new List<RapidOcrRealtimeLine>
        {
            new() { CropBox = new SKRectI(649, 158, 704, 212), DisplayBox = null, Text = "\u7f8e" },
            new() { CropBox = new SKRectI(657, 405, 699, 449), DisplayBox = null, Text = "\u3042" },
            new() { CropBox = new SKRectI(656, 484, 700, 530), DisplayBox = null, Text = "\u308c" },
        };

        var ok = RapidOcrNetProvider.TryBuildSparseGlyphCandidateFromLines(bitmap, lines, out var candidate);

        Assert.True(ok);
        Assert.Equal(RapidOcrNetProvider.SparseGlyphOrientation.Vertical, candidate.Orientation);
        Assert.Equal(5, candidate.Centers.Count);
        Assert.Equal(185, candidate.Centers[0].Y);
        Assert.Equal(427, candidate.Centers[3].Y);
        Assert.Equal(507, candidate.Centers[4].Y);
    }

    [Fact]
    public void SparseGlyphCandidateFromLines_UsesFiveSlotPitchForWideTwoAnchorColumn()
    {
        using var bitmap = new SKBitmap(900, 620);
        var lines = new List<RapidOcrRealtimeLine>
        {
            new() { CropBox = new SKRectI(649, 158, 704, 212), DisplayBox = null, Text = "\u7f8e" },
            new() { CropBox = new SKRectI(656, 484, 700, 530), DisplayBox = null, Text = "\u308c" },
        };

        var ok = RapidOcrNetProvider.TryBuildSparseGlyphCandidateFromLines(bitmap, lines, out var candidate);

        Assert.True(ok);
        Assert.Equal(RapidOcrNetProvider.SparseGlyphOrientation.Vertical, candidate.Orientation);
        Assert.Equal(5, candidate.Centers.Count);
        Assert.Equal(185, candidate.Centers[0].Y);
        Assert.Equal(507, candidate.Centers[4].Y);
    }

    [Fact]
    public void SparseGlyphCandidateFromLines_AlternativeColumnIgnoresOutlierGlyphNoise()
    {
        using var bitmap = new SKBitmap(900, 620);
        var lines = new List<RapidOcrRealtimeLine>
        {
            new() { CropBox = new SKRectI(649, 158, 704, 212), DisplayBox = null, Text = "\u7f8e" },
            new() { CropBox = new SKRectI(657, 405, 699, 449), DisplayBox = null, Text = "\u3042" },
            new() { CropBox = new SKRectI(656, 484, 700, 530), DisplayBox = null, Text = "\u308c" },
            new() { CropBox = new SKRectI(820, 510, 860, 560), DisplayBox = null, Text = "\u5de5" },
        };

        var candidates = RapidOcrNetProvider.BuildAlternativeSparseGlyphCandidatesFromLines(bitmap, lines);

        var candidate = Assert.Single(candidates);
        Assert.Equal(RapidOcrNetProvider.SparseGlyphOrientation.Vertical, candidate.Orientation);
        Assert.Equal(5, candidate.Centers.Count);
        Assert.All(candidate.Centers, center => Assert.InRange(center.X, 640, 710));
    }

    [Fact]
    public void ShouldTryRescue_AllowsNonJapaneseRequestWhenKanaWasDetected()
    {
        var result = new OcrProviderResult(
            "\u7f8e\n\u308c",
            new[]
            {
                new OcrTextBlock("\u7f8e", 1.0, new OcrBoundingBox(428, 75, 39, 42)),
                new OcrTextBlock("\u308c", 0.934, new OcrBoundingBox(428, 308, 38, 43))
            },
            "rapidocr-net:ppocrv5-onnx");

        Assert.True(RapidOcrNetProvider.ShouldTryJapaneseArtVerticalRescue(result, "zh-TW"));
    }

    [Fact]
    public void ShouldTryRescue_DoesNotTreatPlainCjkAsJapanese()
    {
        var result = new OcrProviderResult(
            "\u958b\u59cb\n\u7e7c\u7e8c",
            new[]
            {
                new OcrTextBlock("\u958b\u59cb", 1.0, new OcrBoundingBox(100, 75, 50, 42)),
                new OcrTextBlock("\u7e7c\u7e8c", 0.934, new OcrBoundingBox(100, 308, 50, 43))
            },
            "rapidocr-net:ppocrv5-onnx");

        Assert.False(RapidOcrNetProvider.ShouldTryJapaneseArtVerticalRescue(result, "zh-TW"));
    }

    [Fact]
    public void CandidateBuilder_AnchorsSparseJapaneseColumn()
    {
        var blocks = new[]
        {
            new OcrTextBlock("\u7f8e", 1.0, new OcrBoundingBox(428, 75, 39, 42)),
            new OcrTextBlock("\u308c", 0.934, new OcrBoundingBox(428, 308, 38, 43))
        };

        var found = RapidOcrNetProvider.TryBuildArtVerticalRescueCandidate(
            blocks,
            imageWidth: 881,
            imageHeight: 410,
            out var candidate);

        Assert.True(found);
        Assert.Equal(new SKRectI(390, 65, 510, 365), candidate.Crop);
        Assert.Equal(5, candidate.Scale);
        Assert.Equal(40, candidate.Border);
    }

    [Fact]
    public void CandidateBuilder_RejectsHorizontalText()
    {
        var blocks = new[]
        {
            new OcrTextBlock("\u7f8e", 1.0, new OcrBoundingBox(100, 80, 39, 42)),
            new OcrTextBlock("\u308c", 0.934, new OcrBoundingBox(180, 82, 38, 43))
        };

        var found = RapidOcrNetProvider.TryBuildArtVerticalRescueCandidate(
            blocks,
            imageWidth: 881,
            imageHeight: 410,
            out _);

        Assert.False(found);
    }
}
