using System.Text.RegularExpressions;

namespace Verbeam.Core.Services;

public static partial class TranslationOutputCleaner
{
    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.ReplaceLineEndings("\n").Trim();
        var targetMatches = TargetLabelRegex().Matches(normalized);
        if (targetMatches.Count > 0)
        {
            var targetMatch = targetMatches[targetMatches.Count - 1];
            return TrimOutput(StripPromptArtifacts(normalized[(targetMatch.Index + targetMatch.Length)..]));
        }

        normalized = StripLeadingGlossaryEcho(normalized);
        normalized = SourceLabelRegex().Replace(normalized, string.Empty, 1);
        normalized = PromptLabelPrefixRegex().Replace(normalized, match => match.Value.Length > 0 && match.Value[0] == '\n' ? "\n" : string.Empty);
        return TrimOutput(StripPromptArtifacts(normalized));
    }

    private static string StripLeadingGlossaryEcho(string text)
    {
        var lines = text.Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count == 0 || !GlossaryLabelRegex().IsMatch(lines[0]))
        {
            return text;
        }

        lines.RemoveAt(0);
        while (lines.Count > 0 &&
               (string.IsNullOrWhiteSpace(lines[0]) ||
                lines[0].Trim().Equals("(none)", StringComparison.OrdinalIgnoreCase)))
        {
            lines.RemoveAt(0);
        }

        return string.Join('\n', lines).Trim();
    }

    private static string StripPromptArtifacts(string text)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (PromptPlaceholderRegex().IsMatch(trimmed) ||
                PromptArtifactLineRegex().IsMatch(trimmed))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept).Trim();
    }

    private static string TrimOutput(string value)
        => value
            .Trim()
            .Trim('`')
            .Trim()
            .Trim('"', '\'', '\u300c', '\u300d', '\u300e', '\u300f')
            .Trim();

    [GeneratedRegex(@"^\s*(\u8853\u8a9e\u8868|\u8a5e\u5f59\u8868|\u7528\u8a9e\u8868|Glossary|\u7a0b\u5f0f\u8868)\s*[:\uff1a]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GlossaryLabelRegex();

    [GeneratedRegex(@"^\s*(\u65e5\u6587|\u65e5\u672c\u8a9e|\u539f\u6587|Source|Text)\s*[:\uff1a]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SourceLabelRegex();

    [GeneratedRegex(@"(?:^|\n)\s*(\u53f0\u8a9e|\u7e41\u9ad4|\u7e41\u4e2d\u8b6f\u6587|\u7e41\u9ad4\u4e2d\u6587|\u4e2d\u6587\u7ffb\u8b6f|\u8b6f\u6587|\u7ffb\u8b6f|Target|Translation|Translated text)\s*[:\uff1a]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TargetLabelRegex();

    [GeneratedRegex(@"(^|\n)\s*(\u53f0\u8a9e|\u7e41\u9ad4|\u7e41\u4e2d\u8b6f\u6587|\u7e41\u9ad4\u4e2d\u6587|\u4e2d\u6587\u7ffb\u8b6f|\u8b6f\u6587|\u7ffb\u8b6f|Target|Translation|Translated text)\s*[:\uff1a]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PromptLabelPrefixRegex();

    [GeneratedRegex(@"<<<\s*TEXT\s*>>>|\{\{\s*TEXT\s*\}\}|\[\[\s*TEXT\s*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex PromptPlaceholderRegex();

    [GeneratedRegex(@"^\s*[\(\uff08]?\s*(\u8853\u8a9e\u8868|\u8a5e\u5f59\u8868|\u7528\u8a9e\u8868|Glossary|\u7a0b\u5f0f\u8868|\u65e5\u6587|\u65e5\u672c\u8a9e|\u539f\u6587|\u8b6f\u6587|\u7ffb\u8b6f|\u53f0\u8a9e|\u7e41\u9ad4|\u7e41\u9ad4\u4e2d\u6587|Source|Target|Translation|Translated text)\s*[\)\uff09]?\s*[:\uff1a]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PromptArtifactLineRegex();
}
