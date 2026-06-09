namespace Verbeam.Core.Models;

public sealed record MemoryItem(
    string Id,
    string ProfileId,
    string MemoryKind,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TargetText,
    string Note,
    int Priority,
    double Confidence,
    string TagsJson,
    string MetadataJson,
    string TrustLevel,
    string SourceUri,
    string SourceHash,
    string CreatedBy,
    string ApprovedBy,
    string SecurityFlagsJson,
    string Classification,
    string Visibility,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUsedAt,
    int UseCount);

public sealed record MemorySearchRequest(
    string ProfileId,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<string> MemoryKinds,
    int Limit,
    bool ActiveOnly = true,
    bool TrustedOnly = true,
    double MinimumConfidence = 0.0);

public sealed record MemoryContext(
    string Text,
    string Hash,
    IReadOnlyList<string> MemoryIds)
{
    public string PolicyVersion { get; init; } = string.Empty;
    public IReadOnlyList<MemoryContextSnippet> Snippets { get; init; } = [];

    public static readonly MemoryContext Empty = new(string.Empty, string.Empty, []);
}

public sealed record MemoryContextSnippet(
    string MemoryId,
    string MemoryKind,
    string SnippetHash,
    string TrustLevel,
    string SourceHash);

public sealed record MemoryContextAuditEntry(
    string Id,
    string RequestId,
    string ProfileId,
    string SessionId,
    string? TranslationKey,
    string MemoryId,
    string MemoryKind,
    string SnippetHash,
    string ContextHash,
    string TrustLevel,
    string SourceHash,
    string PolicyVersion,
    DateTimeOffset CreatedAt);

public sealed record MemoryContextRequest(
    string ProfileId,
    string SessionId,
    string SourceLanguage,
    string TargetLanguage,
    string Mode,
    string SourceText);

public record MemoryUpsertRequest
{
    public string? Profile { get; init; }
    public string? MemoryKind { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }
    public string? SourceText { get; init; }
    public string? TargetText { get; init; }
    public string? Note { get; init; }
    public int? Priority { get; init; }
    public double? Confidence { get; init; }
    public string? Origin { get; init; }
    public string? TrustLevel { get; init; }
    public string? SourceUri { get; init; }
    public string? SourceHash { get; init; }
    public string? CreatedBy { get; init; }
    public string? ApprovedBy { get; init; }
    public string? Classification { get; init; }
    public string? Visibility { get; init; }
}

public sealed record TranslationCorrectionRequest
{
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public string? EventId { get; init; }
    public string? CorrectedText { get; init; }
    public string? Note { get; init; }
    public int? Priority { get; init; }
    public double? Confidence { get; init; }
}
