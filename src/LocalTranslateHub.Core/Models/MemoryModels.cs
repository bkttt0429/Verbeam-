namespace LocalTranslateHub.Core.Models;

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
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUsedAt,
    int UseCount);

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
