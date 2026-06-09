using YomiBridge.Core.Models;

namespace YomiBridge.Core.Providers;

public interface ITranslationProvider
{
    ProviderDescriptor Descriptor { get; }

    Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken);
}
