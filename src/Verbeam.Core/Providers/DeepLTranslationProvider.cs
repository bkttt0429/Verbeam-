using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Core.Providers;

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly DeepLOptions _options;

    public DeepLTranslationProvider(HttpClient httpClient, DeepLOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public ProviderDescriptor Descriptor => new(
        "deepl",
        "DeepL",
        "remote-mt",
        Pick(_options.ModelType, "default"),
        RequiresNetwork: true,
        IsLocal: false);

    public async Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken)
    {
        var apiKey = _options.ApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "DeepL API key is not configured. Set Verbeam:DeepL:ApiKey or VB_Verbeam__DeepL__ApiKey.");
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return new ProviderTranslationResult(string.Empty, "deepl:empty")
            {
                TokenUsage = TokenUsage.Zero("deepl:empty")
            };
        }

        var target = FormatDeepLLanguageCode(request.Target, isTarget: true);
        if (string.IsNullOrWhiteSpace(target) ||
            target.Equals(LanguageRegistry.Auto, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DeepL requires an explicit target language.");
        }

        var source = FormatDeepLLanguageCode(request.Source, isTarget: false);
        var modelType = Pick(request.Model, Pick(_options.ModelType, "default"));
        var payload = new DeepLTranslateRequest(
            [request.Text],
            target,
            string.IsNullOrWhiteSpace(source) ? null : source,
            ShouldSendModelType(modelType) ? modelType : null);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildTranslateEndpoint(apiKey))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {apiKey}");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DeepL returned {(int)response.StatusCode}: {TrimForError(body)}");
        }

        var result = JsonSerializer.Deserialize<DeepLTranslateResponse>(body, JsonOptions);
        var translated = result?.Translations.FirstOrDefault()?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(translated))
        {
            throw new InvalidOperationException("DeepL returned an empty translation.");
        }

        return new ProviderTranslationResult(translated, $"deepl:{modelType}")
        {
            TokenUsage = TokenUsageEstimator.EstimateTextPair(request.Text, translated, "deepl:estimated")
        };
    }

    private Uri BuildTranslateEndpoint(string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            var baseUrl = _options.BaseUrl.Trim().TrimEnd('/');
            if (baseUrl.EndsWith("/v2/translate", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(baseUrl);
            }

            return new Uri($"{baseUrl}/v2/translate");
        }

        var root = _options.UseFreeApi || apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com"
            : "https://api.deepl.com";
        return new Uri($"{root}/v2/translate");
    }

    private static string FormatDeepLLanguageCode(string? language, bool isTarget)
    {
        if (LanguageRegistry.IsAuto(language))
        {
            return string.Empty;
        }

        var code = LanguageRegistry.ToTranslationCode(language)
            .Trim()
            .Replace('_', '-')
            .ToUpperInvariant();
        return code switch
        {
            "ZH" or "ZH-CN" or "ZH-HANS" or "ZH-HANS-CN" => isTarget ? "ZH-HANS" : "ZH",
            "ZH-TW" or "ZH-HANT" or "ZH-HANT-TW" or "ZH-HK" => isTarget ? "ZH-HANT" : "ZH",
            "EN" or "EN-US" => isTarget ? "EN-US" : "EN",
            "EN-GB" => isTarget ? "EN-GB" : "EN",
            "PT" or "PT-PT" => isTarget ? "PT-PT" : "PT",
            "PT-BR" => isTarget ? "PT-BR" : "PT",
            _ => code
        };
    }

    private static bool ShouldSendModelType(string modelType)
        => !string.IsNullOrWhiteSpace(modelType) &&
           !modelType.Equals("default", StringComparison.OrdinalIgnoreCase);

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string TrimForError(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 300 ? value : value[..300];
    }

    private sealed record DeepLTranslateRequest(
        [property: JsonPropertyName("text")]
        IReadOnlyList<string> Text,
        [property: JsonPropertyName("target_lang")]
        string TargetLang,
        [property: JsonPropertyName("source_lang")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SourceLang,
        [property: JsonPropertyName("model_type")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ModelType);

    private sealed record DeepLTranslateResponse(
        [property: JsonPropertyName("translations")]
        IReadOnlyList<DeepLTranslation> Translations);

    private sealed record DeepLTranslation(
        [property: JsonPropertyName("detected_source_language")]
        string? DetectedSourceLanguage,
        [property: JsonPropertyName("text")]
        string? Text);
}
