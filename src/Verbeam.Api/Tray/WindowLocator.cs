using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Verbeam.Api.Tray;

/// <summary>
/// Finds and tracks a target game window by process name (optionally refined by a window-title
/// regex), and reports its client area in screen coordinates. Under Per-Monitor-V2 DPI awareness
/// (see app.manifest) GetClientRect/ClientToScreen return true physical pixels, so the rect lines
/// up with CopyFromScreen and the WinForms overlays. A still-valid handle is reused across ticks;
/// processes are only re-enumerated once the handle goes away. Returns false when the window is
/// missing or minimized so the caller can hide the affected region overlays.
/// </summary>
internal sealed class WindowLocator
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private readonly string _processName;
    private readonly Regex? _titlePattern;
    private IntPtr _hwnd;

    public WindowLocator(string processName, string titlePattern)
    {
        var name = (processName ?? string.Empty).Trim();
        // Accept both "eldenring" and "eldenring.exe"; Process names carry no extension.
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        _processName = name;
        _titlePattern = string.IsNullOrWhiteSpace(titlePattern)
            ? null
            : new Regex(titlePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public bool TryGetClientRectOnScreen(out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        var hwnd = ResolveHandle();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        if (!GetClientRect(hwnd, out var client))
        {
            return false;
        }

        var topLeft = new NativePoint { X = client.Left, Y = client.Top };
        var bottomRight = new NativePoint { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(hwnd, ref topLeft) || !ClientToScreen(hwnd, ref bottomRight))
        {
            return false;
        }

        var width = bottomRight.X - topLeft.X;
        var height = bottomRight.Y - topLeft.Y;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        bounds = new Rectangle(topLeft.X, topLeft.Y, width, height);
        return true;
    }

    private IntPtr ResolveHandle()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            return _hwnd;
        }

        _hwnd = IntPtr.Zero;
        if (_processName.Length == 0)
        {
            return IntPtr.Zero;
        }

        foreach (var process in System.Diagnostics.Process.GetProcessesByName(_processName))
        {
            try
            {
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero || !IsWindow(handle))
                {
                    continue;
                }

                if (_titlePattern is not null && !_titlePattern.IsMatch(process.MainWindowTitle ?? string.Empty))
                {
                    continue;
                }

                _hwnd = handle;
                break;
            }
            catch
            {
                // Process exited between enumeration and inspection; skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return _hwnd;
    }
}
