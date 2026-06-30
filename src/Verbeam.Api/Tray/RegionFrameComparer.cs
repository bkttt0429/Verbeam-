namespace Verbeam.Api.Tray;

/// <summary>
/// Downsampled grayscale frame comparison for the native region loop, mirroring
/// the workbench JS sampleRegionFrame/framesDiffer pair: skip OCR when the
/// captured region has not visibly changed since the last translated frame.
/// </summary>
public static class RegionFrameComparer
{
    public const int SampleWidth = 64;
    private const int PixelDeltaThreshold = 24;
    private const double ChangedPixelRatioThreshold = 0.02;

    public static byte[] Sample(Bitmap bitmap)
    {
        var sampleWidth = Math.Max(1, Math.Min(SampleWidth, bitmap.Width));
        var sampleHeight = Math.Max(1, (int)Math.Round(bitmap.Height * (sampleWidth / (double)bitmap.Width)));
        using var scaled = new Bitmap(sampleWidth, sampleHeight);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            graphics.DrawImage(bitmap, 0, 0, sampleWidth, sampleHeight);
        }

        var sample = new byte[sampleWidth * sampleHeight];
        var index = 0;
        for (var y = 0; y < sampleHeight; y++)
        {
            for (var x = 0; x < sampleWidth; x++)
            {
                var pixel = scaled.GetPixel(x, y);
                sample[index++] = (byte)((pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000);
            }
        }

        return sample;
    }

    public static bool Differ(byte[]? previous, byte[]? current)
    {
        if (previous is null || current is null)
        {
            return true;
        }

        if (previous.Length != current.Length)
        {
            return true;
        }

        if (previous.Length == 0)
        {
            return false;
        }

        var changed = 0;
        for (var i = 0; i < previous.Length; i++)
        {
            if (Math.Abs(previous[i] - current[i]) > PixelDeltaThreshold)
            {
                changed++;
            }
        }

        return changed / (double)previous.Length > ChangedPixelRatioThreshold;
    }
}
