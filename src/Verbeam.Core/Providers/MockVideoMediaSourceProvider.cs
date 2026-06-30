using System.Text;
using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public sealed class MockVideoMediaSourceProvider : IVideoMediaSourceProvider
{
    public string Name => "mock";

    public bool CanHandle(string sourceUrl)
        => sourceUrl.StartsWith("mock://", StringComparison.OrdinalIgnoreCase);

    public Task<VideoMediaMetadata> ResolveAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new VideoMediaMetadata(
            "mock",
            sourceUrl.Replace("mock://", string.Empty, StringComparison.OrdinalIgnoreCase),
            "Mock video",
            600));

    public Task<IReadOnlyList<SpeechSegment>> TryLoadCaptionsAsync(
        string sourceUrl,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (!sourceUrl.Contains("captions", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IReadOnlyList<SpeechSegment>>(Array.Empty<SpeechSegment>());
        }

        IReadOnlyList<SpeechSegment> segments =
        [
            new SpeechSegment(0, 0, 2, "mock caption one", 1, null, language),
            new SpeechSegment(1, 2, 4, "mock caption two", 1, null, language)
        ];
        return Task.FromResult(segments);
    }

    public async Task<VideoAudioSection> DownloadAudioSectionAsync(
        string sourceUrl,
        double startSeconds,
        double durationSeconds,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"mock-section-{Guid.NewGuid():N}.txt");
        var text = $"mock speech {Math.Floor(startSeconds)}\nmock speech {Math.Floor(startSeconds + durationSeconds)}";
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(text), cancellationToken);
        var file = new FileInfo(path);
        return new VideoAudioSection(
            file.FullName,
            "text/plain",
            Math.Max(0, startSeconds),
            Math.Max(0, startSeconds) + Math.Max(1, durationSeconds),
            file.Length);
    }
}
