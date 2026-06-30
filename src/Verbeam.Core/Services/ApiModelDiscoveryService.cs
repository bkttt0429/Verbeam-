using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class ApiModelDiscoveryService
{
    private static readonly string[] KnownCompatSuffixes =
    [
        "/api/claudecode",
        "/api/anthropic",
        "/apps/anthropic",
        "/api/coding",
        "/claudecode",
        "/anthropic",
        "/step_plan",
        "/coding",
        "/claude"
    ];

    private readonly HttpClient _httpClient;
    private readonly ApiSupplierOptions _options;
    private readonly ApiSecretStore _secrets;
    private readonly ApiSupplierPresetCatalogService _presets;

    public ApiModelDiscoveryService(
        HttpClient httpClient,
        ApiSupplierOptions options,
        ApiSecretStore secrets,
        ApiSupplierPresetCatalogService presets)
    {
        _httpClient = httpClient;
        _options = options;
        _secrets = secrets;
        _presets = presets;
    }

    public async Task<ApiSupplierModelFetchResult> FetchAndClassifyAsync(
        ApiSupplierProfile supplier,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await FetchModelsAsync(supplier, cancellationToken);
            return new ApiSupplierModelFetchResult(
                supplier.Id,
                "ready",
                models.Count == 0 ? "Model discovery returned no models." : $"Fetched {models.Count} models.",
                models);
        }
        catch (ApiModelDiscoveryException ex)
        {
            return new ApiSupplierModelFetchResult(supplier.Id, ex.Status, ex.Message, []);
        }
    }

    public async Task<IReadOnlyList<ApiSupplierFetchedModel>> FetchModelsAsync(
        ApiSupplierProfile supplier,
        CancellationToken cancellationToken = default)
    {
        var preset = _presets.GetRequiredPreset(supplier.PresetId);
        if (!preset.SupportsModelFetch)
        {
            throw new ApiModelDiscoveryException("unsupported", "This supplier preset does not support model discovery.");
        }

        var apiKey = await _secrets.GetApiKeyAsync(supplier.ApiKeyRef, cancellationToken);
        if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ApiModelDiscoveryException("missing_key", "API key is required before fetching models.");
        }

        var candidates = BuildModelsUrlCandidates(supplier.BaseUrl, isFullUrl: false, Pick(supplier.ModelsUrl, preset.ModelsUrl));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.DiscoveryTimeoutSeconds, 1, 120)));

        ApiModelDiscoveryException? lastEndpointError = null;
        foreach (var url in candidates)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuthHeader(request, preset, apiKey);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ApiModelDiscoveryException("timeout", "Model discovery timed out.");
            }
            catch (HttpRequestException ex)
            {
                throw new ApiModelDiscoveryException("unreachable", $"Model discovery request failed: {ex.Message}");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    ModelsResponse? body;
                    try
                    {
                        body = await response.Content.ReadFromJsonAsync<ModelsResponse>(timeout.Token);
                    }
                    catch (Exception ex) when (ex is NotSupportedException or System.Text.Json.JsonException)
                    {
                        throw new ApiModelDiscoveryException("parse_error", $"Could not parse model discovery response: {ex.Message}");
                    }

                    var now = DateTimeOffset.UtcNow;
                    return (body?.Data ?? [])
                        .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                        .Select(model => new ApiSupplierFetchedModel
                        {
                            Id = model.Id.Trim(),
                            DisplayName = model.Id.Trim(),
                            OwnedBy = model.OwnedBy ?? string.Empty,
                            Source = "fetched",
                            FetchedAt = now
                        })
                        .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new ApiModelDiscoveryException("auth_error", $"Model discovery returned {(int)response.StatusCode}. Check the API key.");
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                {
                    lastEndpointError = new ApiModelDiscoveryException("unsupported", "This supplier endpoint does not expose OpenAI-compatible model discovery.");
                    continue;
                }

                var error = await response.Content.ReadAsStringAsync(timeout.Token);
                throw new ApiModelDiscoveryException("http_error", $"Model discovery returned {(int)response.StatusCode}: {TrimForError(error)}");
            }
        }

        throw lastEndpointError ?? new ApiModelDiscoveryException("unsupported", "No model discovery endpoint candidate was available.");
    }

    public async Task<ApiSupplierTestResult> TestAsync(
        ApiSupplierProfile supplier,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await FetchAndClassifyAsync(supplier, cancellationToken);
        stopwatch.Stop();
        var status = result.Status.Equals("ready", StringComparison.OrdinalIgnoreCase)
            ? "ready"
            : result.Status;
        return new ApiSupplierTestResult(
            supplier.Id,
            status,
            stopwatch.ElapsedMilliseconds,
            result.Message,
            result.Models);
    }

    public static IReadOnlyList<string> BuildModelsUrlCandidates(
        string baseUrl,
        bool isFullUrl,
        string? modelsUrlOverride)
    {
        if (!string.IsNullOrWhiteSpace(modelsUrlOverride))
        {
            return [modelsUrlOverride.Trim()];
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ApiModelDiscoveryException("invalid_endpoint", "Base URL is empty.");
        }

        var candidates = new List<string>();
        if (isFullUrl)
        {
            var versionIndex = trimmed.IndexOf("/v1/", StringComparison.OrdinalIgnoreCase);
            if (versionIndex >= 0)
            {
                candidates.Add($"{trimmed[..versionIndex]}/v1/models");
            }
            else
            {
                var lastSlash = trimmed.LastIndexOf('/');
                var schemeIndex = trimmed.IndexOf("://", StringComparison.Ordinal);
                if (lastSlash > schemeIndex + 2)
                {
                    candidates.Add($"{trimmed[..lastSlash]}/v1/models");
                }
            }

            if (candidates.Count == 0)
            {
                throw new ApiModelDiscoveryException("invalid_endpoint", "Cannot derive a models endpoint from the full URL.");
            }

            return candidates;
        }

        if (EndsWithVersionSegment(trimmed))
        {
            candidates.Add($"{trimmed}/models");
            if (!trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add($"{trimmed}/v1/models");
            }
        }
        else
        {
            candidates.Add($"{trimmed}/v1/models");
        }

        var stripped = StripCompatSuffix(trimmed);
        if (!string.IsNullOrWhiteSpace(stripped) && stripped.Contains("://", StringComparison.Ordinal))
        {
            var root = stripped.TrimEnd('/');
            candidates.Add($"{root}/v1/models");
            candidates.Add($"{root}/models");
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void ApplyAuthHeader(
        HttpRequestMessage request,
        ApiSupplierPreset preset,
        string apiKey)
    {
        if (preset.Protocol.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        var header = string.IsNullOrWhiteSpace(preset.ApiKeyHeader) ? "Authorization" : preset.ApiKeyHeader.Trim();
        var value = string.IsNullOrWhiteSpace(preset.ApiKeyScheme)
            ? apiKey
            : $"{preset.ApiKeyScheme.Trim()} {apiKey}";
        request.Headers.TryAddWithoutValidation(header, value);
    }

    private static bool EndsWithVersionSegment(string url)
    {
        var last = url.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return last.Length > 1 &&
               last[0] is 'v' or 'V' &&
               last[1..].All(char.IsAsciiDigit);
    }

    private static string? StripCompatSuffix(string baseUrl)
    {
        foreach (var suffix in KnownCompatSuffixes)
        {
            if (baseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl[..^suffix.Length];
            }
        }

        return null;
    }

    private static string Pick(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string TrimForError(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 300 ? value : value[..300];
    }

    private sealed record ModelsResponse(
        [property: JsonPropertyName("data")]
        IReadOnlyList<ModelEntry>? Data);

    private sealed record ModelEntry(
        [property: JsonPropertyName("id")]
        string Id,
        [property: JsonPropertyName("owned_by")]
        string? OwnedBy);
}

public sealed class ApiModelDiscoveryException : InvalidOperationException
{
    public ApiModelDiscoveryException(string status, string message)
        : base(message)
    {
        Status = status;
    }

    public string Status { get; }
}
