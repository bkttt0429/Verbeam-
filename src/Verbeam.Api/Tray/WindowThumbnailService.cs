using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Verbeam.Api.Tray;

/// <summary>One alt-tab-style top-level application window.</summary>
public sealed record WindowListEntry(
    string Hwnd,
    int ProcessId,
    string ProcessName,
    string Title,
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>A captured window thumbnail plus the source window bounds (for building a region).</summary>
public sealed record WindowThumbnail(
    string Hwnd,
    string ImageBase64,
    string ImageMimeType,
    int ThumbWidth,
    int ThumbHeight,
    int WindowX,
    int WindowY,
    int WindowWidth,
    int WindowHeight,
    string Method,
    bool Degraded);

/// <summary>
/// Enumerates real application windows (alt-tab/taskbar semantics) and captures per-window
/// thumbnails via PrintWindow(PW_RENDERFULLCONTENT). Verified on this machine to capture UWP and
/// GPU-composited windows (Settings, Windows Terminal) without black frames; falls back to
/// CopyFromScreen when PrintWindow returns a (near-)black frame (DRM/protected/some GPU surfaces).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowThumbnailService
{
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const int DwmwaCloaked = 14;
    private const int DwmwaExtendedFrameBounds = 9;
    private const uint PwRenderFullContent = 0x00000002;

    private readonly int _ownProcessId = Environment.ProcessId;

    public IReadOnlyList<WindowListEntry> ListWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var shell = GetShellWindow();
        var result = new List<WindowListEntry>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsAltTabWindow(hwnd, shell))
            {
                return true;
            }

            if (!TryGetBounds(hwnd, out var rect) || rect.Width < 32 || rect.Height < 32)
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            result.Add(new WindowListEntry(
                hwnd.ToString("X"),
                (int)pid,
                ReadProcessName((int)pid),
                ReadTitle(hwnd),
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height));
            return true;
        }, IntPtr.Zero);

        // Largest windows first — the user's foreground apps tend to be biggest.
        return result
            .OrderByDescending(window => (long)window.Width * window.Height)
            .ToArray();
    }

    public WindowThumbnail? CaptureThumbnail(string hwndHex, int maxWidth = 320)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!TryParseHwnd(hwndHex, out var hwnd) || !IsWindow(hwnd) || IsIconic(hwnd))
        {
            return null;
        }

        if (!TryGetBounds(hwnd, out var rect) || rect.Width < 1 || rect.Height < 1)
        {
            return null;
        }

        var (bitmap, method, degraded) = CaptureWindow(hwnd, rect);
        if (bitmap is null)
        {
            return null;
        }

        using (bitmap)
        using (var thumb = Downscale(bitmap, Math.Clamp(maxWidth, 64, 1024)))
        {
            return new WindowThumbnail(
                hwnd.ToString("X"),
                EncodePngBase64(thumb),
                "image/png",
                thumb.Width,
                thumb.Height,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                method,
                degraded);
        }
    }

    private (Bitmap?, string Method, bool Degraded) CaptureWindow(IntPtr hwnd, Rectangle rect)
    {
        var printed = TryPrintWindow(hwnd, rect.Width, rect.Height);
        if (printed is not null && BlackPercent(printed) < 0.97)
        {
            return (printed, "printwindow", false);
        }

        // PrintWindow gave a black/empty frame (protected or GPU-direct surface).
        // Fall back to a plain screen grab of the window's on-screen rectangle.
        printed?.Dispose();
        var grabbed = TryCopyFromScreen(rect);
        if (grabbed is not null && BlackPercent(grabbed) < 0.97)
        {
            return (grabbed, "copyfromscreen", true);
        }

        // Both methods failed — return whatever the screen grab produced (may be black),
        // marked degraded so the UI can show a placeholder.
        return grabbed is not null ? (grabbed, "copyfromscreen", true) : (null, "none", true);
    }

    private static Bitmap? TryPrintWindow(IntPtr hwnd, int width, int height)
    {
        try
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, hdc, PwRenderFullContent))
                {
                    graphics.ReleaseHdc(hdc);
                    var retryHdc = graphics.GetHdc();
                    PrintWindow(hwnd, retryHdc, 0);
                    graphics.ReleaseHdc(retryHdc);
                    return bitmap;
                }
            }
            finally
            {
                try { graphics.ReleaseHdc(hdc); } catch { /* already released on the retry path */ }
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? TryCopyFromScreen(Rectangle rect)
    {
        try
        {
            var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(rect.Location, Point.Empty, rect.Size);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    // Grid-sample the fraction of transparent / near-black pixels; high == failed capture.
    private static double BlackPercent(Bitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.Width / 40);
        var stepY = Math.Max(1, bitmap.Height / 40);
        var total = 0;
        var blackish = 0;
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var c = bitmap.GetPixel(x, y);
                total++;
                if (c.A < 8 || (c.R < 12 && c.G < 12 && c.B < 12))
                {
                    blackish++;
                }
            }
        }

        return total == 0 ? 1.0 : (double)blackish / total;
    }

    private static Bitmap Downscale(Bitmap source, int maxWidth)
    {
        if (source.Width <= maxWidth)
        {
            return new Bitmap(source);
        }

        var newHeight = Math.Max(1, (int)Math.Round(source.Height * (maxWidth / (double)source.Width)));
        var destination = new Bitmap(maxWidth, newHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(destination);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, maxWidth, newHeight);
        return destination;
    }

    private static string EncodePngBase64(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToBase64String(stream.ToArray());
    }

    private bool IsAltTabWindow(IntPtr hwnd, IntPtr shell)
    {
        if (hwnd == IntPtr.Zero || hwnd == shell)
        {
            return false;
        }

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd))
        {
            return false;
        }

        if (GetWindowTextLength(hwnd) == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if ((int)pid == _ownProcessId)
        {
            return false;
        }

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var appWindow = (exStyle & WsExAppWindow) != 0;
        var toolWindow = (exStyle & WsExToolWindow) != 0;
        var owner = GetWindow(hwnd, GwOwner);

        // Alt-tab rule: explicit app windows, or unowned top-levels; never a tool window.
        if (toolWindow && !appWindow)
        {
            return false;
        }

        return appWindow || owner == IntPtr.Zero;
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

    private static bool TryGetBounds(IntPtr hwnd, out Rectangle rect)
    {
        if (DwmGetWindowAttributeRect(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<NativeRect>()) == 0)
        {
            rect = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            if (rect.Width > 0 && rect.Height > 0)
            {
                return true;
            }
        }

        if (GetWindowRect(hwnd, out var windowRect))
        {
            rect = Rectangle.FromLTRB(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom);
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = Rectangle.Empty;
        return false;
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

    private static string ReadProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool TryParseHwnd(string hwndHex, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(hwndHex))
        {
            return false;
        }

        var trimmed = hwndHex.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (long.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            hwnd = new IntPtr(value);
            return hwnd != IntPtr.Zero;
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int attribute, out NativeRect value, int size);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
