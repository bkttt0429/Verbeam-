using YomiBridge.Core.Models;

namespace YomiBridge.Core.Providers;

public sealed class OcrProviderRegistry
{
    private readonly Dictionary<string, IOcrProvider> _providers;

    public OcrProviderRegistry(IEnumerable<IOcrProvider> providers)
    {
        _providers = providers.ToDictionary(
            provider => provider.Descriptor.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<OcrProviderDescriptor> List()
        => _providers.Values.Select(provider => provider.Descriptor).OrderBy(provider => provider.Name).ToArray();

    public IOcrProvider GetRequired(string name)
    {
        if (_providers.TryGetValue(name, out var provider))
        {
            return provider;
        }

        var known = string.Join(", ", _providers.Keys.OrderBy(key => key));
        throw new InvalidOperationException($"Unknown OCR provider '{name}'. Available OCR providers: {known}.");
    }
}
