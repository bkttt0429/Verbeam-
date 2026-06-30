using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Core.Providers;

public sealed class ApiCompatibleTranslationProvider : ITranslationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly ApiSupplierOptions _options;
    private readonly ApiSupplierStore _suppliers;
    private readonly ApiSecretStore _secrets;
    private readonly ApiSupplierPresetCatalogService _presets;
    private readonly TranslationRouteStore _routes;

    public ApiCompatibleTranslationProvider(
        HttpClient httpClient,
        ApiSupplierOptions options,
        ApiSupplierStore suppliers,
        ApiSecretStore secrets,
        ApiSupplierPresetCatalogService presets,
        TranslationRouteStore routes)
    {
        _httpClient = httpClient;
        _options = options;
        _suppliers = suppliers;
        _secrets = secrets;
        _presets = presets;
        _routes = routes;
    }

    public ProviderDescriptor Descriptor => new(
        "api-compatible",
        "API Provider",
        "remote-llm",
        string.Empty,
        RequiresNetwork: true,
        IsLocal: false);

    public async Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken)
    {
        var providerTimings = new Dictionary<string, double>();
        var endpoint = await ResolveEndpointAsync(request.Model, providerTimings, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(endpoint.TimeoutSeconds, 1, 600)));

        var rendered = Measure(providerTimings, "provider.prompt_render_ms", () => PromptRenderer.RenderForPrefixCache(request));
        AddTiming(providerTimings, "provider.stable_prefix_chars", rendered.StablePrefix.Length);
        AddTiming(providerTimings, "provider.suffix_chars", rendered.Suffix.Length);
        var translated = endpoint.Protocol.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
            ? await TranslateWithAnthropicAsync(endpoint, rendered, providerTimings, timeout.Token)
            : await TranslateWithOpenAiChatAsync(endpoint, rendered, providerTimings, timeout.Token);

        return new ProviderTranslationResult(
            translated.Text,
            $"api-compatible:{endpoint.SupplierId}:{endpoint.Model}")
        {
            Timings = providerTimings,
            TokenUsage = translated.TokenUsage
                ?? TokenUsageEstimator.EstimateProviderRequest(request, translated.Text, $"api-compatible:{endpoint.Protocol}:estimated")
        };
    }

    private async Task<ApiTranslationResult> TranslateWithOpenAiChatAsync(
        ApiCompatibleEndpoint endpoint,
        RenderedCachedPrompt rendered,
        IDictionary<string, double> providerTimings,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(endpoint.BaseUrl, "chat/completions"));
        ApiModelDiscoveryService.ApplyAuthHeader(
            httpRequest,
            new ApiSupplierPreset
            {
                Id = "runtime",
                DisplayName = "runtime",
                Protocol = endpoint.Protocol,
                BaseUrl = endpoint.BaseUrl.ToString(),
                ApiKeyHeader = endpoint.ApiKeyHeader,
                ApiKeyScheme = endpoint.ApiKeyScheme
            },
            endpoint.ApiKey);
        httpRequest.Content = JsonContent.Create(new ApiChatRequest(
            endpoint.Model,
            Stream: false,
            [
                new ApiChatMessage("system", rendered.StablePrefix),
                new ApiChatMessage("user", rendered.Suffix)
            ],
            endpoint.Temperature,
            endpoint.MaxTokens));

        var httpStopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        httpStopwatch.Stop();
        AddTiming(providerTimings, "provider.http_ms", httpStopwatch.Elapsed.TotalMilliseconds);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"API supplier returned {(int)response.StatusCode}: {TrimForError(body)}");
        }

        var chat = JsonSerializer.Deserialize<ApiChatResponse>(body, JsonOptions);
        var translated = chat?.Choices.FirstOrDefault()?.Message.Content?.Trim();
        if (string.IsNullOrWhiteSpace(translated))
        {
            throw new InvalidOperationException("API supplier returned an empty translation.");
        }

        return new ApiTranslationResult(translated, chat?.Usage?.ToTokenUsage("api-compatible:openai:exact"));
    }

    private async Task<ApiTranslationResult> TranslateWithAnthropicAsync(
        ApiCompatibleEndpoint endpoint,
        RenderedCachedPrompt rendered,
        IDictionary<string, double> providerTimings,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(endpoint.BaseUrl, "v1/messages"));
        ApiModelDiscoveryService.ApplyAuthHeader(
            httpRequest,
            new ApiSupplierPreset
            {
                Id = "runtime",
                DisplayName = "runtime",
                Protocol = endpoint.Protocol,
                BaseUrl = endpoint.BaseUrl.ToString(),
                ApiKeyHeader = endpoint.ApiKeyHeader,
                ApiKeyScheme = endpoint.ApiKeyScheme
            },
            endpoint.ApiKey);
        httpRequest.Content = JsonContent.Create(new AnthropicMessagesRequest(
            endpoint.Model,
            endpoint.MaxTokens,
            rendered.StablePrefix,
            [new AnthropicMessage("user", rendered.Suffix)],
            endpoint.Temperature));

        var httpStopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        httpStopwatch.Stop();
        AddTiming(providerTimings, "provider.http_ms", httpStopwatch.Elapsed.TotalMilliseconds);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"API supplier returned {(int)response.StatusCode}: {TrimForError(body)}");
        }

        var message = JsonSerializer.Deserialize<AnthropicMessagesResponse>(body, JsonOptions);
        var translated = (message?.Content ?? Array.Empty<AnthropicContentBlock>())
            .Where(part => part.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
            .Select(part => part.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?.Trim();
        if (string.IsNullOrWhiteSpace(translated))
        {
            throw new InvalidOperationException("API supplier returned an empty translation.");
        }

        return new ApiTranslationResult(translated, message?.Usage?.ToTokenUsage("api-compatible:anthropic:exact"));
    }

    private async Task<ApiCompatibleEndpoint> ResolveEndpointAsync(
        string requestedModel,
        IDictionary<string, double> providerTimings,
        CancellationToken cancellationToken)
    {
        var route = await MeasureAsync(providerTimings, "api.route_lookup_ms", () => _routes.GetAsync("default", cancellationToken))
            ?? throw new InvalidOperationException("No active translation route is configured for api-compatible.");
        if (!route.Provider.Equals("api-compatible", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The active translation route is not configured for api-compatible.");
        }

        var supplier = await MeasureAsync(providerTimings, "api.supplier_lookup_ms", () => _suppliers.GetAsync(route.SupplierId, cancellationToken))
            ?? throw new InvalidOperationException($"Active API supplier was not found: {route.SupplierId}");
        var preset = Measure(providerTimings, "api.preset_lookup_ms", () => _presets.GetRequiredPreset(supplier.PresetId));
        var apiKey = await MeasureAsync(providerTimings, "api.secret_lookup_ms", () => _secrets.GetApiKeyAsync(supplier.ApiKeyRef, cancellationToken));
        if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"API supplier '{supplier.Name}' is missing an API key.");
        }

        var model = Pick(requestedModel, Pick(route.Model, supplier.ActiveModel));
        if (string.IsNullOrWhiteSpace(model))
        {
            model = preset.DefaultModel;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException($"API supplier '{supplier.Name}' has no active model.");
        }

        return new ApiCompatibleEndpoint(
            BuildProtocolBaseUrl(supplier.BaseUrl, supplier.Protocol),
            supplier.Id,
            model,
            apiKey,
            preset.ApiKeyHeader,
            preset.ApiKeyScheme,
            Math.Max(1, _options.MaxTokens),
            Math.Max(0, _options.Temperature),
            Math.Clamp(_options.RequestTimeoutSeconds, 1, 600),
            supplier.Protocol);
    }

    private static Uri BuildProtocolBaseUrl(string baseUrl, string protocol)
    {
        return protocol.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
            ? BuildBaseUrl(baseUrl)
            : BuildOpenAiBaseUrl(baseUrl);
    }

    private static Uri BuildBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return new Uri(trimmed + "/");
    }

    private static Uri BuildOpenAiBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        var lastSegment = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        if (!lastSegment.Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            !lastSegment.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/v1";
        }

        return new Uri(trimmed + "/");
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string TrimForError(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 300 ? value : value[..300];
    }

    private static T Measure<T>(IDictionary<string, double> timings, string name, Func<T> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            stopwatch.Stop();
            AddTiming(timings, name, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task<T> MeasureAsync<T>(IDictionary<string, double> timings, string name, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action();
        }
        finally
        {
            stopwatch.Stop();
            AddTiming(timings, name, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static void AddTiming(IDictionary<string, double> timings, string name, double value)
    {
        if (double.IsFinite(value))
        {
            timings[name] = value;
        }
    }

    private sealed record ApiChatRequest(
        [property: JsonPropertyName("model")]
        string Model,
        [property: JsonPropertyName("stream")]
        bool Stream,
        [property: JsonPropertyName("messages")]
        IReadOnlyList<ApiChatMessage> Messages,
        [property: JsonPropertyName("temperature")]
        double Temperature,
        [property: JsonPropertyName("max_tokens")]
        int MaxTokens);

    private sealed record ApiChatMessage(
        [property: JsonPropertyName("role")]
        string Role,
        [property: JsonPropertyName("content")]
        string Content);

    private sealed record ApiTranslationResult(string Text, TokenUsage? TokenUsage);

    private sealed record ApiChatResponse(
        [property: JsonPropertyName("choices")]
        IReadOnlyList<ApiChoice> Choices,
        [property: JsonPropertyName("usage")]
        ApiUsage? Usage = null);

    private sealed record ApiChoice(
        [property: JsonPropertyName("message")]
        ApiChatMessage Message);

    private sealed record ApiUsage(
        [property: JsonPropertyName("prompt_tokens")]
        long PromptTokens,
        [property: JsonPropertyName("completion_tokens")]
        long CompletionTokens,
        [property: JsonPropertyName("total_tokens")]
        long TotalTokens)
    {
        public TokenUsage ToTokenUsage(string source)
        {
            var input = Math.Max(0, PromptTokens);
            var output = Math.Max(0, CompletionTokens);
            var total = TotalTokens > 0 ? TotalTokens : input + output;
            return new TokenUsage(input, output, total, source, IsEstimated: false);
        }
    }

    private sealed record AnthropicMessagesRequest(
        [property: JsonPropertyName("model")]
        string Model,
        [property: JsonPropertyName("max_tokens")]
        int MaxTokens,
        [property: JsonPropertyName("system")]
        string System,
        [property: JsonPropertyName("messages")]
        IReadOnlyList<AnthropicMessage> Messages,
        [property: JsonPropertyName("temperature")]
        double Temperature);

    private sealed record AnthropicMessage(
        [property: JsonPropertyName("role")]
        string Role,
        [property: JsonPropertyName("content")]
        string Content);

    private sealed record AnthropicMessagesResponse(
        [property: JsonPropertyName("content")]
        IReadOnlyList<AnthropicContentBlock> Content,
        [property: JsonPropertyName("usage")]
        AnthropicUsage? Usage = null);

    private sealed record AnthropicContentBlock(
        [property: JsonPropertyName("type")]
        string Type,
        [property: JsonPropertyName("text")]
        string Text);

    private sealed record AnthropicUsage(
        [property: JsonPropertyName("input_tokens")]
        long InputTokens,
        [property: JsonPropertyName("output_tokens")]
        long OutputTokens)
    {
        public TokenUsage ToTokenUsage(string source)
        {
            var input = Math.Max(0, InputTokens);
            var output = Math.Max(0, OutputTokens);
            return new TokenUsage(input, output, input + output, source, IsEstimated: false);
        }
    }
}
