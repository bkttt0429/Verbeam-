using System.Text;

namespace Verbeam.Core.Providers;

/// <summary>
/// Builds a process argument list from an operator-configured command template (the OCR/ASR
/// "External" provider command line), substituting <c>{placeholder}</c> tokens with runtime values.
///
/// The template is tokenized FIRST (respecting double-quoted segments), then placeholders are
/// substituted INTO the already-separated tokens, and each token is handed to
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, which applies correct Win32
/// argument escaping. Because runtime values are substituted only after tokenization, they can
/// never alter token boundaries — so request-derived values (OCR/ASR language, preprocess preset)
/// cannot inject additional arguments. This replaces hand-built quoting of a single
/// <c>Arguments</c> string, which is prone to argument injection (e.g. a value ending in a
/// backslash escaping the closing quote).
/// </summary>
public static class ExternalCommandTemplate
{
    /// <summary>
    /// Tokenizes <paramref name="template"/> and returns one argument per token, with each
    /// <paramref name="substitutions"/> entry (placeholder -&gt; value) applied to the token text.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        string template,
        IReadOnlyDictionary<string, string> substitutions)
    {
        var tokens = Tokenize(template);
        if (substitutions.Count == 0)
        {
            return tokens;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var value = tokens[i];
            foreach (var (placeholder, replacement) in substitutions)
            {
                value = value.Replace(placeholder, replacement, StringComparison.Ordinal);
            }

            tokens[i] = value;
        }

        return tokens;
    }

    // Splits on unquoted whitespace; double quotes group a segment and are stripped; a backslash
    // immediately before a double quote escapes it (the quote becomes literal). The template is
    // operator-controlled config, so this only needs to reproduce the author's intended token
    // boundaries — runtime values are substituted AFTER tokenizing and never affect boundaries.
    private static List<string> Tokenize(string template)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            if (c == '\\' && i + 1 < template.Length && template[i + 1] == '"')
            {
                current.Append('"');
                hasToken = true;
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
