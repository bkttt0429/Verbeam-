using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Verbeam.Api.Tray;

public sealed record ScreenRect(int X, int Y, int Width, int Height)
{
    public static ScreenRect From(Rectangle rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    public Rectangle ToRectangle() => new(X, Y, Width, Height);
}

public sealed record NativeRegionSnapshot(
    int Index,
    ScreenRect Bounds,
    bool Busy,
    string LastOcrText);

public sealed record NativeSurfaceMap(
    DateTimeOffset CapturedAt,
    NativeWindowIdentity? ForegroundWindow,
    bool IgnoreOwnProcessOverlays,
    IReadOnlyList<NativeRegionSurfaceMap> Regions);

public sealed record NativeRegionSurfaceMap(
    int Index,
    ScreenRect Region,
    string WindowKey,
    string SurfaceKey,
    NativeWindowIdentity? DominantWindow,
    NativeWindowIdentity? TopWindowAtCenter,
    NativeWindowIdentity? ContextWindowAtCenter,
    double DominantVisibleRatio,
    double RawDominantVisibleRatio,
    double CoveredRatio,
    double RawCoveredRatio,
    double OwnOverlayRatio,
    bool Occluded,
    bool OwnOverlayPresent,
    IReadOnlyList<NativeRegionWindowLayer> Layers);

public sealed record NativeRegionWindowLayer(
    int ZIndex,
    NativeWindowIdentity Window,
    ScreenRect WindowRect,
    ScreenRect Intersection,
    double VisibleRatio,
    double ContextVisibleRatio,
    double IntersectRatio,
    bool IsDominant,
    bool IsForeground,
    bool IsOwnProcess);

public sealed record NativeWindowIdentity(
    string Hwnd,
    int ProcessId,
    string ProcessName,
    string Title,
    string TitleBucket);

public sealed class VisibleWindowSurfaceAnalyzer
{
    private const uint GwHwndNext = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int MaxGridColumns = 48;
    private const int MaxGridRows = 32;

    private readonly int _ownProcessId = Environment.ProcessId;

    public NativeSurfaceMap Analyze(IReadOnlyList<NativeRegionSnapshot> regions, bool ignoreOwnProcessOverlays = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NativeSurfaceMap(DateTimeOffset.UtcNow, null, ignoreOwnProcessOverlays, []);
        }

        var windows = EnumerateWindows();
        var foreground = ReadWindowIdentity(GetForegroundWindow());
        var maps = regions
            .Where(region => region.Bounds.Width > 0 && region.Bounds.Height > 0)
            .OrderBy(region => region.Index)
            .Select(region => AnalyzeRegion(region, windows, foreground, ignoreOwnProcessOverlays))
            .ToArray();

        return new NativeSurfaceMap(DateTimeOffset.UtcNow, foreground, ignoreOwnProcessOverlays, maps);
    }

    private NativeRegionSurfaceMap AnalyzeRegion(
        NativeRegionSnapshot snapshot,
        IReadOnlyList<WindowEntry> windows,
        NativeWindowIdentity? foreground,
        bool ignoreOwnProcessOverlays)
    {
        var region = snapshot.Bounds.ToRectangle();
        var area = Math.Max(1.0, region.Width * (double)region.Height);
        var rawCounts = SampleVisibleWindows(region, windows, ignoreOwnProcess: false);
        var contextCounts = SampleVisibleWindows(region, windows, ignoreOwnProcess: ignoreOwnProcessOverlays);
        var totalSamples = Math.Max(1, rawCounts.TotalSamples);
        var foregroundHwnd = foreground?.Hwnd ?? string.Empty;

        var candidates = windows
            .Where(window => window.Rect.IntersectsWith(region))
            .Select((window, zIndex) =>
            {
                var intersection = Rectangle.Intersect(window.Rect, region);
                rawCounts.ByHwnd.TryGetValue(window.Hwnd, out var rawSampleCount);
                contextCounts.ByHwnd.TryGetValue(window.Hwnd, out var contextSampleCount);
                var rawVisibleRatio = rawSampleCount / (double)totalSamples;
                var contextVisibleRatio = contextSampleCount / (double)totalSamples;
                return new NativeRegionWindowLayer(
                    ZIndex: zIndex,
                    Window: window.Identity,
                    WindowRect: ScreenRect.From(window.Rect),
                    Intersection: ScreenRect.From(intersection),
                    VisibleRatio: RoundRatio(rawVisibleRatio),
                    ContextVisibleRatio: RoundRatio(contextVisibleRatio),
                    IntersectRatio: RoundRatio(intersection.Width * (double)intersection.Height / area),
                    IsDominant: false,
                    IsForeground: string.Equals(window.Identity.Hwnd, foregroundHwnd, StringComparison.OrdinalIgnoreCase),
                    IsOwnProcess: window.Identity.ProcessId == _ownProcessId);
            })
            .Where(layer => layer.Intersection.Width > 0 && layer.Intersection.Height > 0)
            .OrderBy(layer => layer.ZIndex)
            .ThenByDescending(layer => layer.VisibleRatio)
            .ToArray();

        var dominant = candidates
            .Where(layer => !layer.IsOwnProcess && layer.ContextVisibleRatio > 0)
            .OrderByDescending(layer => layer.ContextVisibleRatio)
            .ThenBy(layer => layer.ZIndex)
            .FirstOrDefault()
            ?? candidates
                .Where(layer => !layer.IsOwnProcess && layer.VisibleRatio > 0)
                .OrderByDescending(layer => layer.VisibleRatio)
                .ThenBy(layer => layer.ZIndex)
                .FirstOrDefault()
            ?? candidates.OrderByDescending(layer => layer.VisibleRatio).ThenBy(layer => layer.ZIndex).FirstOrDefault();

        var topAtCenter = FindTopWindowAtPoint(
            region.Left + Math.Max(0, region.Width / 2),
            region.Top + Math.Max(0, region.Height / 2),
            windows,
            ignoreOwnProcess: false);
        var contextAtCenter = FindTopWindowAtPoint(
            region.Left + Math.Max(0, region.Width / 2),
            region.Top + Math.Max(0, region.Height / 2),
            windows,
            ignoreOwnProcess: ignoreOwnProcessOverlays);
        var dominantHwnd = dominant?.Window.Hwnd ?? string.Empty;
        var layers = candidates
            .Select(layer => layer with
            {
                IsDominant = string.Equals(layer.Window.Hwnd, dominantHwnd, StringComparison.OrdinalIgnoreCase)
            })
            .ToArray();

        var coveredRatio = RoundRatio(contextCounts.CoveredSamples / (double)totalSamples);
        var rawCoveredRatio = RoundRatio(rawCounts.CoveredSamples / (double)totalSamples);
        var dominantRatio = dominant?.ContextVisibleRatio ?? dominant?.VisibleRatio ?? 0;
        var rawDominantRatio = dominant?.VisibleRatio ?? 0;
        var ownOverlayRatio = RoundRatio(layers.Where(layer => layer.IsOwnProcess).Sum(layer => layer.VisibleRatio));
        var ownOverlayPresent = ownOverlayRatio >= 0.02;
        var occluded = layers.Any(layer => !layer.IsDominant && !layer.IsOwnProcess && layer.ContextVisibleRatio >= 0.08);
        var windowKey = BuildWindowKey(dominant?.Window);

        return new NativeRegionSurfaceMap(
            snapshot.Index,
            snapshot.Bounds,
            windowKey,
            BuildSurfaceKey(windowKey, snapshot.Index),
            dominant?.Window,
            topAtCenter?.Identity,
            contextAtCenter?.Identity,
            dominantRatio,
            rawDominantRatio,
            coveredRatio,
            rawCoveredRatio,
            ownOverlayRatio,
            occluded,
            ownOverlayPresent,
            layers);
    }

    private static string BuildWindowKey(NativeWindowIdentity? dominant)
    {
        if (dominant is null)
        {
            return "native|desktop";
        }

        return string.Join(
            "|",
            "native",
            $"proc:{NormalizeKeyPart(dominant.ProcessName)}",
            $"hwnd:{NormalizeKeyPart(dominant.Hwnd)}",
            $"title:{NormalizeKeyPart(dominant.TitleBucket)}");
    }

    private static string BuildSurfaceKey(string windowKey, int regionIndex) => $"{windowKey}|region:{regionIndex}";

    private static string NormalizeKeyPart(string? value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", "-");
        normalized = Regex.Replace(normalized, @"[^a-z0-9._:-]+", "-").Trim('-');
        return normalized.Length == 0 ? "unknown" : normalized;
    }

    private WindowSampleCounts SampleVisibleWindows(Rectangle region, IReadOnlyList<WindowEntry> windows, bool ignoreOwnProcess)
    {
        var columns = Math.Clamp(region.Width / 32, 12, MaxGridColumns);
        var rows = Math.Clamp(region.Height / 32, 8, MaxGridRows);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var covered = 0;

        for (var row = 0; row < rows; row++)
        {
            var y = region.Top + (int)Math.Round((row + 0.5) * region.Height / rows);
            y = Math.Min(region.Bottom - 1, Math.Max(region.Top, y));
            for (var column = 0; column < columns; column++)
            {
                var x = region.Left + (int)Math.Round((column + 0.5) * region.Width / columns);
                x = Math.Min(region.Right - 1, Math.Max(region.Left, x));
                var window = FindTopWindowAtPoint(x, y, windows, ignoreOwnProcess);
                if (window is null)
                {
                    continue;
                }

                covered++;
                counts.TryGetValue(window.Identity.Hwnd, out var count);
                counts[window.Identity.Hwnd] = count + 1;
            }
        }

        return new WindowSampleCounts(columns * rows, covered, counts);
    }

    private WindowEntry? FindTopWindowAtPoint(int x, int y, IReadOnlyList<WindowEntry> windows, bool ignoreOwnProcess)
        => windows.FirstOrDefault(window =>
            (!ignoreOwnProcess || window.Identity.ProcessId != _ownProcessId) &&
            window.Rect.Contains(x, y));

    private static double RoundRatio(double value) => Math.Round(Math.Clamp(value, 0.0, 1.0), 4);

    private IReadOnlyList<WindowEntry> EnumerateWindows()
    {
        var entries = new List<WindowEntry>();
        var hwnd = GetTopWindow(IntPtr.Zero);
        var zIndex = 0;
        while (hwnd != IntPtr.Zero && zIndex < 512)
        {
            if (TryReadWindow(hwnd, out var entry))
            {
                entries.Add(entry);
            }

            hwnd = GetWindow(hwnd, GwHwndNext);
            zIndex++;
        }

        return entries;
    }

    private bool TryReadWindow(IntPtr hwnd, out WindowEntry entry)
    {
        entry = default!;
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd))
        {
            return false;
        }

        if (!TryGetWindowBounds(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        var identity = ReadWindowIdentity(hwnd);
        if (identity is null)
        {
            return false;
        }

        entry = new WindowEntry(hwnd.ToString("X"), rect, identity);
        return true;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out Rectangle rect)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out NativeRect frame, Marshal.SizeOf<NativeRect>()) == 0)
        {
            rect = ToRectangle(frame);
            if (rect.Width > 0 && rect.Height > 0)
            {
                return true;
            }
        }

        if (GetWindowRect(hwnd, out var windowRect))
        {
            rect = ToRectangle(windowRect);
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = Rectangle.Empty;
        return false;
    }

    private NativeWindowIdentity? ReadWindowIdentity(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var processName = "unknown";
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch
        {
            // Process exited or access denied; keep a stable fallback.
        }

        var title = ReadTitle(hwnd);
        return new NativeWindowIdentity(
            Hwnd: hwnd.ToString("X"),
            ProcessId: (int)processId,
            ProcessName: processName,
            Title: title,
            TitleBucket: BuildTitleBucket(title));
    }

    private static string ReadTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string BuildTitleBucket(string title)
    {
        var bucket = Regex.Replace((title ?? string.Empty).Trim(), @"\s+", " ");
        bucket = Regex.Replace(bucket, @"\d{2,}", "#");
        return bucket.Length <= 80 ? bucket : bucket[..80];
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static Rectangle ToRectangle(NativeRect rect)
        => Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private sealed record WindowEntry(string Hwnd, Rectangle Rect, NativeWindowIdentity Identity);

    private sealed record WindowSampleCounts(
        int TotalSamples,
        int CoveredSamples,
        IReadOnlyDictionary<string, int> ByHwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
