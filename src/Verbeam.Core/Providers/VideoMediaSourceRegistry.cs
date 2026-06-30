namespace Verbeam.Core.Providers;

public sealed class VideoMediaSourceRegistry
{
    private readonly IReadOnlyList<IVideoMediaSourceProvider> _providers;

    public VideoMediaSourceRegistry(IEnumerable<IVideoMediaSourceProvider> providers)
    {
        _providers = providers.ToArray();
    }

    public IVideoMediaSourceProvider GetRequired(string sourceUrl)
    {
        var provider = _providers.FirstOrDefault(item => item.CanHandle(sourceUrl));
        if (provider is null)
        {
            throw new InvalidOperationException("No media source provider can handle this source URL.");
        }

        return provider;
    }

    public IReadOnlyList<string> List()
        => _providers.Select(provider => provider.Name).ToArray();
}
