namespace Verbeam.Core.Models;

public sealed record SceneSummary(
    string Id,
    string SessionId,
    string ProfileId,
    string SummaryText,
    string StartEventId,
    string EndEventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SceneSummaryUpsertRequest
{
    public string? Id { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public string? SummaryText { get; init; }
    public string? StartEventId { get; init; }
    public string? EndEventId { get; init; }
}

public sealed record SceneSummaryDebugItem(
    string Id,
    string SummaryText,
    string StartEventId,
    string EndEventId,
    string SnippetHash,
    DateTimeOffset UpdatedAt);
