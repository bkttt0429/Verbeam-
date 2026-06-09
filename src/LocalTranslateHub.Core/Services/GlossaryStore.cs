using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalTranslateHub.Core.Models;

namespace LocalTranslateHub.Core.Services;

public sealed class GlossaryStore
{
    private readonly string _directory;
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
        await using var stream = File.OpenRead(path);
        var terms = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, _jsonOptions, cancellationToken)
            ?? new Dictionary<string, string>();

        var normalized = terms
            .Where(term => !string.IsNullOrWhiteSpace(term.Key))
            .ToDictionary(term => term.Key.Trim(), term => term.Value.Trim(), StringComparer.Ordinal);

        return new Glossary(Path.GetFileNameWithoutExtension(path), normalized, ComputeHash(normalized));
    }

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
