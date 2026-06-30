using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public sealed class GlossaryStore
{
    private readonly string _directory;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ((DateTime LastWriteUtc, long Length) Stamp, Glossary Value)> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public GlossaryStore(string directory)
    {
        _directory = directory;
    }

    public async Task<IReadOnlyList<GlossarySummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<GlossarySummary>();
        }

        var glossaries = new List<GlossarySummary>();
        foreach (var file in Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var glossary = await LoadFileAsync(file, cancellationToken);
            glossaries.Add(new GlossarySummary(glossary.Id, glossary.Terms.Count, glossary.Hash));
        }

        return glossaries.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<Glossary> GetOptionalAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new Glossary(string.Empty, new Dictionary<string, string>(), string.Empty);
        }

        if (!Directory.Exists(_directory))
        {
            throw new DirectoryNotFoundException($"Glossary directory not found: {_directory}");
        }

        var path = Path.Combine(_directory, id.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? id : id + ".json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Glossary not found: {id}", path);
        }

        return await LoadFileAsync(path, cancellationToken);
    }

    private async Task<Glossary> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        // Glossaries are consulted on every translation request; re-reading and
        // re-hashing the JSON each time is wasted I/O in realtime loops. Cache by
        // mtime+length so editing the file still takes effect immediately.
        var fileInfo = new FileInfo(path);
        var stamp = (fileInfo.LastWriteTimeUtc, fileInfo.Length);
        if (_fileCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
        {
            return cached.Value;
        }

        var loaded = await LoadFileUncachedAsync(path, cancellationToken);
        _fileCache[path] = (stamp, loaded);
        return loaded;
    }

    private async Task<Glossary> LoadFileUncachedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var terms = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, _jsonOptions, cancellationToken)
            ?? new Dictionary<string, string>();

        var normalized = terms
            .Where(term => !string.IsNullOrWhiteSpace(term.Key))
            .ToDictionary(term => term.Key.Trim(), term => term.Value.Trim(), StringComparer.Ordinal);

        var normalizedTerms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var term in normalized)
        {
            var key = NormalizeTerm(term.Key);
            if (key.Length > 0 && !normalizedTerms.ContainsKey(key))
            {
                normalizedTerms[key] = term.Value;
            }

            var compactKey = NormalizeTermCompact(term.Key);
            if (compactKey.Length > 0 && !normalizedTerms.ContainsKey(compactKey))
            {
                normalizedTerms[compactKey] = term.Value;
            }
        }

        return new Glossary(Path.GetFileNameWithoutExtension(path), normalized, ComputeHash(normalized))
        {
            NormalizedTerms = normalizedTerms
        };
    }

    /// <summary>
    /// Normalizes text for the deterministic whole-text glossary match: trims, strips
    /// wrapping brackets/quotes (fullwidth and halfwidth), converts fullwidth ASCII to
    /// halfwidth, collapses internal whitespace, and lowercases. OCR of labels like
    /// "（Compile)" thus matches a glossary key of "Compile".
    /// </summary>
    public static string NormalizeTerm(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        const string wrappers = "（）()［］[]【】「」『』《》〈〉<>\"“”'‘’";
        var trimmed = value.Trim().Trim(wrappers.ToCharArray()).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(trimmed.Length);
        var pendingSpace = false;
        foreach (var raw in trimmed)
        {
            // Fullwidth ASCII (U+FF01-FF5E) -> halfwidth; ideographic space -> space.
            var ch = raw switch
            {
                >= '！' and <= '～' => (char)(raw - 0xFEE0),
                '　' => ' ',
                _ => raw
            };

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    public static string NormalizeTermCompact(string value)
        => NormalizeTerm(value).Replace(" ", string.Empty, StringComparison.Ordinal);

    public static string ComputeHash(IReadOnlyDictionary<string, string> terms)
    {
        if (terms.Count == 0)
        {
            return string.Empty;
        }

        var canonical = string.Join(
            "\n",
            terms.OrderBy(term => term.Key, StringComparer.Ordinal)
                .Select(term => $"{term.Key}\t{term.Value}"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
