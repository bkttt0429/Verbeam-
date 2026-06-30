using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public static class TokenUsageEstimator
{
    public static TokenUsage EstimateProviderRequest(
        ProviderTranslationRequest request,
        string outputText,
        string source)
    {
        var prompt = PromptRenderer.Render(request);
        var inputText = $"{prompt.System}\n\n{prompt.User}";
        return EstimateTextPair(inputText, outputText, source);
    }

    public static TokenUsage EstimateTextPair(string inputText, string outputText, string source)
    {
        var input = EstimateTokens(inputText);
        var output = EstimateTokens(outputText);
        return new TokenUsage(input, output, input + output, source, IsEstimated: true);
    }

    public static long EstimateTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var cjkLike = 0;
        var other = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (IsCjkLike(ch))
            {
                cjkLike += 1;
            }
            else
            {
                other += 1;
            }
        }

        return cjkLike + (long)Math.Ceiling(other / 4.0);
    }

    private static bool IsCjkLike(char ch)
        => ch is >= '\u3040' and <= '\u30ff' // Hiragana/Katakana
            || ch is >= '\u3400' and <= '\u9fff' // CJK Unified + Extension A
            || ch is >= '\uf900' and <= '\ufaff' // CJK Compatibility
            || ch is >= '\uac00' and <= '\ud7af'; // Hangul syllables
}
