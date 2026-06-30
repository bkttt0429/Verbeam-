using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Core.Providers;

public sealed class YouTubeMediaSourceProvider : IVideoMediaSourceProvider
{
    private readonly SpeechService _speechService;

    public YouTubeMediaSourceProvider(SpeechService speechService)
    {
        _speechService = speechService;
    }

    public string Name => "youtube";

    public bool CanHandle(string sourceUrl)
        => SpeechService.IsYouTubeUrl(sourceUrl);

    public async Task<VideoMediaMetadata> ResolveAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        var videoId = TryGetYouTubeVideoId(sourceUrl);
        var (durationSeconds, title) = await _speechService.ProbeVideoMetadataAsync(sourceUrl, cancellationToken);
        return new VideoMediaMetadata(
            "youtube",
            videoId,
            title,
            durationSeconds);
    }

    public Task<IReadOnlyList<SpeechSegment>> TryLoadCaptionsAsync(
        string sourceUrl,
        string language,
        CancellationToken cancellationToken = default)
        => _speechService.TryLoadYouTubeCaptionsAsync(sourceUrl, language, cancellationToken);

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

    private static string TryGetYouTubeVideoId(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.Trim('/');
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0].Equals("v", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pieces[1]);
            }
        }

        return string.Empty;
    }
}
