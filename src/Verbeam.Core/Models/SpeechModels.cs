namespace Verbeam.Core.Models;

public sealed record SpeechRequest
{
    public string? AudioBase64 { get; init; }
    public string? AudioMimeType { get; init; }
    public string? SourceUrl { get; init; }
    public string? Provider { get; init; }
    public string? Language { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public string? Glossary { get; init; }
    public bool? PreferCaptions { get; init; }
}

public sealed record SpeechTranslateRequest
{
    public string? AudioBase64 { get; init; }
    public string? AudioMimeType { get; init; }
    public string? SourceUrl { get; init; }
    public string? SpeechProvider { get; init; }
    public string? Language { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public bool? PreferCaptions { get; init; }

    public string? Target { get; init; }
    public string? Source { get; init; }
    public string? Mode { get; init; }
    public string? Glossary { get; init; }
    public string? TranslationProvider { get; init; }
    public string? Model { get; init; }
}

public sealed record SpeechProviderDescriptor(
    string Name,
    string DisplayName,
    string Kind,
    string DefaultLanguage,
    bool RequiresExternalProcess,
    bool IsLocal);

public sealed record SpeechEngineDescriptor(
    string Name,
    string DisplayName,
    string Kind,
    string DefaultLanguage,
    bool IsAvailable,
    bool IsDefault,
    bool RequiresExternalProcess,
    bool IsLocal,
    string Source,
    string Note);

public sealed record SpeechProviderRequest(
    byte[] AudioBytes,
    string AudioMimeType,
    string Language,
    string? SourceUri,
    IReadOnlyDictionary<string, string> Hotwords);

public sealed record SpeechProviderResult(
    string Text,
    IReadOnlyList<SpeechSegment> Segments,
    string Engine);

public sealed record SpeechAudioChunk(
    int Index,
    double StartSeconds,
    byte[] AudioBytes);

public sealed record SpeechSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    string Text,
    double Confidence,
    string? Speaker,
    string? Language);

public sealed record SpeechResponse(
    string EventId,
    string Text,
    IReadOnlyList<SpeechSegment> Segments,
    string Provider,
    string Engine,
    string Language,
    string SourceKind,
    string SourceUri,
    string AudioMimeType,
    bool CaptionsUsed,
    long LatencyMs);

public sealed record SpeechTranslatedSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    string SourceText,
    string TranslatedText,
    string ErrorCode,
    string ErrorMessage,
    string Provider,
    string Engine,
    long LatencyMs,
    bool CacheHit);

public sealed record SpeechTranslateResponse(
    SpeechResponse Speech,
    IReadOnlyList<SpeechTranslatedSegment> Translations);

public sealed record SpeechJobRequest
{
    public string? AudioBase64 { get; init; }
    public string? AudioMimeType { get; init; }
    public string? SourceUrl { get; init; }
    public string? SpeechProvider { get; init; }
    public string? Provider { get; init; }
    public string? Language { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public string? Glossary { get; init; }
    public bool? PreferCaptions { get; init; }
    public bool Translate { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }
    public string? Mode { get; init; }
    public string? TranslationProvider { get; init; }
    public string? Model { get; init; }
}

public sealed record SpeechJobStatus(
    string Id,
    string Status,
    string ProfileId,
    string SessionId,
    string SourceKind,
    string SourceUri,
    string Language,
    string Provider,
    string Engine,
    bool CaptionsUsed,
    int SegmentCount,
    double Progress,
    string ResultEventId,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt);

public sealed record SpeechJobEvent(
    long Sequence,
    string JobId,
    string Type,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public sealed record SpeechEvent(
    string Id,
    string ProfileId,
    string SessionId,
    string SourceKind,
    string SourceUri,
    string AudioHash,
    string AudioMimeType,
    string Language,
    string Provider,
    string Engine,
    string Text,
    IReadOnlyList<SpeechSegment> Segments,
    bool CaptionsUsed,
    long LatencyMs,
    DateTimeOffset CreatedAt);
