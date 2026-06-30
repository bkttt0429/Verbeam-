using RapidOcrNet;
using SkiaSharp;
using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

/// <summary>
/// Per-session layout cache backing the rapidocr-net realtime incremental path:
/// a full detect builds the line layout, later frames hash-diff each line region
/// and re-run recognition only on the lines whose pixels changed.
/// </summary>
internal sealed class RapidOcrRealtimeCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RapidOcrRealtimeLayout> _sessions = new(StringComparer.Ordinal);

    public RapidOcrRealtimeLayout? TryGet(string key, DateTimeOffset now, int idleTimeoutMs)
    {
        lock (_gate)
        {
            EvictIdle(now, idleTimeoutMs);
            return _sessions.TryGetValue(key, out var layout) ? layout : null;
        }
    }

    public void Store(string key, RapidOcrRealtimeLayout layout, DateTimeOffset now, int maxSessions, int idleTimeoutMs)
    {
        lock (_gate)
        {
            EvictIdle(now, idleTimeoutMs);
            _sessions[key] = layout;
            var capacity = Math.Max(1, maxSessions);
            while (_sessions.Count > capacity)
            {
                var oldest = _sessions.MinBy(entry => entry.Value.LastSeenAt);
                _sessions.Remove(oldest.Key);
            }
        }
    }

    private void EvictIdle(DateTimeOffset now, int idleTimeoutMs)
    {
        if (idleTimeoutMs <= 0)
        {
            return;
        }

        var expired = _sessions
            .Where(entry => (now - entry.Value.LastSeenAt).TotalMilliseconds > idleTimeoutMs)
            .Select(entry => entry.Key)
            .ToArray();
        foreach (var key in expired)
        {
            _sessions.Remove(key);
        }
    }
}

internal sealed class RealtimeRoiBand
{
    public required SKRectI Rect { get; init; }
    public int MissStreak { get; set; }
    public DateTimeOffset LastConfirmedAt { get; set; }
}

internal sealed class RapidOcrRealtimeLayout
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required List<RapidOcrRealtimeLine> Lines { get; init; }
    public required bool[] OutsideCellMask { get; init; }
    public required byte[] OutsideCellSamples { get; init; }
    public required DateTimeOffset LastFullDetectAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? FirstTransientFailureAt { get; set; }

    public long LastFullDetectMs { get; set; }

    /// <summary>
    /// Sticky subtitle region(s) in full-frame pixel space from the last successful detect. While
    /// these are set, blank/changed frames re-detect ONLY here instead of re-scanning the whole
    /// screen, so a whole-screen capture stays realtime. Carried forward across empty frames.
    /// </summary>
    public List<RealtimeRoiBand>? RoiBands { get; set; }

    /// <summary>When the last whole-screen (non-scoped) scan ran; gates how often the costly
    /// whole-frame re-scan fires to catch a subtitle that moved outside the locked ROI.</summary>
    public DateTimeOffset LastWholeScreenScanAt { get; set; }

    /// <summary>Consecutive incremental frames whose rec read empty / flipped script (a subtitle
    /// mid-change). Debounces the full-detect escalation so a changing subtitle is not turned into a
    /// per-frame detect storm. Reset to 0 on any clean frame.</summary>
    public int RepairSignalStreak { get; set; }

    /// <summary>Consecutive full detects that produced a structurally identical line layout (same line
    /// count + box positions; text changes don't count). Backs off the periodic interval re-detect when
    /// a subtitle's layout is stable, and resets the moment the layout changes.</summary>
    public int StableLayoutStreak { get; set; }
}

internal sealed class RapidOcrRealtimeLine
{
    /// <summary>Padded, neighbor-clipped region used for hashing and rec-only crops.</summary>
    public required SKRectI CropBox { get; init; }

    /// <summary>Tight detect-time bounding box reported back in OCR blocks.</summary>
    public required OcrBoundingBox? DisplayBox { get; init; }

    public ulong Hash { get; set; }
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
}

/// <summary>Pixel sampling and box math for the realtime incremental path.</summary>
internal static class RapidOcrRealtimePixels
{
    private const int OutsideGridSize = 16;
    private const int OutsideLuminanceTolerance = 14;

    /// <summary>
    /// Minimum changed outside-grid cells before a re-detect triggers; a single
    /// cell flapping (blinking caret / continue indicator) must not force full
    /// detects every frame.
    /// </summary>
    public const int MinOutsideChangedCells = 2;

    /// <summary>
    /// Converts detect-time text blocks into recognition lines: axis-aligned
    /// boxes ordered top-to-bottom, padded for the recognizer, and clipped at
    /// the vertical midline between neighbors so adjacent lines never overlap.
    /// </summary>
    public static List<RapidOcrRealtimeLine> BuildLines(
        OcrResult detect,
        SKBitmap bitmap,
        int quantizeStep,
        double coordScale = 1.0,
        double displayScale = 1.0)
    {
        // coordScale maps detection-space coordinates back to the supplied bitmap's space.
        // When L1 scale-lock runs detection on an up-scaled copy (sharp enough for det, stable
        // DirectML shape) but recognizes from the original bitmap, det boxes arrive in scaled
        // space and must be divided back down (coordScale = original / scaled) so crops are taken
        // from the original, sharper pixels.
        var raw = new List<(int MinX, int MinY, int MaxX, int MaxY, double CenterY, string Text, double Confidence)>();
        foreach (var block in detect.TextBlocks)
        {
            if (block.BoxPoints is not { Length: > 0 } points || string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            var minX = (int)Math.Round(points.Min(point => point.X) * coordScale);
            var minY = (int)Math.Round(points.Min(point => point.Y) * coordScale);
            var maxX = (int)Math.Round(points.Max(point => point.X) * coordScale);
            var maxY = (int)Math.Round(points.Max(point => point.Y) * coordScale);
            if (maxX <= minX || maxY <= minY)
            {
                continue;
            }

            var confidence = block.CharScores is { Length: > 0 }
                ? Math.Clamp(block.CharScores.Average(score => (double)score), 0, 1)
                : Math.Clamp(block.BoxScore, 0, 1);
            raw.Add((minX, minY, maxX, maxY, (minY + maxY) / 2.0, block.Text, confidence));
        }

        raw = raw
            .OrderBy(entry => entry.CenterY)
            .ThenBy(entry => entry.MinX)
            .ToList();

        var lines = new List<RapidOcrRealtimeLine>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var entry = raw[i];
            var width = entry.MaxX - entry.MinX + 1;
            var height = entry.MaxY - entry.MinY + 1;
            // Horizontal padding is char-sized (~half a glyph), NOT line-width-scaled: a short line
            // previously got only ~6px, too little CRNN lead-in and not enough to recover an edge
            // glyph DBNet clipped, so the first/last character was frequently dropped. height ≈ glyph
            // width for CJK, so height/2 ≈ half a character of margin on each side.
            var padX = Math.Max(12, height / 2);
            var padY = Math.Max(2, height / 12);
            var left = Math.Max(0, entry.MinX - padX);
            var top = Math.Max(0, entry.MinY - padY);
            var right = Math.Min(bitmap.Width, entry.MaxX + padX + 1);
            var bottom = Math.Min(bitmap.Height, entry.MaxY + padY + 1);
            var isHorizontalSubtitleLine = width >= height * 2;
            if (isHorizontalSubtitleLine)
            {
                // Keep DET's vertical band, but span the whole selected row. This lets same-band
                // subtitle replacements use the fast rec-only path without clipping longer text.
                left = 0;
                right = bitmap.Width;
            }

            if (i > 0)
            {
                var previousBoundary = (int)Math.Floor((raw[i - 1].CenterY + entry.CenterY) / 2.0);
                top = Math.Max(top, previousBoundary + 1);
            }

            if (i + 1 < raw.Count)
            {
                var nextBoundary = (int)Math.Floor((entry.CenterY + raw[i + 1].CenterY) / 2.0);
                bottom = Math.Min(bottom, nextBoundary);
            }

            if (right <= left || bottom <= top)
            {
                continue;
            }

            var cropBox = new SKRectI(left, top, right, bottom);
            lines.Add(new RapidOcrRealtimeLine
            {
                CropBox = cropBox,
                DisplayBox = new OcrBoundingBox(
                    Math.Max(0, (int)Math.Round(entry.MinX * displayScale)),
                    Math.Max(0, (int)Math.Round(entry.MinY * displayScale)),
                    Math.Max(1, (int)Math.Round((entry.MaxX - entry.MinX) * displayScale)),
                    Math.Max(1, (int)Math.Round((entry.MaxY - entry.MinY) * displayScale))),
                Hash = LineSignature(bitmap, cropBox, quantizeStep),
                Text = entry.Text,
                Confidence = entry.Confidence
            });
        }

        return lines;
    }

    /// <summary>Maps the configured noise tolerance to a luminance quantization step.</summary>
    public static int QuantizeStep(int tolerance)
        => Math.Max(1, tolerance * 8);

    /// <summary>
    /// Dense sampled signature over the box: columns every ~10px so even a
    /// single changed character is sampled (a coarse 8x8 average hash misses
    /// one-digit updates in wide dialogue lines). Luminance is quantized so
    /// minor capture noise does not flip the signature; compare for equality -
    /// a false positive only costs one extra rec-only pass, a false negative
    /// would show stale text.
    /// </summary>
    public static ulong LineSignature(SKBitmap bitmap, SKRectI box, int quantizeStep)
    {
        var cols = Math.Clamp(box.Width / 10, 8, 64);
        var rows = Math.Clamp(box.Height / 10, 3, 8);
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        var hash = FnvOffset;
        for (var gy = 0; gy < rows; gy++)
        {
            var y = rows == 1
                ? box.Top + box.Height / 2
                : box.Top + Math.Min(box.Height - 1, gy * (box.Height - 1) / (rows - 1));
            for (var gx = 0; gx < cols; gx++)
            {
                var x = cols == 1
                    ? box.Left + box.Width / 2
                    : box.Left + Math.Min(box.Width - 1, gx * (box.Width - 1) / (cols - 1));
                var color = bitmap.GetPixel(x, y);
                var luminance = (color.Red + color.Green + color.Blue) / 3;
                hash = (hash ^ (uint)(luminance / quantizeStep)) * FnvPrime;
            }
        }

        return hash;
    }

    /// <summary>
    /// Samples one luminance byte per 16x16 grid cell that no line box touches,
    /// so new text appearing outside the cached layout can trigger a re-detect.
    /// </summary>
    public static (bool[] Mask, byte[] Samples) BuildOutsideCells(SKBitmap bitmap, IReadOnlyList<RapidOcrRealtimeLine> lines)
    {
        var mask = new bool[OutsideGridSize * OutsideGridSize];
        var samples = new byte[OutsideGridSize * OutsideGridSize];
        for (var gy = 0; gy < OutsideGridSize; gy++)
        {
            for (var gx = 0; gx < OutsideGridSize; gx++)
            {
                var cell = CellRect(bitmap, gx, gy);
                var i = gy * OutsideGridSize + gx;
                var covered = false;
                foreach (var line in lines)
                {
                    if (line.CropBox.IntersectsWith(cell))
                    {
                        covered = true;
                        break;
                    }
                }

                mask[i] = covered;
                if (!covered)
                {
                    samples[i] = CellLuminance(bitmap, cell);
                }
            }
        }

        return (mask, samples);
    }

    public static int CountOutsideChangedCells(SKBitmap bitmap, RapidOcrRealtimeLayout layout)
    {
        var changed = 0;
        for (var gy = 0; gy < OutsideGridSize; gy++)
        {
            for (var gx = 0; gx < OutsideGridSize; gx++)
            {
                var i = gy * OutsideGridSize + gx;
                if (layout.OutsideCellMask[i])
                {
                    continue;
                }

                var luminance = CellLuminance(bitmap, CellRect(bitmap, gx, gy));
                if (Math.Abs(luminance - layout.OutsideCellSamples[i]) > OutsideLuminanceTolerance)
                {
                    changed++;
                    if (changed >= MinOutsideChangedCells)
                    {
                        return changed;
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Zero-copy view over the source pixels; falls back to a real copy when
    /// ExtractSubset rejects the rectangle.
    /// </summary>
    public static SKBitmap CropSubset(SKBitmap source, SKRectI box)
    {
        var subset = new SKBitmap();
        if (source.ExtractSubset(subset, box))
        {
            return subset;
        }

        subset.Dispose();
        var crop = new SKBitmap(box.Width, box.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(crop);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, box, new SKRect(0, 0, box.Width, box.Height));
        return crop;
    }

    public static SKBitmap CropRecognitionSubset(SKBitmap source, SKRectI box)
        => CropSubset(source, TrimHorizontalInkBox(source, box));

    private static SKRectI TrimHorizontalInkBox(SKBitmap source, SKRectI box)
    {
        box = new SKRectI(
            Math.Clamp(box.Left, 0, source.Width),
            Math.Clamp(box.Top, 0, source.Height),
            Math.Clamp(box.Right, 0, source.Width),
            Math.Clamp(box.Bottom, 0, source.Height));
        if (box.Width <= 0 || box.Height <= 0 || box.Width < box.Height * 3)
        {
            return box;
        }

        var min = 255;
        var max = 0;
        long sum = 0;
        var count = 0;
        for (var y = box.Top; y < box.Bottom; y += 2)
        {
            for (var x = box.Left; x < box.Right; x += 2)
            {
                var color = source.GetPixel(x, y);
                var luma = (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
                min = Math.Min(min, luma);
                max = Math.Max(max, luma);
                sum += luma;
                count++;
            }
        }

        if (count == 0 || max - min < 18)
        {
            return box;
        }

        var mean = (double)sum / count;
        var brightThreshold = mean + (max - mean) * 0.48;
        var darkThreshold = mean - (mean - min) * 0.48;
        var useBrightInk = max - mean >= mean - min;
        var minInkRows = Math.Max(1, box.Height / 18);
        var left = -1;
        var right = -1;

        for (var x = box.Left; x < box.Right; x++)
        {
            var ink = 0;
            for (var y = box.Top; y < box.Bottom; y++)
            {
                var color = source.GetPixel(x, y);
                var luma = (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
                if ((useBrightInk && luma >= brightThreshold) ||
                    (!useBrightInk && luma <= darkThreshold))
                {
                    ink++;
                }
            }

            if (ink >= minInkRows)
            {
                if (left < 0)
                {
                    left = x;
                }

                right = x;
            }
        }

        if (left < 0 || right < left)
        {
            return box;
        }

        var pad = Math.Clamp(box.Height, 24, 72);
        left = Math.Max(box.Left, left - pad);
        right = Math.Min(box.Right, right + pad + 1);
        if (right - left < Math.Min(box.Width, box.Height * 2))
        {
            var center = (left + right) / 2;
            var half = Math.Min(box.Width, box.Height * 2) / 2;
            left = Math.Max(box.Left, center - half);
            right = Math.Min(box.Right, center + half);
        }

        return new SKRectI(left, box.Top, right, box.Bottom);
    }

    public static string TextFrom(TextLine line)
        => line.Chars is { Length: > 0 }
            ? string.Concat(line.Chars)
            : string.Empty;

    public static double ConfidenceFrom(TextLine line, double fallback)
        => line.CharScores is { Length: > 0 }
            ? Math.Clamp(line.CharScores.Average(score => (double)score), 0, 1)
            : fallback;

    private static SKRectI CellRect(SKBitmap bitmap, int gx, int gy)
    {
        var left = gx * bitmap.Width / OutsideGridSize;
        var top = gy * bitmap.Height / OutsideGridSize;
        var right = Math.Max(left + 1, (gx + 1) * bitmap.Width / OutsideGridSize);
        var bottom = Math.Max(top + 1, (gy + 1) * bitmap.Height / OutsideGridSize);
        return new SKRectI(left, top, right, bottom);
    }

    private static byte CellLuminance(SKBitmap bitmap, SKRectI cell)
    {
        var x = Math.Min(bitmap.Width - 1, cell.Left + cell.Width / 2);
        var y = Math.Min(bitmap.Height - 1, cell.Top + cell.Height / 2);
        var color = bitmap.GetPixel(x, y);
        return (byte)((color.Red + color.Green + color.Blue) / 3);
    }
}
