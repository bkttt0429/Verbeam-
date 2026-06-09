using System.Security.Cryptography;
using System.Text;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class ContextCompressionService
{
    private static readonly char[] BoundaryCharacters =
    [
        '\n',
        ',',
        ';',
        '.',
        '!',
        '?',
        '\u3002',
        '\uFF01',
        '\uFF1F',
        '\uFF0C',
        '\uFF1B'
    ];

    private readonly ContextCompressionOptions _options;

    public ContextCompressionService(ContextCompressionOptions options)
    {
        _options = options;
    }

    public CompressedContext Compress(IEnumerable<string?> contextPieces)
    {
        var context = NormalizeContext(contextPieces);
        if (context.Length == 0)
        {
            return CompressedContext.Empty;
        }

        var maxCharacters = Math.Max(1, _options.MaxCharacters);
        var text = !_options.Enabled || context.Length <= maxCharacters
            ? context
            : CompressText(context, maxCharacters);

        return new CompressedContext(
            text,
            ComputeHash(text),
            IsCompressed: text.Length < context.Length,
            OriginalCharacters: context.Length,
            CompressedCharacters: text.Length);
    }

    private string CompressText(string text, int maxCharacters)
    {
        const string markerTemplate = "\n\n[...compressed {0} characters...]\n\n";
        var marker = string.Format(markerTemplate, Math.Max(0, text.Length - maxCharacters));
        var budget = maxCharacters - marker.Length;
        if (budget <= 20)
        {
            return TakePrefix(text, maxCharacters);
        }

        var requestedHead = Math.Max(0, _options.HeadCharacters);
        var requestedTail = Math.Max(0, _options.TailCharacters);
        if (requestedHead == 0 && requestedTail == 0)
        {
            requestedHead = budget / 2;
            requestedTail = budget - requestedHead;
        }

        var (headBudget, tailBudget) = AllocateBudget(
            budget,
            requestedHead,
            requestedTail);

        var head = TakePrefix(text, headBudget);
        var tail = TakeSuffix(text, tailBudget);
        var compressed = BuildCompressedText(text, markerTemplate, head, tail);

        while (compressed.Length > maxCharacters && head.Length + tail.Length > 0)
        {
            var overflow = compressed.Length - maxCharacters;
            if (head.Length > 1)
            {
                head = TakePrefixHard(head, Math.Max(1, head.Length - overflow));
            }
            else
            {
                tail = TakeSuffixHard(tail, Math.Max(0, tail.Length - overflow));
            }

            compressed = BuildCompressedText(text, markerTemplate, head, tail);
        }

        return compressed.Length <= maxCharacters ? compressed : TakePrefix(compressed, maxCharacters);
    }

    private static string NormalizeContext(IEnumerable<string?> contextPieces)
    {
        return string.Join(
                "\n\n",
                contextPieces
                    .Where(piece => !string.IsNullOrWhiteSpace(piece))
                    .Select(piece => piece!.ReplaceLineEndings("\n").Trim()))
            .Trim();
    }

    private static string BuildCompressedText(string original, string markerTemplate, string head, string tail)
    {
        var omitted = Math.Max(0, original.Length - head.Length - tail.Length);
        var marker = string.Format(markerTemplate, omitted);
        return $"{head}{marker}{tail}".Trim();
    }

    private static (int Head, int Tail) AllocateBudget(int budget, int requestedHead, int requestedTail)
    {
        if (requestedHead == 0 && requestedTail == 0)
        {
            var balancedHead = budget / 2;
            return (balancedHead, budget - balancedHead);
        }

        if (requestedTail == 0)
        {
            return (budget, 0);
        }

        if (requestedHead == 0)
        {
            return (0, budget);
        }

        var tail = Math.Min(requestedTail, Math.Max(1, budget - 1));
        var head = Math.Min(requestedHead, Math.Max(1, budget - tail));
        var remaining = Math.Max(0, budget - head - tail);
        return (head + remaining, tail);
    }

    private static string TakePrefix(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
        {
            return text.Trim();
        }

        var boundary = text.LastIndexOfAny(BoundaryCharacters, Math.Min(maxCharacters - 1, text.Length - 1));
        if (boundary >= maxCharacters / 2)
        {
            return text[..(boundary + 1)].Trim();
        }

        return text[..maxCharacters].Trim();
    }

    private static string TakeSuffix(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
        {
            return text.Trim();
        }

        var start = text.Length - maxCharacters;
        var boundary = text.IndexOfAny(BoundaryCharacters, start);
        if (boundary >= 0 && boundary < text.Length - (maxCharacters / 2))
        {
            return text[(boundary + 1)..].Trim();
        }

        return text[start..].Trim();
    }

    private static string TakePrefixHard(string text, int maxCharacters)
        => text.Length <= maxCharacters ? text : text[..Math.Max(0, maxCharacters)].Trim();

    private static string TakeSuffixHard(string text, int maxCharacters)
        => text.Length <= maxCharacters ? text : text[^Math.Max(0, maxCharacters)..].Trim();

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record CompressedContext(
    string Text,
    string Hash,
    bool IsCompressed,
    int OriginalCharacters,
    int CompressedCharacters)
{
    public static readonly CompressedContext Empty = new(
        string.Empty,
        string.Empty,
        IsCompressed: false,
        OriginalCharacters: 0,
        CompressedCharacters: 0);
}
