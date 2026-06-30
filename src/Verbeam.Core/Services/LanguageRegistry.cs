using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

/// <summary>
/// Single source of truth for language identity. UI and API values are hints that
/// normalize to a canonical BCP-47 tag here; OCR provider adapters look up their
/// engine-specific code instead of doing ad-hoc prefix matching. Adding a language
/// means adding one profile entry, not touching the OCR pipeline.
/// </summary>
public static class LanguageRegistry
{
    public const string Auto = "auto";

    public const string TraditionalChinese = "zh-Hant-TW";
    public const string SimplifiedChinese = "zh-Hans-CN";
    public const string Japanese = "ja-JP";
    public const string English = "en-US";
    public const string Korean = "ko-KR";

    public static class Providers
    {
        public const string Windows = "windows";
        public const string Tesseract = "tesseract";
        public const string EasyOcr = "easyocr";
        public const string PaddleOcr = "paddleocr";
    }

    public static readonly IReadOnlyList<LanguageProfile> Profiles =
    [
        new(
            TraditionalChinese,
            "zh-TW",
            Scripts: ["Hant", "Hani"],
            Aliases: ["zh-tw", "zh-hant", "zh-hant-tw", "zh-hk", "chi_tra", "ch_tra", "chinese_cht", "traditional chinese", "traditional chinese taiwan"],
            ProviderCodes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Providers.Windows] = "zh-Hant",
                [Providers.Tesseract] = "chi_tra",
                [Providers.EasyOcr] = "ch_tra",
                [Providers.PaddleOcr] = "chinese_cht"
            }),
        new(
            SimplifiedChinese,
            "zh-CN",
            Scripts: ["Hans", "Hani"],
            Aliases: ["zh", "zh-cn", "zh-hans", "zh-hans-cn", "chi_sim", "ch_sim", "ch", "simplified chinese"],
            ProviderCodes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Providers.Windows] = "zh-Hans",
                [Providers.Tesseract] = "chi_sim",
                [Providers.EasyOcr] = "ch_sim",
                [Providers.PaddleOcr] = "ch"
            }),
        new(
            Japanese,
            "ja",
            Scripts: ["Kana", "Hani"],
            Aliases: ["ja", "jp", "ja-jp", "jpn", "japan", "japanese"],
            ProviderCodes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Providers.Windows] = "ja-JP",
                [Providers.Tesseract] = "jpn",
                [Providers.EasyOcr] = "ja",
                [Providers.PaddleOcr] = "japan"
            }),
        new(
            English,
            "en",
            Scripts: ["Latn"],
            Aliases: ["en", "en-us", "en-gb", "eng", "english"],
            ProviderCodes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Providers.Windows] = "en-US",
                [Providers.Tesseract] = "eng",
                [Providers.EasyOcr] = "en",
                [Providers.PaddleOcr] = "en"
            }),
        new(
            Korean,
            "ko",
            Scripts: ["Hang"],
            Aliases: ["ko", "ko-kr", "kor", "korean"],
            ProviderCodes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Providers.Windows] = "ko-KR",
                [Providers.Tesseract] = "kor",
                [Providers.EasyOcr] = "ko",
                [Providers.PaddleOcr] = "korean"
            })
    ];

    /// <summary>Allowed-language preset used by "Auto" and "Auto: CJK + English".</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedLanguages =
        [TraditionalChinese, SimplifiedChinese, Japanese, Korean, English];

    private static readonly Dictionary<string, LanguageProfile> Index = BuildIndex();

    private static Dictionary<string, LanguageProfile> BuildIndex()
    {
        var index = new Dictionary<string, LanguageProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Profiles)
        {
            index[profile.Canonical] = profile;
            index[profile.TranslationCode] = profile;
            foreach (var alias in profile.Aliases)
            {
                index[alias] = profile;
            }

            foreach (var code in profile.ProviderCodes.Values)
            {
                index.TryAdd(code, profile);
            }
        }

        return index;
    }

    public static bool IsAuto(string? value)
        => string.IsNullOrWhiteSpace(value) ||
           value.Trim().Equals(Auto, StringComparison.OrdinalIgnoreCase);

    public static bool TryGet(string? value, out LanguageProfile profile)
    {
        profile = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (Index.TryGetValue(trimmed, out var exact))
        {
            profile = exact;
            return true;
        }

        // Tolerate longer tags (e.g. "zh-Hant-TW-x-foo", "ja-JP-mac"): walk up the
        // subtag chain until a known prefix matches.
        var normalized = trimmed.ToLowerInvariant();
        for (var cut = normalized.LastIndexOf('-'); cut > 0; cut = normalized.LastIndexOf('-'))
        {
            normalized = normalized[..cut];
            if (Index.TryGetValue(normalized, out var prefix))
            {
                profile = prefix;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalizes any UI/API/alias value to its canonical tag. Blank or "auto"
    /// stays "auto"; unknown values pass through trimmed so future languages
    /// degrade gracefully instead of throwing.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (IsAuto(value))
        {
            return Auto;
        }

        return TryGet(value, out var profile) ? profile.Canonical : value!.Trim();
    }

    /// <summary>Engine-specific code for a canonical tag; falls back to the canonical value.</summary>
    public static string ProviderCode(string? language, string providerKind)
    {
        if (!TryGet(language, out var profile))
        {
            return language?.Trim() ?? string.Empty;
        }

        return profile.ProviderCodes.TryGetValue(providerKind, out var code)
            ? code
            : profile.Canonical;
    }

    /// <summary>Maps a canonical tag to the translation-side language code (e.g. zh-Hant-TW → zh-TW).</summary>
    public static string ToTranslationCode(string? language)
        => TryGet(language, out var profile) ? profile.TranslationCode : language?.Trim() ?? string.Empty;

    /// <summary>
    /// Resolves the allowed-language set for auto detection: explicit request values
    /// normalized, otherwise the default CJK + English preset.
    /// </summary>
    public static IReadOnlyList<string> ResolveAllowedLanguages(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return DefaultAllowedLanguages;
        }

        var resolved = requested
            .Select(Normalize)
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != Auto)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return resolved.Length == 0 ? DefaultAllowedLanguages : resolved;
    }
}
