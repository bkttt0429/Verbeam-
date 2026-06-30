using System.Globalization;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

/// <summary>
/// Fixed window/section grid for video speech sessions. Windows are carved
/// per section so a window never crosses a section boundary, and repeated
/// position updates always map to bit-identical window bounds.
/// </summary>
public static class VideoSpeechWindowGrid
{
    public readonly record struct GridWindow(double StartSeconds, double EndSeconds, int SectionIndex)
    {
        public double SectionStartSeconds(double sectionSeconds) => SectionIndex * sectionSeconds;

        public double SectionEndSeconds(double sectionSeconds) => (SectionIndex + 1) * sectionSeconds;
    }

    public static double SectionLength(VideoSpeechOptions options)
        => Math.Max(60, options.SectionSeconds);

    public static double WindowLength(VideoSpeechOptions options)
        => Math.Clamp(options.WindowSeconds, 15, SectionLength(options));

    public static int SectionIndexOf(double seconds, double sectionSeconds)
        => (int)Math.Floor(Math.Max(0, seconds) / sectionSeconds);

    public static GridWindow WindowFor(double seconds, double sectionSeconds, double windowSeconds)
    {
        var section = SectionIndexOf(seconds, sectionSeconds);
        var sectionStart = section * sectionSeconds;
        var slot = (int)Math.Floor((Math.Max(0, seconds) - sectionStart) / windowSeconds);
        var start = sectionStart + slot * windowSeconds;
        var end = Math.Min(start + windowSeconds, sectionStart + sectionSeconds);
        return new GridWindow(start, end, section);
    }

    public static GridWindow? NextWindow(GridWindow window, double sectionSeconds, double windowSeconds, double durationSeconds)
    {
        // A window's end always coincides with the next grid window's start,
        // including at section boundaries (end == sectionEnd == next section start).
        var next = WindowFor(window.EndSeconds, sectionSeconds, windowSeconds);
        if (durationSeconds > 0 && next.StartSeconds >= durationSeconds)
        {
            return null;
        }

        return durationSeconds > 0 && next.EndSeconds > durationSeconds
            ? next with { EndSeconds = durationSeconds }
            : next;
    }

    public static IReadOnlyList<GridWindow> EnumerateWindows(
        double fromSeconds,
        double toSeconds,
        double sectionSeconds,
        double windowSeconds,
        double durationSeconds)
    {
        var values = new List<GridWindow>();
        if (durationSeconds > 0)
        {
            toSeconds = Math.Min(toSeconds, durationSeconds);
        }

        var current = WindowFor(Math.Max(0, fromSeconds), sectionSeconds, windowSeconds);
        if (durationSeconds > 0 && current.StartSeconds >= durationSeconds)
        {
            return values;
        }

        if (durationSeconds > 0 && current.EndSeconds > durationSeconds)
        {
            current = current with { EndSeconds = durationSeconds };
        }

        values.Add(current);
        while (current.EndSeconds < toSeconds)
        {
            var next = NextWindow(current, sectionSeconds, windowSeconds, durationSeconds);
            if (next is null)
            {
                break;
            }

            current = next.Value;
            values.Add(current);
        }

        return values;
    }

    public static string WindowKey(string sessionId, GridWindow window)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{sessionId}:{window.StartSeconds:F3}:{window.EndSeconds:F3}");
}
