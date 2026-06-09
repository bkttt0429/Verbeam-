using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public interface ITranslationProvider
{
    ProviderDescriptor Descriptor { get; }

    Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken);
}
