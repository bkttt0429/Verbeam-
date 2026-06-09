namespace YomiBridge.Core.Models;

public sealed record Glossary(string Id, IReadOnlyDictionary<string, string> Terms, string Hash);

public sealed record GlossarySummary(string Id, int TermCount, string Hash);
