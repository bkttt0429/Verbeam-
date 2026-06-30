using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Verbeam.Api.Tray;

public sealed record RegionSelectionSafetySnapshot(
    DateTimeOffset CapturedAt,
    bool IsWindows,
    bool CanOpenSelector,
    string ReasonCode,
    string Message,
    bool IsFullscreenLike,
    NativeWindowIdentity? ForegroundWindow,
    ScreenRect? WindowRect,
    ScreenRect? MonitorRect,
    double MonitorCoverageRatio);

/// <summary>
/// Guards actions that open a full-screen selection window. In exclusive or fullscreen-like games,
/// creating our selector can steal focus, minimize the game, or capture a black frame. Running saved
/// regions is still allowed; only interactive selector entry points should consult this service.
/// </summary>
public sealed class RegionSelectionSafety
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int BoundsTolerancePx = 6;
    private const double FullscreenCoverageThreshold = 0.985;

    private readonly int _ownProcessId = Environment.ProcessId;

    public RegionSelectionSafetySnapshot Check()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindows())
        {
            return Allow(capturedAt, isWindows: false, "non_windows", "Selection guard is only available on Windows.");
        }

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return Allow(capturedAt, isWindows: true, "no_foreground_window", "No foreground window was reported by Windows.");
        }

        var identity = ReadWindowIdentity(hwnd);
        if (identity is null)
        {
            return Allow(capturedAt, isWindows: true, "foreground_unavailable", "Foreground window details are unavailable.");
        }

        if (identity.ProcessId == _ownProcessId)
        {
            return Allow(capturedAt, isWindows: true, "foreground_is_verbeam", "Verbeam is the foreground app.");
        }

        if (!TryGetWindowBounds(hwnd, out var windowRect) || windowRect.Width <= 0 || windowRect.Height <= 0)
        {
            return Allow(capturedAt, isWindows: true, "foreground_bounds_unavailable", "Foreground window bounds are unavailable.", identity);
        }

        var monitorRect = Screen.FromHandle(hwnd).Bounds;
        if (monitorRect.Width <= 0 || monitorRect.Height <= 0)
        {
            return Allow(capturedAt, isWindows: true, "monitor_bounds_unavailable", "Monitor bounds are unavailable.", identity, windowRect);
        }

        var coverage = MonitorCoverage(windowRect, monitorRect);
        var fullscreenLike = IsFullscreenLike(windowRect, monitorRect, coverage);
        if (!fullscreenLike)
        {
            return new RegionSelectionSafetySnapshot(
                capturedAt,
                true,
                true,
                "foreground_not_fullscreen",
                "Foreground window is not fullscreen-like.",
                false,
                identity,
                ScreenRect.From(windowRect),
                ScreenRect.From(monitorRect),
                RoundRatio(coverage));
        }

        return new RegionSelectionSafetySnapshot(
            capturedAt,
            true,
            false,
            "fullscreen_like_foreground",
            "Fullscreen-like foreground detected. Use saved profile regions, or switch the game to borderless/windowed mode before editing regions.",
            true,
            identity,
            ScreenRect.From(windowRect),
            ScreenRect.From(monitorRect),
            RoundRatio(coverage));
    }

    private static RegionSelectionSafetySnapshot Allow(
        DateTimeOffset capturedAt,
        bool isWindows,
        string reasonCode,
        string message,
        NativeWindowIdentity? foregroundWindow = null,
        Rectangle? windowRect = null)
        => new(
            capturedAt,
            isWindows,
            true,
            reasonCode,
            message,
            false,
            foregroundWindow,
            windowRect.HasValue ? ScreenRect.From(windowRect.Value) : null,
            null,
            0);

    private static bool IsFullscreenLike(Rectangle windowRect, Rectangle monitorRect, double coverage)
    {
        var coversMonitorArea = coverage >= FullscreenCoverageThreshold;
        var reachesEdges =
            windowRect.Left <= monitorRect.Left + BoundsTolerancePx &&
            windowRect.Top <= monitorRect.Top + BoundsTolerancePx &&
            windowRect.Right >= monitorRect.Right - BoundsTolerancePx &&
            windowRect.Bottom >= monitorRect.Bottom - BoundsTolerancePx;
        var atLeastMonitorSized =
            windowRect.Width >= monitorRect.Width - BoundsTolerancePx &&
            windowRect.Height >= monitorRect.Height - BoundsTolerancePx;

        return coversMonitorArea && reachesEdges && atLeastMonitorSized;
    }

    private static double MonitorCoverage(Rectangle windowRect, Rectangle monitorRect)
    {
        var intersection = Rectangle.Intersect(windowRect, monitorRect);
        var monitorArea = Math.Max(1.0, monitorRect.Width * (double)monitorRect.Height);
        return intersection.Width * (double)intersection.Height / monitorArea;
    }

    private static double RoundRatio(double value) => Math.Round(Math.Clamp(value, 0.0, 1.0), 4);

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

    private static NativeWindowIdentity? ReadWindowIdentity(IntPtr hwnd)
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

    private static Rectangle ToRectangle(NativeRect rect)
        => Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
