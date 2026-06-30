namespace Verbeam.Core.Services;

/// <summary>
/// Splits long source text into translation-sized chunks on natural boundaries
/// (blank-line paragraphs first, then sentence punctuation, then a hard character
/// cut as a last resort). Concatenating every <see cref="TextSegment.Content"/> with
/// its <see cref="TextSegment.Separator"/> reproduces the original text byte-for-byte,
/// so the caller can translate the content runs and re-stitch them with the untouched
/// separators. Used by <see cref="TranslationService"/> for long-text auto-chunking.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Breaks <paramref name="text"/> into ordered segments whose content runs are each
    /// at most <paramref name="maxChars"/> characters where the boundaries allow it.
    /// Returns a single segment when the text already fits or chunking is not possible.
    /// </summary>
    public static IReadOnlyList<TextSegment> Split(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars)
        {
            return [new TextSegment(text, string.Empty)];
        }

        var segments = new List<TextSegment>();
        foreach (var (content, separator) in SplitParagraphs(text))
        {
            if (content.Length <= maxChars)
            {
                segments.Add(new TextSegment(content, separator));
                continue;
            }

            var pieces = SplitLongBlock(content, maxChars);
            for (var i = 0; i < pieces.Count; i++)
            {
                // Only the final piece of a split paragraph carries the paragraph's
                // trailing separator; pieces in the middle were contiguous text.
                var pieceSeparator = i == pieces.Count - 1 ? separator : string.Empty;
                segments.Add(new TextSegment(pieces[i], pieceSeparator));
            }
        }

        return segments.Count == 0 ? [new TextSegment(text, string.Empty)] : segments;
    }

    /// <summary>
    /// Splits text into paragraph blocks at blank-line boundaries, returning each block's
    /// content together with the exact whitespace separator (blank lines) that followed it.
    /// </summary>
    private static List<(string Content, string Separator)> SplitParagraphs(string text)
    {
        var blocks = new List<(string, string)>();
        var index = 0;
        while (index < text.Length)
        {
            var separatorStart = FindParagraphSeparator(text, index, out var separatorEnd);
            if (separatorStart < 0)
            {
                blocks.Add((text[index..], string.Empty));
                break;
            }

            blocks.Add((text[index..separatorStart], text[separatorStart..separatorEnd]));
            index = separatorEnd;
        }

        return blocks;
    }

    /// <summary>
    /// Locates the next paragraph break (a newline followed, after optional spaces/tabs,
    /// by another newline) at or after <paramref name="from"/>. Consumes any run of further
    /// blank lines into the separator. Returns -1 when no break remains.
    /// </summary>
    private static int FindParagraphSeparator(string text, int from, out int separatorEnd)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r'))
            {
                continue;
            }

            var cursor = i;
            var newlineCount = 0;
            while (cursor < text.Length)
            {
                if (text[cursor] == '\r')
                {
                    cursor++;
                    if (cursor < text.Length && text[cursor] == '\n')
                    {
                        cursor++;
                    }

                    newlineCount++;
                }
                else if (text[cursor] == '\n')
                {
                    cursor++;
                    newlineCount++;
                }
                else if (text[cursor] is ' ' or '\t')
                {
                    cursor++;
                }
                else
                {
                    break;
                }
            }

            if (newlineCount >= 2)
            {
                separatorEnd = cursor;
                return i;
            }

            // A single newline is part of the paragraph; keep scanning past it.
            i = cursor - 1;
        }

        separatorEnd = -1;
        return -1;
    }

    /// <summary>
    /// Breaks a single oversized block into contiguous pieces no longer than
    /// <paramref name="maxChars"/>, preferring sentence boundaries and falling back to a
    /// hard character cut for runaway sentences. Pieces concatenate back to the input.
    /// </summary>
    private static List<string> SplitLongBlock(string block, int maxChars)
    {
        var pieces = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var sentence in SplitSentences(block))
        {
            if (sentence.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    pieces.Add(current.ToString());
                    current.Clear();
                }

                for (var offset = 0; offset < sentence.Length; offset += maxChars)
                {
                    pieces.Add(sentence.Substring(offset, Math.Min(maxChars, sentence.Length - offset)));
                }

                continue;
            }

            if (current.Length > 0 && current.Length + sentence.Length > maxChars)
            {
                pieces.Add(current.ToString());
                current.Clear();
            }

            current.Append(sentence);
        }

        if (current.Length > 0)
        {
            pieces.Add(current.ToString());
        }

        return pieces;
    }

    /// <summary>
    /// Splits text into sentences, keeping each terminating punctuation mark (CJK or Latin)
    /// or newline attached to the sentence it ends. The pieces concatenate back to the input.
    /// </summary>
    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            current.Append(ch);
            if (ch is '。' or '！' or '？' or '．' or '…' or '!' or '?' or '\n')
            {
                sentences.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            sentences.Add(current.ToString());
        }

        return sentences;
    }
}

/// <summary>
/// One translation unit: a <see cref="Content"/> run to translate, followed by the exact
/// <see cref="Separator"/> (blank lines) that must be re-inserted verbatim after it.
/// </summary>
public sealed record TextSegment(string Content, string Separator);
