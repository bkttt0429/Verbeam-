using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Verbeam.Core.Models;

namespace Verbeam.Api.Tray;

/// <summary>
/// Polls the OS foreground window on the tray UI thread and raises <see cref="ForegroundChanged"/>
/// whenever it changes to a different window, ignoring our own overlays/selection windows. Used to
/// auto-activate the game profile whose window just came to the front. Polling (vs. SetWinEventHook)
/// keeps it simple and robust; ~600ms latency on a profile switch is imperceptible here.
/// </summary>
internal sealed class ForegroundWindowWatcher : IDisposable
{
    public readonly record struct ForegroundInfo(IntPtr Hwnd, int ProcessId, string ProcessName, string Title);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private readonly System.Windows.Forms.Timer _timer;
    private readonly int _ownProcessId = Environment.ProcessId;
    private IntPtr _lastHwnd;

    public event Action<ForegroundInfo>? ForegroundChanged;

    public ForegroundWindowWatcher(int pollMs = 600)
    {
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(150, pollMs) };
        _timer.Tick += (_, _) => Poll();
    }

    public bool Enabled
    {
        get => _timer.Enabled;
        set => _timer.Enabled = value;
    }

    private void Poll()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == _lastHwnd)
        {
            return;
        }

        _lastHwnd = hwnd;

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || (int)processId == _ownProcessId)
        {
            // Zero, or one of our own windows (overlays / selection) — never auto-switch on those.
            return;
        }

        string processName;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch
        {
            // Process exited between the foreground read and the lookup; skip.
            return;
        }

        ForegroundChanged?.Invoke(new ForegroundInfo(hwnd, (int)processId, processName, ReadTitle(hwnd)));
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

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}

/// <summary>
/// Pure matching of a foreground window (process name + optional title) against the window-bound
/// game profiles. Kept dependency-free so the rule set is unit-testable.
/// </summary>
public static class ProfileMatcher
{
    public static GameProfile? Match(IReadOnlyList<GameProfile> profiles, string processName, string title)
    {
        var name = StripExe(processName);
        if (name.Length == 0)
        {
            return null;
        }

        foreach (var profile in profiles)
        {
            var binding = profile.Surface;
            if (binding is null || !string.Equals(binding.Kind, "window", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(StripExe(binding.ProcessName), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(binding.WindowTitlePattern))
            {
                try
                {
                    if (!Regex.IsMatch(title ?? string.Empty, binding.WindowTitlePattern, RegexOptions.IgnoreCase))
                    {
                        continue;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed title pattern degrades to process-name-only matching.
                }
            }

            return profile;
        }

        return null;
    }

    private static string StripExe(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
