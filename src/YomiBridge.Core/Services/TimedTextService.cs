using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using YomiBridge.Core.Models;

namespace YomiBridge.Core.Services;

public static class TimedTextService
{
    private static readonly Regex CueTimeRegex = new(
        @"(?<start>\d{1,2}:)?\d{1,2}:\d{2}[\.,]\d{3}\s*-->\s*(?<end>\d{1,2}:)?\d{1,2}:\d{2}[\.,]\d{3}",
        RegexOptions.Compiled);

    public static IReadOnlyList<SpeechSegment> ParseVtt(string text, string? language = null)
        => ParseTimedText(text, language);

    public static IReadOnlyList<SpeechSegment> ParseSrt(string text, string? language = null)
        => ParseTimedText(text, language);

    public static string ToVtt(IReadOnlyList<SpeechTranslatedSegment> segments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("WEBVTT");
        builder.AppendLine();
        foreach (var segment in segments)
        {
            builder.AppendLine($"{FormatVttTime(segment.StartSeconds)} --> {FormatVttTime(segment.EndSeconds)}");
            builder.AppendLine(segment.TranslatedText);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string ToSrt(IReadOnlyList<SpeechTranslatedSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            builder.AppendLine((segment.Index + 1).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine($"{FormatSrtTime(segment.StartSeconds)} --> {FormatSrtTime(segment.EndSeconds)}");
            builder.AppendLine(segment.TranslatedText);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static IReadOnlyList<SpeechSegment> ParseTimedText(string text, string? language)
    {
        var normalized = text.ReplaceLineEndings("\n");
        var lines = normalized.Split('\n');
        var segments = new List<SpeechSegment>();
        var index = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].Trim();
            if (line.Length == 0 || line.Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!line.Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split("-->", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !TryParseTime(parts[0], out var start) ||
                !TryParseTime(parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var end))
            {
                continue;
            }

            var textLines = new List<string>();
            for (lineIndex++; lineIndex < lines.Length; lineIndex++)
            {
                var textLine = lines[lineIndex].Trim();
                if (textLine.Length == 0)
                {
                    break;
                }

                textLines.Add(CleanCueText(textLine));
            }

            var cueText = string.Join(" ", textLines.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            if (cueText.Length > 0)
            {
                segments.Add(new SpeechSegment(index, start, end, cueText, 1.0, null, language));
                index++;
            }
        }

        return NormalizeRollingCaptionSegments(segments);
    }

    private static IReadOnlyList<SpeechSegment> NormalizeRollingCaptionSegments(IReadOnlyList<SpeechSegment> segments)
    {
        if (segments.Count <= 1)
        {
            return segments;
        }

        var normalized = new List<SpeechSegment>(segments.Count);
        var previousCueText = string.Empty;

        foreach (var segment in segments)
        {
            var cueText = NormalizeWhitespace(segment.Text);
            var deltaText = RemoveRepeatedPrefix(previousCueText, cueText);
            previousCueText = cueText;

            if (deltaText.Length == 0)
            {
                continue;
            }

            normalized.Add(segment with
            {
                Index = normalized.Count,
                Text = deltaText
            });
        }

        return normalized;
    }

    private static string RemoveRepeatedPrefix(string previousCueText, string currentCueText)
    {
        if (string.IsNullOrWhiteSpace(currentCueText))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(previousCueText))
        {
            return currentCueText;
        }

        if (string.Equals(previousCueText, currentCueText, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (currentCueText.StartsWith(previousCueText, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeWhitespace(currentCueText[previousCueText.Length..]);
        }

        var maxOverlap = Math.Min(previousCueText.Length, currentCueText.Length);
        for (var length = maxOverlap; length >= 12; length--)
        {
            var previousSuffix = previousCueText[^length..];
            var currentPrefix = currentCueText[..length];
            if (string.Equals(previousSuffix, currentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeWhitespace(currentCueText[length..]);
            }
        }

        return currentCueText;
    }

    private static bool TryParseTime(string value, out double seconds)
    {
        seconds = 0;
        value = value.Trim().Replace(',', '.');
        var pieces = value.Split(':');
        if (pieces.Length is < 2 or > 3)
        {
            return false;
        }

        var offset = pieces.Length == 3 ? 0 : -1;
        var hoursText = pieces.Length == 3 ? pieces[0] : "0";
        var minutesText = pieces[1 + offset];
        var secondsText = pieces[2 + offset];

        if (!double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var secondPart) ||
            !int.TryParse(minutesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutePart) ||
            !int.TryParse(hoursText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hourPart))
        {
            return false;
        }

        seconds = hourPart * 3600 + minutePart * 60 + secondPart;
        return true;
    }

    private static string CleanCueText(string value)
        => NormalizeWhitespace(WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", string.Empty)));

    private static string NormalizeWhitespace(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();

    private static string FormatVttTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private static string FormatSrtTime(double seconds)
        => FormatVttTime(seconds).Replace('.', ',');
}
