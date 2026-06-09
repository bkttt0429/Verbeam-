using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public interface IOcrProvider
{
    OcrProviderDescriptor Descriptor { get; }

    Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken);
}
