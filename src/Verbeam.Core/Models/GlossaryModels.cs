namespace Verbeam.Core.Models;

public sealed record Glossary(string Id, IReadOnlyDictionary<string, string> Terms, string Hash)
{
    /// <summary>
    /// Lookup of normalized term keys (see GlossaryStore.NormalizeTerm and
    /// GlossaryStore.NormalizeTermCompact) to translated value, used for the
    /// deterministic whole-text translation short-circuit.
    /// </summary>
    public IReadOnlyDictionary<string, string> NormalizedTerms { get; init; } =
        new Dictionary<string, string>();
}

public sealed record GlossarySummary(string Id, int TermCount, string Hash);
