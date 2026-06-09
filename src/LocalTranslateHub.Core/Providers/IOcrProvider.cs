using LocalTranslateHub.Core.Models;

namespace LocalTranslateHub.Core.Providers;

public interface IOcrProvider
{
    OcrProviderDescriptor Descriptor { get; }

    Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken);
}
