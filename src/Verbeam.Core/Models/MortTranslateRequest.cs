namespace Verbeam.Core.Models;

public sealed record MortTranslateRequest
{
    public string? Name { get; init; }
    public string? Text { get; init; }
    public string? Target { get; init; }
    public string? Source { get; init; }
    public string? Mode { get; init; }
    /// <summary>App surface / feature source (text|region|ocr|audio|document); for usage analytics only.</summary>
    public string? Surface { get; init; }
    public string? Glossary { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public string? Context { get; init; }
    public IReadOnlyList<string>? ContextItems { get; init; }
    public bool? AllowSharedMemory { get; init; }
    public string? PrincipalId { get; init; }
    public bool? Realtime { get; init; }
    public string? BroadcastSourceKind { get; init; }
    public bool? SkipMemoryContext { get; init; }
    public string? TraceId { get; init; }
    public string? ItemId { get; init; }
    public string? ChunkId { get; init; }
    public long? ClientQueuedAtUnixMs { get; init; }
    public long? ClientRequestStartedAtUnixMs { get; init; }
    public long? BackgroundReceivedAtUnixMs { get; init; }
    public long? BackgroundFetchStartedAtUnixMs { get; init; }
}

public sealed record MortTranslateResponse(
    string Result,
    string ErrorCode,
    string ErrorMessage,
    TokenUsage? TokenUsage = null)
{
    public static MortTranslateResponse Success(string result, TokenUsage? tokenUsage = null)
        => new(result, "0", string.Empty, tokenUsage);

    public static MortTranslateResponse Error(string fallbackResult, string errorMessage, string errorCode = "1")
        => new(fallbackResult, errorCode, errorMessage);
}
