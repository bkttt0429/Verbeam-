using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Verbeam.Core.Providers;
using Xunit;

namespace Verbeam.Tests;

public class OnnxRecognizerTests
{
    private static string? FindOcrModelsDir()
    {
        var dir = System.AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Verbeam.Api", "ocr-models");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    [Fact]
    public void RecognizesJapaneseLineWithJapanRec()
    {
        var modelsDir = FindOcrModelsDir();
        var model = modelsDir is null ? null : Path.Combine(modelsDir, "japan_PP-OCRv4_rec_infer.onnx");
        var keys = modelsDir is null ? null : Path.Combine(modelsDir, "japan_dict.txt");
        // The validation image is a clean horizontal "もしかしたら" line confirmed to read
        // correctly via the Python rapidocr japan rec (same CTC convention this class ports).
        var image = @"D:\LocalTranslateHub\app\.verify\_recline.png";

        if (model is null || !File.Exists(model) || !File.Exists(keys!) || !File.Exists(image))
        {
            return; // resources not present in this environment — skip rather than fail CI
        }

        using var recognizer = new OnnxRecognizer(model, keys!);
        using var bitmap = SKBitmap.Decode(image);
        Assert.NotNull(bitmap);

        var text = recognizer.RecognizeLine(bitmap);

        Assert.Equal("もしかしたら", text);
    }

    private static string? FindAppRoot()
    {
        var dir = System.AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "Verbeam.Api")) &&
                Directory.Exists(Path.Combine(dir, ".codex-run")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    [Fact]
    public void V6KanaConstrainedDecodeFixesSparseReSlot()
    {
        var resources = FindV6SparseSlotResources("slot_4_re.png");
        if (resources is null)
        {
            return;
        }

        using var recognizer = new OnnxRecognizer(resources.Value.Model, resources.Value.Keys);
        using var bitmap = SKBitmap.Decode(resources.Value.Image);
        Assert.NotNull(bitmap);

        var raw = recognizer.RecognizeLine(bitmap);
        var kana = recognizer.RecognizeConstrained(
            bitmap,
            OnnxRecognizerDecodeMask.Kana,
            topK: 3);

        Assert.NotEqual("\u308c", raw);
        Assert.Equal("\u308c", kana.Text);
        Assert.NotEmpty(kana.Steps);
        Assert.All(kana.Steps, step => Assert.True(step.TopCandidates.Count <= 3));
    }

    [Fact]
    public void V6KanaConstrainedDecodeReadsTightSparseShiAndKuSlots()
    {
        var shi = FindV6SparseSlotResources("slot_1_shi_pred.png");
        var ku = FindV6SparseSlotResources("slot_2_ku_pred.png");
        if (shi is null || ku is null)
        {
            return;
        }

        using var recognizer = new OnnxRecognizer(shi.Value.Model, shi.Value.Keys);
        using var shiBitmap = SKBitmap.Decode(shi.Value.Image);
        using var kuBitmap = SKBitmap.Decode(ku.Value.Image);
        Assert.NotNull(shiBitmap);
        Assert.NotNull(kuBitmap);

        using var tightShi = CenterCrop(shiBitmap, 0.45);
        using var tightKu = Threshold(CenterCrop(kuBitmap, 0.45), 170);

        var shiText = recognizer.RecognizeConstrained(
            tightShi,
            OnnxRecognizerDecodeMask.Kana,
            topK: 3).Text;
        var kuText = recognizer.RecognizeConstrained(
            tightKu,
            OnnxRecognizerDecodeMask.Kana,
            topK: 3).Text;

        Assert.Equal("\u3057", shiText);
        Assert.Equal("\u304f", kuText);
    }

    [Fact]
    public void V6SparseGlyphSlotSelectorReconstructsKnownColumnSlots()
    {
        var resources = FindV6SparseSlotResources("slot_0_mi.png");
        if (resources is null)
        {
            return;
        }

        using var recognizer = new OnnxRecognizer(resources.Value.Model, resources.Value.Keys);
        var files = new[]
        {
            "slot_0_mi.png",
            "slot_1_shi_pred.png",
            "slot_2_ku_pred.png",
            "slot_3_a.png",
            "slot_4_re.png"
        };

        var builder = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            var slot = FindV6SparseSlotResources(file);
            Assert.NotNull(slot);
            using var bitmap = SKBitmap.Decode(slot.Value.Image);
            Assert.NotNull(bitmap);

            var selected = RapidOcrNetProvider.RecognizeSparseGlyphSlotWithOnnx(bitmap, recognizer);
            Assert.NotNull(selected);
            builder.Append(selected.Value.Text);
        }

        Assert.Equal("\u7f8e\u3057\u304f\u3042\u308c", builder.ToString());
    }

    [Fact]
    public void SparseGlyphCandidateFromLines_InterpolatesMissingVerticalSlots()
    {
        // DET framed only the strong end glyphs of a 5-char vertical column (美 … れ); the thin
        // middle しく were dropped. The from-lines fallback must rebuild all 5 slot centers so the
        // constrained per-slot decoder gets ROIs for the missing kana.
        using var bitmap = new SKBitmap(200, 400);
        var lines = new List<RapidOcrRealtimeLine>
        {
            new() { CropBox = new SKRectI(53, 52, 115, 105), DisplayBox = null, Text = "美" },   // 美 slot 0
            new() { CropBox = new SKRectI(66, 285, 128, 338), DisplayBox = null, Text = "れ" },  // れ slot 4
        };

        var ok = RapidOcrNetProvider.TryBuildSparseGlyphCandidateFromLines(bitmap, lines, out var candidate);

        Assert.True(ok);
        Assert.Equal(RapidOcrNetProvider.SparseGlyphOrientation.Vertical, candidate.Orientation);
        Assert.Equal(5, candidate.Centers.Count);                 // 2 anchors + 3 interpolated しく/あ slots
        Assert.Equal(78, candidate.Centers[0].Y);                 // 美 center
        Assert.Equal(311, candidate.Centers[candidate.Centers.Count - 1].Y); // れ center
        // strictly increasing Y, evenly spaced
        for (var i = 1; i < candidate.Centers.Count; i++)
        {
            Assert.True(candidate.Centers[i].Y > candidate.Centers[i - 1].Y);
        }
    }

    [Fact]
    public void SparseGlyphCandidateFromLines_IgnoresContiguousColumnWithNoGaps()
    {
        // No missing slots between adjacent glyphs -> the normal path already had every slot, so the
        // fallback must decline (centers.Count would not exceed the found glyph count).
        using var bitmap = new SKBitmap(200, 400);
        var lines = new List<RapidOcrRealtimeLine>
        {
            new() { CropBox = new SKRectI(53, 52, 115, 105), DisplayBox = null, Text = "美" },
            new() { CropBox = new SKRectI(55, 112, 117, 165), DisplayBox = null, Text = "し" },
            new() { CropBox = new SKRectI(57, 172, 119, 225), DisplayBox = null, Text = "く" },
        };

        var ok = RapidOcrNetProvider.TryBuildSparseGlyphCandidateFromLines(bitmap, lines, out _);

        Assert.False(ok);
    }

    private static (string Model, string Keys, string Image)? FindV6SparseSlotResources(string imageName)
    {
        var appRoot = FindAppRoot();
        if (appRoot is null)
        {
            return null;
        }

        var model = Path.Combine(appRoot, "src", "Verbeam.Api", "ocr-models", "ppocrv6", "PP-OCRv6_small_rec_inference.onnx");
        var keys = Path.Combine(appRoot, "src", "Verbeam.Api", "ocr-models", "ppocrv6", "ppocrv6_small_dict.txt");
        var image = Path.Combine(appRoot, ".codex-run", "column-slot-prototype", imageName);
        return File.Exists(model) && File.Exists(keys) && File.Exists(image)
            ? (model, keys, image)
            : null;
    }

    private static SKBitmap CenterCrop(SKBitmap source, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var left = Math.Max(0, (source.Width - width) / 2);
        var top = Math.Max(0, (source.Height - height) / 2);
        var output = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(output);
        canvas.DrawBitmap(
            source,
            new SKRectI(left, top, left + width, top + height),
            new SKRect(0, 0, width, height));
        canvas.Flush();
        return output;
    }

    private static SKBitmap Threshold(SKBitmap source, byte threshold)
    {
        using (source)
        {
            var output = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var pixel = source.GetPixel(x, y);
                    var luminance = (int)Math.Round((pixel.Red * 0.299) + (pixel.Green * 0.587) + (pixel.Blue * 0.114));
                    var value = luminance > threshold ? (byte)255 : (byte)0;
                    output.SetPixel(x, y, new SKColor(value, value, value, 255));
                }
            }

            return output;
        }
    }
}
