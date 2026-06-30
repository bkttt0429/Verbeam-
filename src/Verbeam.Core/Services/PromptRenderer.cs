using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public static class PromptRenderer
{
    private const string StableNewLine = "\n";

    public static RenderedPrompt Render(ProviderTranslationRequest request)
    {
        var glossary = FormatGlossary(request.GlossaryTerms);
        var hasContext = !string.IsNullOrWhiteSpace(request.Context) ||
            !string.IsNullOrWhiteSpace(request.MemoryContext);
        var context = FormatContext(request.Context, request.MemoryContext);
        var source = TranslationConfigurationCatalog.FormatLanguageForPrompt(request.Source);
        var target = TranslationConfigurationCatalog.FormatLanguageForPrompt(request.Target);
        var userTemplate = request.Preset.UserTemplate;
        var user = userTemplate
            .Replace("{TEXT}", request.Text, StringComparison.Ordinal)
            .Replace("{SOURCE}", source, StringComparison.Ordinal)
            .Replace("{TARGET}", target, StringComparison.Ordinal)
            .Replace("{MODE}", request.Mode, StringComparison.Ordinal)
            .Replace("{GLOSSARY}", glossary, StringComparison.Ordinal)
            .Replace("{CONTEXT}", context, StringComparison.Ordinal);

        if (hasContext && !userTemplate.Contains("{CONTEXT}", StringComparison.Ordinal))
        {
            user = $"{context}{Environment.NewLine}{Environment.NewLine}{user}";
        }

        return new RenderedPrompt(request.Preset.SystemPrompt, user);
    }

    public static RenderedCachedPrompt RenderForPrefixCache(ProviderTranslationRequest request)
    {
        var glossary = FormatGlossary(request.GlossaryTerms).ReplaceLineEndings(StableNewLine);
        var source = TranslationConfigurationCatalog.FormatLanguageForPrompt(request.Source);
        var target = TranslationConfigurationCatalog.FormatLanguageForPrompt(request.Target);
        var hasContext = !string.IsNullOrWhiteSpace(request.Context) ||
            !string.IsNullOrWhiteSpace(request.MemoryContext);
        var context = hasContext
            ? FormatContext(request.Context, request.MemoryContext).ReplaceLineEndings(StableNewLine)
            : string.Empty;
        var stablePrefix = string.Join(
            $"{StableNewLine}{StableNewLine}",
            request.Preset.SystemPrompt.Trim().ReplaceLineEndings(StableNewLine),
            string.Join(
                StableNewLine,
                "Translation setup:",
                $"Source: {source}",
                $"Target: {target}",
                $"Mode: {request.Mode}",
                "Glossary:",
                glossary));
        var suffix = hasContext
            ? string.Join(
                $"{StableNewLine}{StableNewLine}",
                "Dynamic context:",
                context,
                "Text:",
                request.Text)
            : request.Text.ReplaceLineEndings(StableNewLine);

        return new RenderedCachedPrompt(stablePrefix, suffix);
    }

    private static string FormatGlossary(IReadOnlyDictionary<string, string> terms)
    {
        if (terms.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            terms.OrderBy(term => term.Key, StringComparer.Ordinal)
                .Select(term =>
                    $"{FormatInlineData(term.Key)} => {FormatInlineData(term.Value)}"));
    }

    private static string FormatContext(string requestContext, string memoryContext)
    {
        var blocks = new List<string>();
        if (!string.IsNullOrWhiteSpace(memoryContext))
        {
            blocks.Add(memoryContext.Trim());
        }

        if (!string.IsNullOrWhiteSpace(requestContext))
        {
            blocks.Add(PromptContextRenderer.RenderRequestContext(requestContext));
        }

        return blocks.Count == 0
            ? "(none)"
            : string.Join($"{Environment.NewLine}{Environment.NewLine}", blocks);
    }

    private static string FormatInlineData(string value)
        => PromptContextRenderer.SanitizeInlineData(value).ReplaceLineEndings(" ");
}

public sealed record RenderedPrompt(string System, string User);

public sealed record RenderedCachedPrompt(string StablePrefix, string Suffix);
