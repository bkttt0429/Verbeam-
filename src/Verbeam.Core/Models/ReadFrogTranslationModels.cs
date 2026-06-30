namespace Verbeam.Core.Models;

public sealed record ReadFrogTranslateRequest
{
    public string? Name { get; init; }
    public string? Text { get; init; }
    public ReadFrogLangConfig? LangConfig { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }
    public string? Mode { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Glossary { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public string? Context { get; init; }
    public IReadOnlyList<string>? ContextItems { get; init; }
    public string? WebTitle { get; init; }
    public string? WebContent { get; init; }
    public string? WebSummary { get; init; }
    public bool? SkipMemoryContext { get; init; }
    public string? TraceId { get; init; }
    public string? ItemId { get; init; }
    public string? ChunkId { get; init; }
    public long? ClientQueuedAtUnixMs { get; init; }
    public long? ClientRequestStartedAtUnixMs { get; init; }
    public long? BackgroundReceivedAtUnixMs { get; init; }
    public long? BackgroundFetchStartedAtUnixMs { get; init; }
}

public sealed record ReadFrogLangConfig
{
    public string? SourceCode { get; init; }
    public string? TargetCode { get; init; }
    public string? Level { get; init; }
}

public sealed record ReadFrogTranslateResponse(
    string Result,
    string ErrorCode,
    string ErrorMessage,
    string Engine,
    long LatencyMs,
    bool CacheHit,
    TokenUsage? TokenUsage = null,
    TranslationPerformanceTrace? PerformanceTrace = null)
{
    public static ReadFrogTranslateResponse Success(TranslationOutcome outcome)
        => new(
            outcome.Text,
            outcome.ErrorCode,
            outcome.ErrorMessage,
            outcome.Engine,
            outcome.LatencyMs,
            outcome.CacheHit,
            outcome.TokenUsage,
            outcome.PerformanceTrace);

    public static ReadFrogTranslateResponse Error(
        string fallbackResult,
        string errorMessage,
        string errorCode = "1")
        => new(fallbackResult, errorCode, errorMessage, string.Empty, 0, CacheHit: false);
}

public sealed record ReadFrogTranslationOutcome(
    MortTranslateRequest Request,
    TranslationOutcome Outcome);
