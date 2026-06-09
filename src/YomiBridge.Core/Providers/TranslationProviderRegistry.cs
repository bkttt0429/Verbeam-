using YomiBridge.Core.Models;

namespace YomiBridge.Core.Providers;

public sealed class TranslationProviderRegistry
{
    private readonly Dictionary<string, ITranslationProvider> _providers;

    public TranslationProviderRegistry(IEnumerable<ITranslationProvider> providers)
    {
        _providers = providers.ToDictionary(
            provider => provider.Descriptor.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ProviderDescriptor> List()
        => _providers.Values.Select(provider => provider.Descriptor).OrderBy(provider => provider.Name).ToArray();

    public ITranslationProvider GetRequired(string name)
    {
        if (_providers.TryGetValue(name, out var provider))
        {
            return provider;
        }

        var known = string.Join(", ", _providers.Keys.OrderBy(key => key));
        throw new InvalidOperationException($"Unknown provider '{name}'. Available providers: {known}.");
    }
}
