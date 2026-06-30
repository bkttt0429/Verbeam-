using System.Drawing;
using Verbeam.Api.Tray;
using Verbeam.Core.Models;

namespace Verbeam.Tests;

public sealed class SurfaceMathTests
{
    [Fact]
    public void MapsNormalizedRegionIntoSurfacePixels()
    {
        var surface = new Rectangle(100, 200, 1000, 500);

        var mapped = SurfaceMath.ToScreen(surface, new GameRegionRect(0.1, 0.2, 0.5, 0.4));

        Assert.Equal(new Rectangle(200, 300, 500, 200), mapped);
    }

    [Fact]
    public void OffsetsBySurfaceOriginSoRegionsFollowAMovedWindow()
    {
        var region = new GameRegionRect(0.25, 0.0, 0.5, 0.1);

        var atOrigin = SurfaceMath.ToScreen(new Rectangle(0, 0, 800, 600), region);
        var moved = SurfaceMath.ToScreen(new Rectangle(640, 480, 800, 600), region);

        Assert.Equal(atOrigin.Size, moved.Size);
        Assert.Equal(atOrigin.X + 640, moved.X);
        Assert.Equal(atOrigin.Y + 480, moved.Y);
    }

    [Fact]
    public void ClampsDegenerateSizeToAtLeastOnePixel()
    {
        var mapped = SurfaceMath.ToScreen(new Rectangle(0, 0, 1920, 1080), new GameRegionRect(0.5, 0.5, 0.0, 0.0));

        Assert.Equal(1, mapped.Width);
        Assert.Equal(1, mapped.Height);
    }

    [Fact]
    public void RoundTripsAScreenRectThroughNormalized()
    {
        var surface = new Rectangle(50, 60, 1280, 720);
        var screen = new Rectangle(200, 180, 400, 120);

        var normalized = SurfaceMath.ToNormalized(surface, screen);
        var back = SurfaceMath.ToScreen(surface, normalized);

        Assert.Equal(screen, back);
    }

    [Fact]
    public void ToNormalizedClampsARegionDraggedPastTheSurfaceEdge()
    {
        var surface = new Rectangle(0, 0, 1000, 1000);
        var screen = new Rectangle(800, 800, 400, 400);   // extends 200px past the right/bottom edge

        var normalized = SurfaceMath.ToNormalized(surface, screen);

        Assert.Equal(0.8, normalized.X, 3);
        Assert.Equal(0.8, normalized.Y, 3);
        Assert.Equal(0.2, normalized.Width, 3);
        Assert.Equal(0.2, normalized.Height, 3);
    }
}
