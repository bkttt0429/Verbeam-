using System.Net.Http.Json;
using System.Text.Json.Serialization;
using YomiBridge.Core.Models;
using YomiBridge.Core.Options;
using YomiBridge.Core.Services;

namespace YomiBridge.Core.Providers;

public sealed class OllamaTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;

    public OllamaTranslationProvider(HttpClient httpClient, OllamaOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public ProviderDescriptor Descriptor => new(
        "ollama",
        "Ollama",
        "local-llm",
        _options.Model,
        RequiresNetwork: false,
        IsLocal: true);

    public async Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken)
    {
        var prompt = PromptRenderer.Render(request);
        var endpoint = BuildEndpoint(_options.BaseUrl);
        var payload = new OllamaChatRequest(
            request.Model,
            false,
            new[]
            {
                new OllamaChatMessage("system", prompt.System),
                new OllamaChatMessage("user", prompt.User)
            },
            new OllamaChatOptions(
                Temperature: _options.Temperature,
                NumContext: _options.NumContext,
                NumPredict: _options.NumPredict),
            _options.KeepAlive);

        using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {TrimForError(body)}");
        }

        var chat = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken);
        var translated = chat?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(translated))
        {
            throw new InvalidOperationException("Ollama returned an empty translation.");
        }

        return new ProviderTranslationResult(translated, $"ollama:{request.Model}");
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(normalized), "api/chat");
    }

    private static string TrimForError(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 300 ? value : value[..300];
    }

    private sealed record OllamaChatRequest(
        [property: JsonPropertyName("model")]
        string Model,
        [property: JsonPropertyName("stream")]
        bool Stream,
        [property: JsonPropertyName("messages")]
        IReadOnlyList<OllamaChatMessage> Messages,
        [property: JsonPropertyName("options")]
        OllamaChatOptions Options,
        [property: JsonPropertyName("keep_alive")]
        string KeepAlive);

    private sealed record OllamaChatMessage(
        [property: JsonPropertyName("role")]
        string Role,
        [property: JsonPropertyName("content")]
        string Content);

    private sealed record OllamaChatOptions(
        [property: JsonPropertyName("temperature")]
        double Temperature,
        [property: JsonPropertyName("num_ctx")]
        int NumContext,
        [property: JsonPropertyName("num_predict")]
        int NumPredict);

    private sealed record OllamaChatResponse([property: JsonPropertyName("message")] OllamaChatMessageResponse? Message);

    private sealed record OllamaChatMessageResponse([property: JsonPropertyName("content")] string? Content);
}
