using System.Text.Json;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public sealed class TranslationRouteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TranslationRouteStore(string path)
    {
        _path = path;
    }

    public async Task<IReadOnlyList<TranslationRoute>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadDocumentAsync(cancellationToken)).Routes;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TranslationRoute?> GetAsync(
        string profileId = "default",
        CancellationToken cancellationToken = default)
    {
        var profile = string.IsNullOrWhiteSpace(profileId) ? "default" : profileId.Trim();
        var routes = await ListAsync(cancellationToken);
        return routes.FirstOrDefault(item => item.ProfileId.Equals(profile, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<TranslationRoute> SetAsync(
        TranslationRoute route,
        CancellationToken cancellationToken = default)
    {
        Validate(route);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var routes = document.Routes.ToList();
            var index = routes.FindIndex(item => item.ProfileId.Equals(route.ProfileId, StringComparison.OrdinalIgnoreCase));
            var saved = route with { UpdatedAt = DateTimeOffset.UtcNow };
            if (index >= 0)
            {
                routes[index] = saved;
            }
            else
            {
                routes.Add(saved);
            }

            await SaveDocumentAsync(new TranslationRouteStoreDocument { Routes = routes }, cancellationToken);
            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TranslationRouteStoreDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new TranslationRouteStoreDocument();
        }

        await using var stream = File.OpenRead(_path);
        var document = await JsonSerializer.DeserializeAsync<TranslationRouteStoreDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        return document ?? new TranslationRouteStoreDocument();
    }

    private async Task SaveDocumentAsync(
        TranslationRouteStoreDocument document,
        CancellationToken cancellationToken)
    {
        foreach (var route in document.Routes)
        {
            Validate(route);
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        }

        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _path);
        }
    }

    private static void Validate(TranslationRoute route)
    {
        if (string.IsNullOrWhiteSpace(route.ProfileId) ||
            string.IsNullOrWhiteSpace(route.Provider))
        {
            throw new InvalidOperationException("Translation route requires profileId and provider.");
        }

        foreach (var fallback in route.Fallback)
        {
            if (string.IsNullOrWhiteSpace(fallback.Provider) ||
                string.IsNullOrWhiteSpace(fallback.Model))
            {
                throw new InvalidOperationException("Translation route fallback requires provider and model.");
            }
        }
    }

    private sealed record TranslationRouteStoreDocument
    {
        public int SchemaVersion { get; init; } = 1;
        public IReadOnlyList<TranslationRoute> Routes { get; init; } = [];
    }
}
