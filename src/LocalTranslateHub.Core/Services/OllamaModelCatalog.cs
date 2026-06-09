using System.Net.Http.Json;
using System.Text.Json;
using LocalTranslateHub.Core.Models;
using LocalTranslateHub.Core.Options;

namespace LocalTranslateHub.Core.Services;

public sealed class OllamaModelCatalog
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public OllamaModelCatalog(HttpClient httpClient, OllamaOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<IReadOnlyList<TranslationModelDescriptor>> ListAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        var models = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);

        AddConfiguredModel(models, _options.Model);
        foreach (var model in _options.Models)
        {
            AddConfiguredModel(models, model);
        }

        try
        {
            var tags = await _httpClient.GetFromJsonAsync<OllamaTagsResponse>(
                BuildTagsEndpoint(_options.BaseUrl),
                _jsonOptions,
                cancellationToken);

            foreach (var item in tags?.Models ?? [])
            {
                var name = Pick(item.Name, item.Model);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                models[name] = new ModelEntry(name, IsDefault(name), IsInstalled: true, "ollama");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (JsonException)
        {
        }

        return models.Values
            .OrderByDescending(model => model.IsDefault)
            .ThenByDescending(model => model.IsInstalled)
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .Select(model => new TranslationModelDescriptor(
                provider,
                model.Name,
                model.Name,
                model.IsDefault,
                model.IsInstalled,
                model.Source))
            .ToArray();
    }

    private void AddConfiguredModel(Dictionary<string, ModelEntry> models, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var name = model.Trim();
        models[name] = new ModelEntry(name, IsDefault(name), IsInstalled: false, "configured");
    }

    private bool IsDefault(string model)
        => string.Equals(model, _options.Model, StringComparison.OrdinalIgnoreCase);

    private static string BuildTagsEndpoint(string baseUrl)
        => $"{baseUrl.TrimEnd('/')}/api/tags";

    private static string Pick(string? value, string? fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback?.Trim() ?? string.Empty : value.Trim();

    private sealed record ModelEntry(
        string Name,
        bool IsDefault,
        bool IsInstalled,
        string Source);

    private sealed record OllamaTagsResponse(IReadOnlyList<OllamaTagModel>? Models);

    private sealed record OllamaTagModel(string? Name, string? Model);
}
