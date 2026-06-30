using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Core.Providers;

public sealed class BilibiliMediaSourceProvider : IVideoMediaSourceProvider
{
    private readonly SpeechService _speechService;

    public BilibiliMediaSourceProvider(SpeechService speechService)
    {
        _speechService = speechService;
    }

    public string Name => "bilibili";

    public bool CanHandle(string sourceUrl)
        => Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) &&
           (uri.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase));

    public async Task<VideoMediaMetadata> ResolveAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        var (durationSeconds, title) = await _speechService.ProbeVideoMetadataAsync(sourceUrl, cancellationToken);
        return new VideoMediaMetadata(
            "bilibili",
            TryGetVideoId(sourceUrl),
            title,
            durationSeconds);
    }

    public Task<IReadOnlyList<SpeechSegment>> TryLoadCaptionsAsync(
        string sourceUrl,
        string language,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SpeechSegment>>(Array.Empty<SpeechSegment>());

    public Task<VideoAudioSection> DownloadAudioSectionAsync(
        string sourceUrl,
        double startSeconds,
        double durationSeconds,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => _speechService.DownloadYouTubeAudioSectionAsync(
            sourceUrl,
            startSeconds,
            durationSeconds,
            outputDirectory,
            cancellationToken);

    private static string TryGetVideoId(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.FirstOrDefault(part => part.StartsWith("BV", StringComparison.OrdinalIgnoreCase) ||
                                            part.StartsWith("av", StringComparison.OrdinalIgnoreCase))
            ?? uri.Host;
    }
}
