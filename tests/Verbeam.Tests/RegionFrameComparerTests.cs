using System.Drawing;
using Verbeam.Api.Tray;

namespace Verbeam.Tests;

public sealed class RegionFrameComparerTests
{
    [Fact]
    public void Differ_TreatsNullSamplesAsChanged()
    {
        Assert.True(RegionFrameComparer.Differ(null, new byte[] { 1, 2, 3 }));
        Assert.True(RegionFrameComparer.Differ(new byte[] { 1, 2, 3 }, null));
    }

    [Fact]
    public void Differ_TreatsDifferentLengthsAsChanged()
    {
        Assert.True(RegionFrameComparer.Differ(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Differ_IgnoresSmallNoise()
    {
        var previous = new byte[256];
        var current = new byte[256];
        for (var i = 0; i < current.Length; i++)
        {
            current[i] = 10; // within the per-pixel delta threshold
        }

        Assert.False(RegionFrameComparer.Differ(previous, current));
    }

    [Fact]
    public void Differ_DetectsLocalizedChange()
    {
        var previous = new byte[256];
        var current = new byte[256];
        for (var i = 0; i < 16; i++)
        {
            current[i] = 255; // >2% of pixels changed far past the delta threshold
        }

        Assert.True(RegionFrameComparer.Differ(previous, current));
    }

    [Fact]
    public void Differ_SameSamplesAreUnchanged()
    {
        var sample = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        Assert.False(RegionFrameComparer.Differ(sample, (byte[])sample.Clone()));
    }

    [Fact]
    public void Sample_DownsamplesToCappedWidth()
    {
        using var bitmap = new Bitmap(640, 200);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
        }

        var sample = RegionFrameComparer.Sample(bitmap);

        // 640x200 → 64x20 grayscale bytes
        Assert.Equal(64 * 20, sample.Length);
        Assert.All(sample, value => Assert.True(value < 8));
    }

    [Fact]
    public void Sample_ReflectsContentChange()
    {
        using var dark = new Bitmap(128, 64);
        using (var graphics = Graphics.FromImage(dark))
        {
            graphics.Clear(Color.Black);
        }

        using var withText = new Bitmap(128, 64);
        using (var graphics = Graphics.FromImage(withText))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.White, 8, 24, 96, 16);
        }

        var a = RegionFrameComparer.Sample(dark);
        var b = RegionFrameComparer.Sample(withText);

        Assert.True(RegionFrameComparer.Differ(a, b));
    }
}
