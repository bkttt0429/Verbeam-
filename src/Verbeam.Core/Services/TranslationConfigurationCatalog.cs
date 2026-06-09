using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class TranslationConfigurationCatalog
{
    private static readonly IReadOnlyList<LanguageDefinition> LanguageDefinitions =
    [
        new(
            "ja",
            "Japanese",
            "日本語",
            "Japanese",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["jp", "ja-jp"]),
        new(
            "en",
            "English",
            "English",
            "English",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["en-us", "en-gb"]),
        new(
            "zh-TW",
            "Traditional Chinese (Taiwan)",
            "繁體中文（台灣）",
            "Traditional Chinese (Taiwan) / 繁體中文（台灣）, using Traditional Chinese characters only and never Simplified Chinese",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["zh-hant", "traditional chinese", "traditional chinese taiwan"]),
        new(
            "zh-CN",
            "Simplified Chinese (China)",
            "简体中文（中国）",
            "Simplified Chinese (China), using Simplified Chinese characters",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["zh-hans", "simplified chinese"]),
        new(
            "zh",
            "Chinese",
            "中文",
            "Chinese",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            []),
        new(
            "ko",
            "Korean",
            "한국어",
            "Korean",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["ko-kr"])
    ];

    private readonly VerbeamOptions _options;

    public TranslationConfigurationCatalog(VerbeamOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<TranslationLanguageDescriptor> ListLanguages()
    {
        return LanguageDefinitions
            .Select(language => new TranslationLanguageDescriptor(
                language.Code,
                language.DisplayName,
                language.NativeName,
                language.PromptName,
                IsLanguageMatch(language.Code, _options.DefaultSource),
                IsLanguageMatch(language.Code, _options.DefaultTarget),
                language.IsOcrSupported,
                language.IsSpeechSupported))
            .ToArray();
    }

    public IReadOnlyList<TranslationModelDescriptor> EnrichModels(
        string provider,
        IReadOnlyList<TranslationModelDescriptor> models)
    {
        if (!provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return models
                .Select(model => model with
                {
                    IsRecommended = model.IsDefault,
                    RecommendationReason = model.IsDefault
                        ? "Provider default for this runtime."
                        : model.RecommendationReason,
                    RecommendedUse = model.IsDefault ? "provider default" : model.RecommendedUse
                })
                .ToArray();
        }

        var recommendations = BuildOllamaRecommendations()
            .ToDictionary(recommendation => recommendation.Name, StringComparer.OrdinalIgnoreCase);
        var values = models.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var recommendation in recommendations.Values)
        {
            if (values.TryGetValue(recommendation.Name, out var existing))
            {
                values[recommendation.Name] = existing with
                {
                    DisplayName = string.IsNullOrWhiteSpace(existing.DisplayName)
                        ? recommendation.DisplayName
                        : existing.DisplayName,
                    IsRecommended = true,
                    RecommendationReason = recommendation.Reason,
                    RecommendedUse = recommendation.UseCase
                };
                continue;
            }

            values[recommendation.Name] = new TranslationModelDescriptor(
                provider,
                recommendation.Name,
                recommendation.DisplayName,
                IsDefault: IsConfiguredDefault(recommendation.Name),
                IsInstalled: false,
                "recommended",
                IsRecommended: true,
                recommendation.Reason,
                recommendation.UseCase);
        }

        return values.Values
            .OrderByDescending(model => model.IsDefault)
            .ThenByDescending(model => model.IsRecommended)
            .ThenByDescending(model => model.IsInstalled)
            .ThenBy(model => RecommendationRank(model.Name, recommendations))
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string FormatLanguageForPrompt(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return language;
        }

        var normalized = NormalizeCode(language);
        var definition = LanguageDefinitions.FirstOrDefault(item =>
            item.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase));

        return definition?.PromptName ?? language.Trim();
    }

    private IReadOnlyList<ModelRecommendation> BuildOllamaRecommendations()
    {
        var recommendations = new List<ModelRecommendation>();

        if (!string.IsNullOrWhiteSpace(_options.Ollama.Model))
        {
            recommendations.Add(new ModelRecommendation(
                _options.Ollama.Model.Trim(),
                _options.Ollama.Model.Trim(),
                "current default",
                "Matches the configured Verbeam default model.",
                Rank: 0));
        }

        recommendations.AddRange(
        [
            new ModelRecommendation(
                "verbeam-mort-qwen2.5-0.5b:latest",
                "Verbeam MORT Qwen2.5 0.5B",
                "realtime OCR overlay",
                "Small local profile tuned for short MORT and OCR snippets with deterministic output.",
                Rank: 10),
            new ModelRecommendation(
                "qwen2.5:0.5b",
                "Qwen2.5 0.5B",
                "smoke test",
                "Fast base model for checking that Ollama routing works before using a larger model.",
                Rank: 20),
            new ModelRecommendation(
                "qwen2.5:1.5b",
                "Qwen2.5 1.5B",
                "balanced local translation",
                "A practical next step when 0.5B is too rough but latency still matters.",
                Rank: 30),
            new ModelRecommendation(
                "translategemma:latest",
                "TranslateGemma",
                "higher quality translation",
                "Translation-focused local model option when quality matters more than overlay latency.",
                Rank: 40)
        ]);

        return recommendations
            .GroupBy(recommendation => recommendation.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Rank).First())
            .OrderBy(recommendation => recommendation.Rank)
            .ToArray();
    }

    private bool IsConfiguredDefault(string model)
        => string.Equals(model, _options.Ollama.Model, StringComparison.OrdinalIgnoreCase);

    private static int RecommendationRank(
        string model,
        IReadOnlyDictionary<string, ModelRecommendation> recommendations)
        => recommendations.TryGetValue(model, out var recommendation) ? recommendation.Rank : int.MaxValue;

    private static bool IsLanguageMatch(string code, string configured)
        => string.Equals(NormalizeCode(code), NormalizeCode(configured), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCode(string language)
    {
        var value = language.Trim().ToLowerInvariant();
        foreach (var definition in LanguageDefinitions)
        {
            if (definition.Code.Equals(value, StringComparison.OrdinalIgnoreCase) ||
                definition.Aliases.Any(alias => alias.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                return definition.Code;
            }
        }

        return language.Trim();
    }

    private sealed record LanguageDefinition(
        string Code,
        string DisplayName,
        string NativeName,
        string PromptName,
        bool IsOcrSupported,
        bool IsSpeechSupported,
        IReadOnlyList<string> Aliases);

    private sealed record ModelRecommendation(
        string Name,
        string DisplayName,
        string UseCase,
        string Reason,
        int Rank);
}
