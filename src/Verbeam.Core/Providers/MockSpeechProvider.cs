using System.Text;
using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public sealed class MockSpeechProvider : ISpeechProvider
{
    public SpeechProviderDescriptor Descriptor { get; } = new(
        "mock",
        "Mock ASR Provider",
        "test",
        "ja",
        RequiresExternalProcess: false,
        IsLocal: true);

    public Task<SpeechProviderResult> TranscribeAsync(
        SpeechProviderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = TryReadTextPayload(request.AudioBytes);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = $"[mock-asr: received {request.AudioBytes.Length} bytes]";
        }

        var lines = text
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            lines = [text.Trim()];
        }

        var segments = lines
            .Select((line, index) => new SpeechSegment(
                index,
                index * 2,
                index * 2 + 2,
                line,
                1.0,
                null,
                request.Language))
            .ToArray();

        return Task.FromResult(new SpeechProviderResult(
            string.Join(Environment.NewLine, segments.Select(segment => segment.Text)),
            segments,
            "mock"));
    }

    private static string TryReadTextPayload(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.All(IsReadableTextChar) ? text : string.Empty;
    }

    private static bool IsReadableTextChar(char value)
        => !char.IsControl(value) || char.IsWhiteSpace(value);
}
