using System.Reflection;
using System.Text;

namespace Verbeam.Core.Services;

/// <summary>
/// Deterministic Simplified&lt;-&gt;Traditional Chinese conversion using embedded
/// OpenCC dictionaries (forward maximum-matching, mirroring OpenCC's s2tw / t2s
/// conversion chains). Unlike an LLM, this is faithful (only swaps characters,
/// never rephrases / drops / truncates), instant (0ms), and never leaves the
/// wrong variant behind — the right tool for zh-CN&lt;-&gt;zh-TW where the content
/// is the same language and only the script differs.
/// </summary>
public sealed class ChineseVariantConverter
{
    private sealed class Pass
    {
        public required Dictionary<string, string> Map { get; init; }
        public required int MaxKeyLen { get; init; }
    }

    private static readonly Lazy<ChineseVariantConverter> _shared = new(() => new ChineseVariantConverter());
    public static ChineseVariantConverter Shared => _shared.Value;

    private readonly Pass[] _s2tw;   // Simplified -> Traditional (Taiwan standard)
    private readonly Pass[] _t2s;    // Traditional -> Simplified

    public ChineseVariantConverter()
    {
        // s2tw chain: pass1 = group{STPhrases, STCharacters}, pass2 = TWVariants.
        _s2tw =
        [
            BuildPass("STCharacters.txt", "STPhrases.txt"),
            BuildPass("TWVariants.txt"),
        ];
        // t2s chain: pass1 = group{TSPhrases, TSCharacters}.
        _t2s =
        [
            BuildPass("TSCharacters.txt", "TSPhrases.txt"),
        ];
    }

    /// <summary>Convert Simplified Chinese to Traditional (Taiwan standard). No-op on already-Traditional/neutral text.</summary>
    public string ToTraditionalTaiwan(string text) => Run(text, _s2tw);

    /// <summary>Convert Traditional Chinese to Simplified. No-op on already-Simplified/neutral text.</summary>
    public string ToSimplified(string text) => Run(text, _t2s);

    private static string Run(string text, Pass[] passes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var pass in passes)
        {
            text = ConvertPass(text, pass);
        }

        return text;
    }

    private static string ConvertPass(string text, Pass pass)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var maxLen = Math.Min(pass.MaxKeyLen, text.Length - i);
            var matched = false;
            for (var len = maxLen; len >= 1; len--)
            {
                if (pass.Map.TryGetValue(text.Substring(i, len), out var value))
                {
                    builder.Append(value);
                    i += len;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                // Keep the original codepoint intact (don't split a surrogate pair).
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(text[i]);
                    builder.Append(text[i + 1]);
                    i += 2;
                }
                else
                {
                    builder.Append(text[i]);
                    i++;
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds one conversion pass. When multiple dict files are given they form an
    /// OpenCC "group": files listed first win on key collisions, so later files are
    /// loaded first and earlier files overwrite them.
    /// </summary>
    private static Pass BuildPass(params string[] dictFiles)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        // Load in reverse so the first-listed dict (highest priority) wins.
        for (var f = dictFiles.Length - 1; f >= 0; f--)
        {
            foreach (var (key, value) in ReadDict(dictFiles[f]))
            {
                map[key] = value;
            }
        }

        var maxKeyLen = 1;
        foreach (var key in map.Keys)
        {
            if (key.Length > maxKeyLen)
            {
                maxKeyLen = key.Length;
            }
        }

        return new Pass { Map = map, MaxKeyLen = maxKeyLen };
    }

    private static IEnumerable<(string Key, string Value)> ReadDict(string fileName)
    {
        var resourceName = "Verbeam.Core.Data.opencc." + fileName;
        var assembly = typeof(ChineseVariantConverter).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded OpenCC dictionary not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab + 1 >= line.Length)
            {
                continue;
            }

            var key = line[..tab];
            var values = line[(tab + 1)..];
            // The value column may list several alternatives separated by spaces;
            // OpenCC uses the first as the canonical conversion.
            var space = values.IndexOf(' ');
            var value = space < 0 ? values : values[..space];
            if (value.Length > 0)
            {
                yield return (key, value);
            }
        }
    }
}
