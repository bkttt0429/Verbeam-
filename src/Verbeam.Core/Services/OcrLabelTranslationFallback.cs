using System.Text.RegularExpressions;

namespace Verbeam.Core.Services;

/// <summary>
/// Deterministic fallback for tiny OCR labels that local LLMs often return as
/// prompt residue or an empty response. Keep this intentionally narrow so normal
/// OCR prose still goes through the configured translator.
/// </summary>
public static class OcrLabelTranslationFallback
{
    private static readonly IReadOnlyDictionary<string, string> CompilerTerms =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["compile"] = "\u7de8\u8b6f",
            ["compiler"] = "\u7de8\u8b6f\u5668",
            ["assemble"] = "\u7d44\u8b6f",
            ["assembler"] = "\u7d44\u8b6f\u5668",
            ["preprocess"] = "\u9810\u5148\u8655\u7406",
            ["preprocessor"] = "\u524d\u7f6e\u8655\u7406\u5668",
            ["link"] = "\u9023\u7d50",
            ["linker"] = "\u9023\u7d50\u5668",
            ["library"] = "\u7a0b\u5f0f\u5eab",
            ["executable"] = "\u57f7\u884c\u6a94",
            ["sourceprogram"] = "\u539f\u7a0b\u5f0f",
            ["objectprogram"] = "\u76ee\u7684\u7a0b\u5f0f"
        };

    private static readonly Regex FileReferenceRegex = new(
        @"^(?:file|\*)\s*\.\s*(?:c|obj|lib|exe)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DotSpacingRegex = new(
        @"\s*\.\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool SupportsTarget(string? target)
        => string.Equals(
            LanguageRegistry.Normalize(target),
            LanguageRegistry.TraditionalChinese,
            StringComparison.OrdinalIgnoreCase);

    public static string? TryTranslate(string text)
    {
        var compact = GlossaryStore.NormalizeTermCompact(text);
        if (compact.Length == 0)
        {
            return null;
        }

        if (CompilerTerms.TryGetValue(compact, out var mapped))
        {
            return mapped;
        }

        var normalized = GlossaryStore.NormalizeTerm(text);
        if (!FileReferenceRegex.IsMatch(normalized))
        {
            return null;
        }

        var withoutDotSpaces = DotSpacingRegex.Replace(normalized, ".");
        return WhitespaceRegex.Replace(withoutDotSpaces, string.Empty);
    }
}
