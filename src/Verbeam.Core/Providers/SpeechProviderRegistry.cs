using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public sealed class SpeechProviderRegistry
{
    private readonly Dictionary<string, ISpeechProvider> _providers;

    public SpeechProviderRegistry(IEnumerable<ISpeechProvider> providers)
    {
        _providers = providers.ToDictionary(
            provider => provider.Descriptor.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<SpeechProviderDescriptor> List()
        => _providers.Values.Select(provider => provider.Descriptor).OrderBy(provider => provider.Name).ToArray();

    public ISpeechProvider GetRequired(string name)
    {
        if (_providers.TryGetValue(name, out var provider))
        {
            return provider;
        }

        var known = string.Join(", ", _providers.Keys.OrderBy(key => key));
        throw new InvalidOperationException($"Unknown ASR provider '{name}'. Available ASR providers: {known}.");
    }
}
