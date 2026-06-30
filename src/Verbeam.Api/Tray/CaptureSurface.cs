using Verbeam.Core.Models;

namespace Verbeam.Api.Tray;

/// <summary>
/// Resolves the current screen-space base rectangle that a game profile's normalized (0..1)
/// regions map into. A window surface follows the bound game window's client area as it moves
/// or resizes; a monitor surface is a fixed screen. Returns false when the surface is currently
/// unavailable (window missing/minimized) so the caller can park the affected regions.
/// </summary>
internal interface ICaptureSurface
{
    bool TryGetBounds(out Rectangle screenBounds);
}

internal sealed class WindowCaptureSurface : ICaptureSurface
{
    private readonly WindowLocator _locator;

    public WindowCaptureSurface(WindowLocator locator) => _locator = locator;

    public bool TryGetBounds(out Rectangle screenBounds) => _locator.TryGetClientRectOnScreen(out screenBounds);
}

internal sealed class MonitorCaptureSurface : ICaptureSurface
{
    private readonly Func<Rectangle> _bounds;

    public MonitorCaptureSurface(Func<Rectangle> bounds) => _bounds = bounds;

    public bool TryGetBounds(out Rectangle screenBounds)
    {
        screenBounds = _bounds();
        return screenBounds.Width > 0 && screenBounds.Height > 0;
    }
}

/// <summary>
/// Pure normalized(0..1)→screen-pixel mapping, kept free of WinForms/capture dependencies so the
/// arithmetic (origin offset + size scaling, degenerate-size clamp) is unit-testable. Public for
/// that reason; the rest of the tray surface machinery stays internal.
/// </summary>
public static class SurfaceMath
{
    public static Rectangle ToScreen(Rectangle surface, GameRegionRect region)
    {
        var x = surface.X + (int)Math.Round(region.X * surface.Width);
        var y = surface.Y + (int)Math.Round(region.Y * surface.Height);
        var width = Math.Max(1, (int)Math.Round(region.Width * surface.Width));
        var height = Math.Max(1, (int)Math.Round(region.Height * surface.Height));
        return new Rectangle(x, y, width, height);
    }

    /// <summary>Inverse of <see cref="ToScreen"/>: express a screen rectangle as a normalized
    /// region within the surface, clamped to [0,1] and kept inside the surface even if the user
    /// dragged past an edge. Used when capturing regions to save into a game profile.</summary>
    public static GameRegionRect ToNormalized(Rectangle surface, Rectangle screen)
    {
        if (surface.Width <= 0 || surface.Height <= 0)
        {
            return new GameRegionRect(0, 0, 0, 0);
        }

        static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

        var x = Clamp01((screen.X - surface.X) / (double)surface.Width);
        var y = Clamp01((screen.Y - surface.Y) / (double)surface.Height);
        var width = Clamp01(screen.Width / (double)surface.Width);
        var height = Clamp01(screen.Height / (double)surface.Height);

        if (x + width > 1.0)
        {
            width = 1.0 - x;
        }

        if (y + height > 1.0)
        {
            height = 1.0 - y;
        }

        return new GameRegionRect(x, y, width, height);
    }
}
