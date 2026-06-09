using YomiBridge.Core.Models;

namespace YomiBridge.Core.Providers;

public interface IOcrProvider
{
    OcrProviderDescriptor Descriptor { get; }

    Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken);
}
