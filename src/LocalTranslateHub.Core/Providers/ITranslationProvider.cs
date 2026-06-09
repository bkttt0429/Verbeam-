using LocalTranslateHub.Core.Models;

namespace LocalTranslateHub.Core.Providers;

public interface ITranslationProvider
{
    ProviderDescriptor Descriptor { get; }

    Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken);
}
