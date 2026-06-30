using System.Text.Json;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public sealed class ApiSupplierPresetCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly string[] SupportedProtocols = ["openai_chat", "anthropic"];
    private readonly string _path;

    public ApiSupplierPresetCatalogStore(string path)
    {
        _path = path;
    }

    public async Task<ApiSupplierPresetCatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            throw new FileNotFoundException($"API supplier preset catalog was not found: {_path}", _path);
        }

        await using var stream = File.OpenRead(_path);
        return await LoadAsync(stream, _path, cancellationToken);
    }

    public async Task SaveAsync(
        ApiSupplierPresetCatalogDocument catalog,
        CancellationToken cancellationToken = default)
    {
        Validate(catalog);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken);
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

    public static async Task<ApiSupplierPresetCatalogDocument> LoadAsync(
        Stream stream,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        var catalog = await JsonSerializer.DeserializeAsync<ApiSupplierPresetCatalogDocument>(
            stream,
            JsonOptions,
            cancellationToken);

        if (catalog is null)
        {
            throw new InvalidOperationException($"API supplier preset catalog is empty or invalid: {sourceName}");
        }

        Validate(catalog);
        return catalog;
    }

    public static void Validate(ApiSupplierPresetCatalogDocument catalog)
    {
        if (catalog.SchemaVersion <= 0)
        {
            throw new InvalidOperationException("API supplier preset catalog schemaVersion must be positive.");
        }

        if (string.IsNullOrWhiteSpace(catalog.CatalogVersion))
        {
            throw new InvalidOperationException("API supplier preset catalog catalogVersion is required.");
        }

        if (catalog.Presets.Count == 0)
        {
            throw new InvalidOperationException("API supplier preset catalog must contain at least one preset.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in catalog.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id) ||
                string.IsNullOrWhiteSpace(preset.DisplayName) ||
                string.IsNullOrWhiteSpace(preset.Protocol) ||
                string.IsNullOrWhiteSpace(preset.BaseUrl))
            {
                throw new InvalidOperationException("Each API supplier preset requires id, displayName, protocol, and baseUrl.");
            }

            if (!ids.Add(preset.Id))
            {
                throw new InvalidOperationException($"Duplicate API supplier preset id: {preset.Id}");
            }

            if (!SupportedProtocols.Contains(preset.Protocol, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported API supplier protocol: {preset.Id}/{preset.Protocol}");
            }

            if (!Uri.TryCreate(preset.BaseUrl, UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException($"API supplier preset baseUrl must be an absolute HTTP URL: {preset.Id}");
            }

            if (!string.IsNullOrWhiteSpace(preset.ModelsUrl) &&
                (!Uri.TryCreate(preset.ModelsUrl, UriKind.Absolute, out var modelsUri) ||
                 modelsUri.Scheme is not ("http" or "https")))
            {
                throw new InvalidOperationException($"API supplier preset modelsUrl must be an absolute HTTP URL: {preset.Id}");
            }

            foreach (var model in preset.RecommendedModels)
            {
                if (string.IsNullOrWhiteSpace(model.Id))
                {
                    throw new InvalidOperationException($"API supplier recommended model id is required: {preset.Id}");
                }

                if (model.QualityScore is < 0 or > 1 ||
                    model.LatencyScore is < 0 or > 1 ||
                    model.ContextScore is < 0 or > 1 ||
                    model.StabilityScore is < 0 or > 1)
                {
                    throw new InvalidOperationException($"API supplier recommended model has invalid scores: {preset.Id}/{model.Id}");
                }
            }
        }
    }
}
