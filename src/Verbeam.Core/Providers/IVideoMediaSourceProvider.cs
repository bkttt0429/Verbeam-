using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public interface IVideoMediaSourceProvider
{
    string Name { get; }

    bool CanHandle(string sourceUrl);

    Task<VideoMediaMetadata> ResolveAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SpeechSegment>> TryLoadCaptionsAsync(
        string sourceUrl,
        string language,
        CancellationToken cancellationToken = default);

    Task<VideoAudioSection> DownloadAudioSectionAsync(
        string sourceUrl,
        double startSeconds,
        double durationSeconds,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
