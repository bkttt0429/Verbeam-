using System.Net;
using System.Text;

namespace Verbeam.Core.Services;

/// <summary>
/// One ordered piece of a document. <see cref="Translate"/> distinguishes text
/// that should be sent to the translator from spans that must be emitted
/// verbatim (markdown code fences, HTML tags/script/style/comments, blank or
/// whitespace-only runs).
/// </summary>
public sealed record DocumentSegment(string Text, bool Translate);

/// <summary>
/// Splits markdown into block-level segments so the translator receives whole
/// paragraphs instead of one call per physical line, and never sees fenced code
/// blocks. Contract: <c>string.Join("\n", segments.Select(s =&gt; s.Text))</c>
/// reproduces the input with newlines normalized to "\n"; the caller replaces
/// each translatable segment's text with its translation before re-joining.
/// </summary>
public static class MarkdownSegmenter
{
    public static IReadOnlyList<DocumentSegment> Segment(string text, bool mergeParagraphs = true)
    {
        var segments = new List<DocumentSegment>();
        if (text is null)
        {
            return segments;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');

        var paragraph = new List<string>();
        var fence = new List<string>();
        var inFence = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            segments.Add(new DocumentSegment(string.Join("\n", paragraph), Translate: true));
            paragraph.Clear();
        }

        foreach (var line in lines)
        {
            var isFenceLine = IsFenceLine(line);
            if (inFence)
            {
                fence.Add(line);
                if (isFenceLine)
                {
                    segments.Add(new DocumentSegment(string.Join("\n", fence), Translate: false));
                    fence.Clear();
                    inFence = false;
                }

                continue;
            }

            if (isFenceLine)
            {
                FlushParagraph();
                fence.Add(line);
                inFence = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                segments.Add(new DocumentSegment(line, Translate: false));
                continue;
            }

            if (mergeParagraphs)
            {
                paragraph.Add(line);
            }
            else
            {
                segments.Add(new DocumentSegment(line, Translate: true));
            }
        }

        if (inFence && fence.Count > 0)
        {
            // Unterminated fence: keep the remaining lines verbatim rather than translate code.
            segments.Add(new DocumentSegment(string.Join("\n", fence), Translate: false));
        }

        FlushParagraph();
        return segments;
    }

    private static bool IsFenceLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) ||
               trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }
}

/// <summary>
/// Splits HTML into text nodes and verbatim spans (tags, comments, and the
/// contents of <c>script</c>/<c>style</c>/<c>title</c> elements). Translatable
/// segments carry HTML-decoded text; the caller must HTML-encode the
/// translation before writing it back. Whitespace-only text is left verbatim.
/// </summary>
public static class HtmlTextSegmenter
{
    private static readonly HashSet<string> RawContentTags =
        new(StringComparer.OrdinalIgnoreCase) { "script", "style", "title" };

    public static IReadOnlyList<DocumentSegment> Segment(string html)
    {
        var segments = new List<DocumentSegment>();
        if (string.IsNullOrEmpty(html))
        {
            return segments;
        }

        var text = new StringBuilder();
        var index = 0;
        var length = html.Length;

        void FlushText()
        {
            if (text.Length == 0)
            {
                return;
            }

            var raw = text.ToString();
            text.Clear();
            segments.Add(string.IsNullOrWhiteSpace(raw)
                ? new DocumentSegment(raw, Translate: false)
                : new DocumentSegment(WebUtility.HtmlDecode(raw), Translate: true));
        }

        while (index < length)
        {
            if (html[index] != '<')
            {
                text.Append(html[index]);
                index++;
                continue;
            }

            if (StartsWithAt(html, index, "<!--"))
            {
                FlushText();
                var end = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                var stop = end < 0 ? length : end + 3;
                segments.Add(new DocumentSegment(html[index..stop], Translate: false));
                index = stop;
                continue;
            }

            var tagName = ReadTagName(html, index, out var isClosingTag);
            if (!isClosingTag && RawContentTags.Contains(tagName))
            {
                FlushText();
                var stop = FindRawElementEnd(html, index, tagName, length);
                segments.Add(new DocumentSegment(html[index..stop], Translate: false));
                index = stop;
                continue;
            }

            FlushText();
            var gt = html.IndexOf('>', index);
            var tagStop = gt < 0 ? length : gt + 1;
            segments.Add(new DocumentSegment(html[index..tagStop], Translate: false));
            index = tagStop;
        }

        FlushText();
        return segments;
    }

    private static bool StartsWithAt(string source, int index, string literal)
        => index + literal.Length <= source.Length &&
           source.AsSpan(index, literal.Length).SequenceEqual(literal);

    private static string ReadTagName(string source, int ltIndex, out bool isClosingTag)
    {
        isClosingTag = false;
        var i = ltIndex + 1;
        if (i < source.Length && source[i] == '/')
        {
            isClosingTag = true;
            i++;
        }

        var start = i;
        while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '-' or ':'))
        {
            i++;
        }

        return source[start..i];
    }

    private static int FindRawElementEnd(string source, int ltIndex, string tagName, int length)
    {
        var openGt = source.IndexOf('>', ltIndex);
        if (openGt < 0)
        {
            return length;
        }

        if (source[openGt - 1] == '/')
        {
            // Self-closing raw tag has no content.
            return openGt + 1;
        }

        var close = source.IndexOf("</" + tagName, openGt + 1, StringComparison.OrdinalIgnoreCase);
        if (close < 0)
        {
            return length;
        }

        var closeGt = source.IndexOf('>', close);
        return closeGt < 0 ? length : closeGt + 1;
    }
}
