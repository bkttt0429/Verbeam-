using System.Text.Json;
using YomiBridge.Core.Models;

namespace YomiBridge.Core.Providers;

internal static class SpeechJsonResultParser
{
    public static SpeechProviderResult Parse(string stdout, string engineName)
    {
        var trimmed = stdout.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new SpeechProviderResult(string.Empty, Array.Empty<SpeechSegment>(), engineName);
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                return document.RootElement.ValueKind == JsonValueKind.Array
                    ? ParseArray(document.RootElement, engineName)
                    : ParseObject(document.RootElement, engineName);
            }
            catch (JsonException)
            {
                // Fall through to plain text output.
            }
        }

        return SingleSegment(trimmed, engineName);
    }

    private static SpeechProviderResult ParseObject(JsonElement root, string engineName)
    {
        var text = GetString(root, "text") ?? GetString(root, "transcript") ?? string.Empty;
        var segments = root.TryGetProperty("segments", out var segmentsElement) && segmentsElement.ValueKind == JsonValueKind.Array
            ? ParseSegments(segmentsElement)
            : Array.Empty<SpeechSegment>();

        if (segments.Count == 0 && root.TryGetProperty("result", out var resultElement))
        {
            if (resultElement.ValueKind == JsonValueKind.Array)
            {
                segments = ParseSegments(resultElement);
            }
            else if (resultElement.ValueKind == JsonValueKind.String)
            {
                text = resultElement.GetString() ?? text;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = JoinSegmentText(segments);
        }

        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            segments = [new SpeechSegment(0, 0, 0, text, 1.0, null, null)];
        }

        return new SpeechProviderResult(text, NormalizeSegmentIndexes(segments), engineName);
    }

    private static SpeechProviderResult ParseArray(JsonElement root, string engineName)
    {
        var segments = ParseSegments(root);
        var text = JoinSegmentText(segments);
        return new SpeechProviderResult(text, NormalizeSegmentIndexes(segments), engineName);
    }

    private static IReadOnlyList<SpeechSegment> ParseSegments(JsonElement root)
    {
        var segments = new List<SpeechSegment>();
        var index = 0;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var lineText = item.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(lineText))
                {
                    segments.Add(new SpeechSegment(index, index * 2, index * 2 + 2, lineText.Trim(), 1.0, null, null));
                    index++;
                }

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = GetString(item, "text") ?? GetString(item, "sentence") ?? GetString(item, "transcript") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var start = GetDouble(item, "start")
                ?? GetDouble(item, "startSeconds")
                ?? GetDouble(item, "start_time")
                ?? GetDouble(item, "startTime")
                ?? GetTimestampValue(item, first: true)
                ?? index * 2;
            var end = GetDouble(item, "end")
                ?? GetDouble(item, "endSeconds")
                ?? GetDouble(item, "end_time")
                ?? GetDouble(item, "endTime")
                ?? GetTimestampValue(item, first: false)
                ?? start;
            var id = GetInt(item, "id") ?? GetInt(item, "index") ?? index;
            var confidence = GetDouble(item, "confidence") ?? GetDouble(item, "score") ?? 1.0;
            var speaker = GetString(item, "speaker") ?? GetString(item, "spk") ?? GetString(item, "speaker_id");
            var language = GetString(item, "language") ?? GetString(item, "lang");

            segments.Add(new SpeechSegment(
                id,
                NormalizeTime(start),
                NormalizeTime(end),
                text.Trim(),
                Math.Clamp(confidence, 0.0, 1.0),
                speaker,
                language));
            index++;
        }

        return segments;
    }

    private static IReadOnlyList<SpeechSegment> NormalizeSegmentIndexes(IReadOnlyList<SpeechSegment> segments)
        => segments
            .Select((segment, index) => segment with { Index = index })
            .ToArray();

    private static SpeechProviderResult SingleSegment(string text, string engineName)
        => new(text, [new SpeechSegment(0, 0, 0, text, 1.0, null, null)], engineName);

    private static string JoinSegmentText(IReadOnlyList<SpeechSegment> segments)
        => string.Join(Environment.NewLine, segments.Select(segment => segment.Text).Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static double? GetDouble(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var parsed) ? parsed : null;

    private static double? GetTimestampValue(JsonElement element, bool first)
    {
        if (!element.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = timestamp.EnumerateArray().ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        if (values[0].ValueKind == JsonValueKind.Array)
        {
            var nested = values[0].EnumerateArray().ToArray();
            var position = first ? 0 : 1;
            return nested.Length > position && nested[position].TryGetDouble(out var parsed) ? parsed : null;
        }

        var flatPosition = first ? 0 : 1;
        return values.Length > flatPosition && values[flatPosition].TryGetDouble(out var value) ? value : null;
    }

    private static double NormalizeTime(double value)
        => value > 1000 ? value / 1000 : value;
}
