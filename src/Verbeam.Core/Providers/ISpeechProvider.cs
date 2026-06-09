using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public interface ISpeechProvider
{
    SpeechProviderDescriptor Descriptor { get; }

    Task<SpeechProviderResult> TranscribeAsync(
        SpeechProviderRequest request,
        CancellationToken cancellationToken);
}
