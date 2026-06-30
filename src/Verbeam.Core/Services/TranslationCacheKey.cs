using System.Security.Cryptography;
using System.Text;

namespace Verbeam.Core.Services;

public static class TranslationCacheKey
{
    /// <summary>
    /// Normalizes text for cache key derivation only (the LLM still receives the raw
    /// text): NFKC unifies width variants (fullwidth ASCII, halfwidth katakana),
    /// zero-width characters are dropped, and whitespace runs collapse to one space.
    /// OCR jitter variants of the same subtitle thus share one cache entry instead of
    /// each paying an LLM call. Content characters and casing are never altered, so a
    /// real change (a digit, a name) always produces a new key.
    /// </summary>
    public static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var ch in normalized)
        {
            // Zero-width space/joiners and BOM.
            if (ch is '​' or '‌' or '‍' or '﻿')
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                // Whitespace between two CJK-range characters is OCR noise, not
                // meaning; between ASCII words it separates tokens and must stay.
                if (builder[^1] <= '⹿' || ch <= '⹿')
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    public static string Create(
        string text,
        string source,
        string target,
        string mode,
        string provider,
        string model,
        string presetVersion,
        string glossaryHash,
        string contextHash = "")
    {
        var parts = new List<string>
        {
            NormalizeText(text),
            source,
            target,
            mode,
            provider,
            model,
            presetVersion,
            glossaryHash
        };

        if (!string.IsNullOrWhiteSpace(contextHash))
        {
            parts.Add(contextHash);
        }

        var raw = string.Join("\u001f", parts);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}
