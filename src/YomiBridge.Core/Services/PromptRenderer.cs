using YomiBridge.Core.Models;

namespace YomiBridge.Core.Services;

public static class PromptRenderer
{
    public static RenderedPrompt Render(ProviderTranslationRequest request)
    {
        var glossary = FormatGlossary(request.GlossaryTerms);
        var context = FormatContext(request.Context);
        var source = FormatLanguage(request.Source);
        var target = FormatLanguage(request.Target);
        var userTemplate = request.Preset.UserTemplate;
        var user = userTemplate
            .Replace("{TEXT}", request.Text, StringComparison.Ordinal)
            .Replace("{SOURCE}", source, StringComparison.Ordinal)
            .Replace("{TARGET}", target, StringComparison.Ordinal)
            .Replace("{MODE}", request.Mode, StringComparison.Ordinal)
            .Replace("{GLOSSARY}", glossary, StringComparison.Ordinal)
            .Replace("{CONTEXT}", context, StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(context) && !userTemplate.Contains("{CONTEXT}", StringComparison.Ordinal))
        {
            user = $"Background context (compressed; use only for terminology, tone, and disambiguation):{Environment.NewLine}{context}{Environment.NewLine}{Environment.NewLine}{user}";
        }

        return new RenderedPrompt(request.Preset.SystemPrompt, user);
    }

    private static string FormatLanguage(string language)
    {
        return language.Trim().ToLowerInvariant() switch
        {
            "ja" or "jp" or "ja-jp" => "Japanese",
            "zh-tw" or "zh-hant" or "traditional chinese" => "Traditional Chinese (Taiwan) / 繁體中文（台灣）, using Traditional Chinese characters only and never Simplified Chinese",
            "zh-cn" or "zh-hans" or "simplified chinese" => "Simplified Chinese (China), using Simplified Chinese characters",
            "zh" => "Chinese",
            "en" or "en-us" or "en-gb" => "English",
            "ko" or "ko-kr" => "Korean",
            _ => language
        };
    }

    private static string FormatGlossary(IReadOnlyDictionary<string, string> terms)
    {
        if (terms.Count == 0)
        {
            return "(none)";
        }

        return string.Join(
            Environment.NewLine,
            terms.OrderBy(term => term.Key, StringComparer.Ordinal)
                .Select(term => $"{term.Key} => {term.Value}"));
    }

    private static string FormatContext(string context)
        => string.IsNullOrWhiteSpace(context) ? "(none)" : context.Trim();
}

public sealed record RenderedPrompt(string System, string User);
