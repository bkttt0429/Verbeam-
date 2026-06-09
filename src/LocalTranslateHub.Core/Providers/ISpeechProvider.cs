using LocalTranslateHub.Core.Models;

namespace LocalTranslateHub.Core.Providers;

public interface ISpeechProvider
{
    SpeechProviderDescriptor Descriptor { get; }

    Task<SpeechProviderResult> TranscribeAsync(
        SpeechProviderRequest request,
        CancellationToken cancellationToken);
}
