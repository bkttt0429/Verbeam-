using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;

namespace Verbeam.Core.Services;

public sealed class OcrRoutingService
{
    public const string AutoProvider = "auto";

    private static readonly OcrRoutingProfile[] Profiles =
    [
        new(
            "realtime-dialogue",
            "Realtime Dialogue / Prose",
            "dialogue",
            "realtime",
            "dotnet-onnx-or-windows-native",
            "rapidocr-net",
            "oneocr",
            ExpectedLatencyMs: 30,
            PreferAsyncJob: false,
            PreservesStructure: false,
            "Fast path for game dialogue, UI labels, and prose. Favors RapidOcrNet (.NET ONNX, runs on CPU, ~16-30ms incremental, reads CJK + Latin) with OneOCR as the fallback; non-CJK/Latin scripts (Korean/Cyrillic/Thai/…) route to OneOCR for coverage — see PickRealtimeProvider."),
        new(
            "cjk-screenshot-text",
            "CJK Screenshot Text",
            "screenshot_text",
            "fast",
            "local-text",
            "paddleocr",
            "external",
            ExpectedLatencyMs: 450,
            PreferAsyncJob: false,
            PreservesStructure: false,
            "Local text OCR path for dense CJK screenshots where text accuracy matters more than minimum latency."),
        new(
            "structure-document",
            "Structure / Document Region",
            "document",
            "structure",
            "local-structure",
            "pp-structure-v3",
            "pix2text",
            ExpectedLatencyMs: 1800,
            PreferAsyncJob: true,
            PreservesStructure: true,
            "Structure OCR path for formulas, tables, and document regions. Formulas bypass translation; table cells translate in place."),
        new(
            "high-accuracy-structure",
            "High Accuracy Structure OCR",
            "high_accuracy",
            "slow-vlm",
            "local-vlm",
            "paddleocr-vl",
            "pp-structure-v3",
            ExpectedLatencyMs: 7000,
            PreferAsyncJob: true,
            PreservesStructure: true,
            "Manual high-accuracy structure OCR path for complex tables, formulas, charts, and layout-heavy captures."),
        new(
            "explicit-dots-ocr",
            "Explicit dots.ocr VLM",
            "document_vlm",
            "slow-vlm",
            "local-vlm",
            "dots-ocr",
            "paddleocr-vl",
            ExpectedLatencyMs: 9000,
            PreferAsyncJob: true,
            PreservesStructure: true,
            "Listed for explicit user selection only; auto routing avoids dots.ocr unless the user requests it.")
    ];

    private readonly VerbeamOptions _options;
    private readonly OcrProviderRegistry _providers;

    public OcrRoutingService(
        VerbeamOptions options,
        OcrProviderRegistry providers)
    {
        _options = options;
        _providers = providers;
    }

    public IReadOnlyList<OcrRoutingProfile> ListProfiles()
        => Profiles;

    public OcrRoutingDecision ResolveDecision(
        string? requestedProvider,
        string? contentType,
        string? preference,
        IReadOnlyList<OcrSmokeQualitySummary>? qualitySummaries = null,
        string? language = null)
    {
        var provider = Pick(requestedProvider, _options.Ocr.DefaultProvider);
        var normalizedContentType = NormalizeContentType(contentType);
        var normalizedPreference = NormalizePreference(preference);

        if (!provider.Equals(AutoProvider, StringComparison.OrdinalIgnoreCase))
        {
            var explicitProfile = ProfileForProvider(provider, normalizedContentType, normalizedPreference);
            var explicitQuality = PickQualitySummary(provider, explicitProfile, normalizedContentType, qualitySummaries);
            return new OcrRoutingDecision(
                provider,
                explicitProfile.Name,
                normalizedContentType,
                normalizedPreference,
                explicitProfile.SpeedClass,
                explicitProfile.RuntimeKind,
                explicitProfile.ExpectedLatencyMs,
                explicitProfile.PreferAsyncJob,
                explicitProfile.PreservesStructure,
                explicitQuality is null
                    ? $"Explicit OCR provider '{provider}' was requested."
                    : $"Explicit OCR provider '{provider}' was requested; recent smoke quality is {explicitQuality.Status}.",
                explicitQuality?.Status ?? "unknown",
                explicitQuality?.Note ?? string.Empty)
            {
                QualityIssues = explicitQuality?.Issues ?? Array.Empty<OcrSmokeQualityIssue>()
            };
        }

        var profile = SelectAutoProfile(normalizedContentType, normalizedPreference);
        // Realtime dialogue picks the OCR engine by LANGUAGE (LunaTranslator-style) with a
        // sequential fallback (RSTGameTranslation-style); other profiles keep the static pick.
        var selectedProvider = profile.Name == "realtime-dialogue"
            ? PickRealtimeProvider(language)
            : SelectRegisteredProvider(profile.RecommendedProvider, profile.FallbackProvider);
        var qualitySelection = SelectQualityAwareProvider(
            profile,
            normalizedContentType,
            selectedProvider,
            profile.FallbackProvider,
            qualitySummaries);
        return new OcrRoutingDecision(
            qualitySelection.Provider,
            profile.Name,
            normalizedContentType,
            normalizedPreference,
            profile.SpeedClass,
            profile.RuntimeKind,
            profile.ExpectedLatencyMs,
            profile.PreferAsyncJob,
            profile.PreservesStructure,
            qualitySelection.Reason ??
            $"Auto route selected '{qualitySelection.Provider}' for {normalizedContentType}/{normalizedPreference}.",
            qualitySelection.Quality?.Status ?? "unknown",
            qualitySelection.Quality?.Note ?? string.Empty)
        {
            QualityIssues = qualitySelection.Quality?.Issues ?? Array.Empty<OcrSmokeQualityIssue>()
        };
    }

    public string ResolveProviderName(
        string? requestedProvider,
        string? contentType,
        string? preference,
        string? language = null)
        => ResolveDecision(requestedProvider, contentType, preference, qualitySummaries: null, language: language).Provider;

    public IReadOnlyList<string> ListSmokeMatrixProviders(
        string? contentType,
        string? preference)
    {
        var normalizedContentType = NormalizeContentType(contentType);
        var normalizedPreference = NormalizePreference(preference);
        var profile = SelectAutoProfile(normalizedContentType, normalizedPreference);
        string[] candidates = profile.Name switch
        {
            "high-accuracy-structure" =>
            [
                profile.RecommendedProvider,
                profile.FallbackProvider,
                "pix2text",
                "dots-ocr",
                "paddleocr",
                "external"
            ],
            "structure-document" =>
            [
                profile.RecommendedProvider,
                profile.FallbackProvider,
                "paddleocr",
                "external"
            ],
            "cjk-screenshot-text" =>
            [
                profile.RecommendedProvider,
                "rapidocr-ppocrv5",
                profile.FallbackProvider
            ],
            "realtime-dialogue" =>
            [
                profile.RecommendedProvider,
                profile.FallbackProvider,
                AppleVisionOcrProvider.ProviderName,
                "windows",
                "rapidocr-ppocrv5",
                "external",
                "paddleocr"
            ],
            _ =>
            [
                profile.RecommendedProvider,
                profile.FallbackProvider,
                "paddleocr"
            ]
        };

        return candidates
            .Where(provider => !string.IsNullOrWhiteSpace(provider) && _providers.Contains(provider))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private OcrRoutingProfile SelectAutoProfile(
        string contentType,
        string preference)
    {
        if (preference is "accuracy" or "high_accuracy" or "vlm" ||
            contentType is "high_accuracy" or "complex_document")
        {
            return Profiles.First(profile => profile.Name == "high-accuracy-structure");
        }

        if (contentType is "formula" or "table" or "structure" or "document" or "document_region" or "formula_table")
        {
            return Profiles.First(profile => profile.Name == "structure-document");
        }

        if (contentType is "screenshot" or "screenshot_text" or "cjk_screenshot" ||
            preference is "balanced" or "cjk")
        {
            return Profiles.First(profile => profile.Name == "cjk-screenshot-text");
        }

        return Profiles.First(profile => profile.Name == "realtime-dialogue");
    }

    private OcrRoutingProfile ProfileForProvider(
        string provider,
        string contentType,
        string preference)
    {
        if (provider.Equals("dots-ocr", StringComparison.OrdinalIgnoreCase))
        {
            return Profiles.First(profile => profile.Name == "explicit-dots-ocr");
        }

        if (provider.Equals("paddleocr-vl", StringComparison.OrdinalIgnoreCase))
        {
            return Profiles.First(profile => profile.Name == "high-accuracy-structure");
        }

        if (provider.Equals("pp-structure-v3", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("pix2text", StringComparison.OrdinalIgnoreCase))
        {
            return Profiles.First(profile => profile.Name == "structure-document");
        }

        if (provider.Equals("oneocr", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("windows", StringComparison.OrdinalIgnoreCase) ||
            provider.StartsWith("rapidocr-net", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("rapidocr-ppocrv5", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals(AppleVisionOcrProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return Profiles.First(profile => profile.Name == "realtime-dialogue");
        }

        if (provider.Equals("paddleocr", StringComparison.OrdinalIgnoreCase))
        {
            return Profiles.First(profile => profile.Name == "cjk-screenshot-text");
        }

        return SelectAutoProfile(contentType, preference);
    }

    // Scripts the rapidocr-net PP-OCRv5 "ch" rec model does NOT cover (it reads Chinese,
    // Japanese kana/kanji, and Latin). These route to OneOCR for its broader Windows
    // language-pack coverage instead.
    private static readonly HashSet<string> RealtimeOneOcrLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ko", "ru", "uk", "be", "bg", "sr", "mk", "el", "th", "ar", "fa", "ur", "he", "hi"
    };

    private string PickRealtimeProvider(string? language)
    {
        // rapidocr-net-v6 (PP-OCRv6 medium det + small rec) is the preferred realtime engine when
        // its models are bundled: it reads sparse/vertical kana and low-contrast Latin that the v5
        // ch PP-OCRv5 path missed. Falls back to rapidocr-net (v5) then OneOCR so a build without the
        // v6 models keeps working unchanged.
        var rapidPreferred = _providers.Contains("rapidocr-net-v6") ? "rapidocr-net-v6" : "rapidocr-net";

        // macOS: Apple Vision (VNRecognizeText) is the native realtime engine — NPU-accelerated and,
        // on macOS 13+, reads CJK (ja/zh-Hans/zh-Hant/yue/ko) plus ru/uk/th/vi. Prefer it when
        // registered; rapidocr-net (CPU) stays the cross-platform fallback. Windows/Linux skip this.
        if (OperatingSystem.IsMacOS() && _providers.Contains(AppleVisionOcrProvider.ProviderName))
        {
            return SelectRegisteredProvider(AppleVisionOcrProvider.ProviderName, rapidPreferred);
        }

        // LunaTranslator-style per-language routing + RSTGameTranslation-style sequential
        // fallback: rapidocr-net (CPU, ~16-30ms incremental, reads CJK+Latin, and unlike
        // oneocr is NOT slowed by the video's GPU decode) is the realtime default; OneOCR
        // (broader scripts, ~300ms, GPU) covers what its model can't and is the fallback.
        var baseLanguage = (language ?? string.Empty).Trim().ToLowerInvariant().Split('-')[0];
        return RealtimeOneOcrLanguages.Contains(baseLanguage)
            ? SelectRegisteredProvider("oneocr", rapidPreferred)
            : SelectRegisteredProvider(rapidPreferred, "oneocr");
    }

    private string SelectRegisteredProvider(string preferred, string fallback)
    {
        if (_providers.Contains(preferred))
        {
            return preferred;
        }

        if (_providers.Contains(fallback))
        {
            return fallback;
        }

        return _options.Ocr.DefaultProvider.Equals(AutoProvider, StringComparison.OrdinalIgnoreCase)
            ? "external"
            : _options.Ocr.DefaultProvider;
    }

    private QualityAwareProviderSelection SelectQualityAwareProvider(
        OcrRoutingProfile profile,
        string contentType,
        string selectedProvider,
        string fallbackProvider,
        IReadOnlyList<OcrSmokeQualitySummary>? qualitySummaries)
    {
        var selectedQuality = PickQualitySummary(selectedProvider, profile, contentType, qualitySummaries);
        if (!profile.PreservesStructure || qualitySummaries is null || qualitySummaries.Count == 0)
        {
            return new QualityAwareProviderSelection(selectedProvider, selectedQuality, Reason: null);
        }

        var fallbackRegistered = _providers.Contains(fallbackProvider);
        if (!fallbackRegistered || selectedProvider.Equals(fallbackProvider, StringComparison.OrdinalIgnoreCase))
        {
            return new QualityAwareProviderSelection(selectedProvider, selectedQuality, Reason: null);
        }

        var fallbackQuality = PickQualitySummary(fallbackProvider, profile, contentType, qualitySummaries);
        if (QualityRank(fallbackQuality) <= QualityRank(selectedQuality))
        {
            return new QualityAwareProviderSelection(selectedProvider, selectedQuality, Reason: null);
        }

        var selectedIssueLabel = QualityIssueLabel(selectedQuality);
        var reason =
            $"Auto route selected '{fallbackProvider}' for {contentType} because recent smoke quality outranks '{selectedProvider}' ({QualityLabel(fallbackQuality)} vs {QualityLabel(selectedQuality)}{selectedIssueLabel}).";
        return new QualityAwareProviderSelection(fallbackProvider, fallbackQuality, reason);
    }

    private static OcrSmokeQualitySummary? PickQualitySummary(
        string provider,
        OcrRoutingProfile profile,
        string contentType,
        IReadOnlyList<OcrSmokeQualitySummary>? qualitySummaries)
    {
        if (qualitySummaries is null || qualitySummaries.Count == 0)
        {
            return null;
        }

        return qualitySummaries
            .Where(summary => summary.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(summary => IsQualityContentMatch(summary, profile, contentType))
            .ThenBy(summary => QualityRank(summary))
            .ThenByDescending(summary => summary.RuntimeFailureCount)
            .ThenByDescending(summary => summary.TableIntegrityIssueCount)
            .ThenBy(summary => summary.Scope.Equals("provider", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(summary => summary.SampleCount)
            .ThenByDescending(summary => summary.LastSeenAt)
            .FirstOrDefault();
    }

    private static bool IsQualityContentMatch(
        OcrSmokeQualitySummary summary,
        OcrRoutingProfile profile,
        string contentType)
        => summary.ContentType.Equals(contentType, StringComparison.OrdinalIgnoreCase) ||
           (profile.PreservesStructure && IsStructureContentType(summary.ContentType));

    private static bool IsStructureContentType(string contentType)
        => contentType is "formula" or "table" or "structure" or "document" or "document_region" or "formula_table" or "high_accuracy";

    private static int QualityRank(OcrSmokeQualitySummary? summary)
        => summary is null ? 2 : QualityRank(summary.Status);

    private static int QualityRank(string status)
        => status switch
        {
            "pass" => 4,
            "warn" => 1,
            "fail" => 0,
            _ => 2
        };

    private static string QualityLabel(OcrSmokeQualitySummary? summary)
        => summary is null ? "unknown" : $"{summary.Provider}:{summary.Status}";

    private static string QualityIssueLabel(OcrSmokeQualitySummary? summary)
    {
        if (summary is null || summary.Issues.Count == 0)
        {
            return string.Empty;
        }

        var issueCodes = summary.Issues
            .Take(2)
            .Select(issue => issue.Count > 1 ? $"{issue.Code} x{issue.Count}" : issue.Code);
        return $"; issues: {string.Join(", ", issueCodes)}";
    }

    private static string NormalizeContentType(string? value)
    {
        var normalized = Pick(value, "dialogue")
            .Replace('-', '_')
            .ToLowerInvariant();
        return normalized switch
        {
            "game_dialogue" or "prose" or "ui_label" or "realtime" => "dialogue",
            "math" or "equation" => "formula",
            "formulas_tables" or "math_table" => "formula_table",
            "doc" or "document_ocr" => "document",
            "highaccuracy" or "manual_high_accuracy" => "high_accuracy",
            _ => normalized
        };
    }

    private static string NormalizePreference(string? value)
    {
        var normalized = Pick(value, "speed")
            .Replace('-', '_')
            .ToLowerInvariant();
        return normalized switch
        {
            "realtime" or "fast" => "speed",
            "precise" or "quality" => "accuracy",
            "structure" => "balanced",
            _ => normalized
        };
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed record QualityAwareProviderSelection(
        string Provider,
        OcrSmokeQualitySummary? Quality,
        string? Reason);
}
