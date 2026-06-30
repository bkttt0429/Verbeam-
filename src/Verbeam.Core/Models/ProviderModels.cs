namespace Verbeam.Core.Models;

public sealed record ProviderDescriptor(
    string Name,
    string DisplayName,
    string Kind,
    string DefaultModel,
    bool RequiresNetwork,
    bool IsLocal);

public sealed record TranslationModelDescriptor(
    string Provider,
    string Name,
    string DisplayName,
    bool IsDefault,
    bool IsInstalled,
    string Source,
    bool IsRecommended = false,
    string RecommendationReason = "",
    string RecommendedUse = "",
    string SupplierId = "",
    string SupplierName = "");

public sealed record TranslationLanguageDescriptor(
    string Code,
    string DisplayName,
    string NativeName,
    string PromptName,
    bool IsDefaultSource,
    bool IsDefaultTarget,
    bool IsOcrSupported,
    bool IsSpeechSupported);

public sealed record ProviderTranslationRequest(
    string Text,
    string Source,
    string Target,
    string Mode,
    string Model,
    PromptPreset Preset,
    IReadOnlyDictionary<string, string> GlossaryTerms,
    string Context,
    string MemoryContext = "");

public sealed record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    string Source,
    bool IsEstimated)
{
    public static TokenUsage Zero(string source)
        => new(0, 0, 0, source, IsEstimated: false);
}

public sealed record ProviderTranslationResult(string Text, string Engine)
{
    public IReadOnlyDictionary<string, double> Timings { get; init; } = new Dictionary<string, double>();

    public TokenUsage? TokenUsage { get; init; }
}

public sealed record TranslationTraceStage(
    string Name,
    long DurationMs,
    long StartedOffsetMs);

public sealed record TranslationPerformanceTrace(
    string TraceId,
    string ItemId,
    string? ChunkId,
    string Provider,
    string Model,
    string Engine,
    int TextCharacters,
    bool CacheHit,
    long TotalLatencyMs,
    IReadOnlyList<TranslationTraceStage> Stages,
    IReadOnlyDictionary<string, double> ProviderTimings,
    IReadOnlyDictionary<string, long> ClientUnixMs,
    IReadOnlyList<TranslationPerformanceTrace> Children);

/// <summary>
/// One item from a streaming translation: either an incremental token (<see cref="Delta"/>)
/// or the terminal item carrying the assembled <see cref="Final"/> result. Providers emit
/// zero or more delta chunks followed by exactly one final chunk.
/// </summary>
public sealed record ProviderStreamChunk(string Delta, ProviderTranslationResult? Final);

public sealed record TranslationOutcome(
    bool IsSuccess,
    string Text,
    string Engine,
    long LatencyMs,
    bool CacheHit,
    string ErrorCode,
    string ErrorMessage,
    TokenUsage? TokenUsage = null,
    TranslationPerformanceTrace? PerformanceTrace = null)
{
    public static TranslationOutcome Success(
        string text,
        string engine,
        long latencyMs,
        bool cacheHit,
        TokenUsage? tokenUsage = null,
        TranslationPerformanceTrace? performanceTrace = null)
        => new(true, text, engine, latencyMs, cacheHit, "0", string.Empty, tokenUsage, performanceTrace);

    public static TranslationOutcome Failure(
        string fallbackText,
        string errorMessage,
        string errorCode = "1",
        TranslationPerformanceTrace? performanceTrace = null)
        => new(false, fallbackText, string.Empty, 0, false, errorCode, errorMessage, PerformanceTrace: performanceTrace);
}

public sealed record TranslationEvent(
    string Id,
    string SessionId,
    string ProfileId,
    string? TranslationKey,
    string RequestName,
    string SourceText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    string Mode,
    string Provider,
    string GlossaryId,
    string GlossaryHash,
    string Engine,
    string Model,
    long LatencyMs,
    bool CacheHit,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset CreatedAt)
{
    /// <summary>Persisted token usage for usage analytics (0 when unknown / not reported).</summary>
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens { get; init; }
    public string TokenSource { get; init; } = string.Empty;
    public bool TokenEstimated { get; init; }
    /// <summary>App surface code (see <see cref="TranslationSurface"/>); 0 = unknown.</summary>
    public int Surface { get; init; }
}

/// <summary>
/// Which app surface a translation originated from — the "feature source" axis, kept separate from
/// <c>mode</c> (which is the prompt/style preset like subtitle/technical). Stored as a stable integer
/// code in <c>translation_events.surface</c>; never renumber existing values. 0 = unknown is the
/// catch-all so an unlabeled path is still counted, just uncategorized.
/// </summary>
public static class TranslationSurface
{
    public const int Unknown = 0;
    public const int Text = 1;
    public const int Region = 2;
    public const int Ocr = 3;
    public const int Audio = 4;
    public const int Document = 5;

    /// <summary>Maps a request-supplied surface string to its stable code (defaults to Unknown).</summary>
    public static int FromString(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "text" => Text,
            "region" => Region,
            "ocr" => Ocr,
            "audio" => Audio,
            "document" => Document,
            _ => Unknown
        };

    public static string ToKey(int code)
        => code switch
        {
            Text => "text",
            Region => "region",
            Ocr => "ocr",
            Audio => "audio",
            Document => "document",
            _ => "unknown"
        };
}

/// <summary>Aggregated token usage over a time range, grouped by provider+model.</summary>
public sealed record TokenUsageBreakdown(
    string Provider,
    string Model,
    long Requests,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);

/// <summary>Aggregated token usage grouped by app surface (feature source).</summary>
public sealed record TokenUsageSurfaceBreakdown(
    string Surface,
    long Requests,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);

/// <summary>One bucket (day) of the usage trend.</summary>
public sealed record TokenUsageDailyPoint(
    string Date,
    long Requests,
    long TotalTokens);

/// <summary>Full usage summary for the settings dashboard.</summary>
public sealed record TokenUsageSummary(
    string ProfileId,
    string Range,
    long TotalRequests,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalTokens,
    IReadOnlyList<TokenUsageBreakdown> ByProvider,
    IReadOnlyList<TokenUsageDailyPoint> Daily)
{
    /// <summary>Usage grouped by app surface (feature source). Empty on non-persistent stores.</summary>
    public IReadOnlyList<TokenUsageSurfaceBreakdown> BySurface { get; init; } = [];
}
