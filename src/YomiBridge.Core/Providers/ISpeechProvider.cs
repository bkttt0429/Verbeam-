using YomiBridge.Core.Models;

namespace YomiBridge.Core.Providers;

public interface ISpeechProvider
{
    SpeechProviderDescriptor Descriptor { get; }

    Task<SpeechProviderResult> TranscribeAsync(
        SpeechProviderRequest request,
        CancellationToken cancellationToken);
}
