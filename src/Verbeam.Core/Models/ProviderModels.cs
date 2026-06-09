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
    string RecommendedUse = "");

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

public sealed record ProviderTranslationResult(string Text, string Engine);

public sealed record TranslationOutcome(
    bool IsSuccess,
    string Text,
    string Engine,
    long LatencyMs,
    bool CacheHit,
    string ErrorCode,
    string ErrorMessage)
{
    public static TranslationOutcome Success(string text, string engine, long latencyMs, bool cacheHit)
        => new(true, text, engine, latencyMs, cacheHit, "0", string.Empty);

    public static TranslationOutcome Failure(string fallbackText, string errorMessage, string errorCode = "1")
        => new(false, fallbackText, string.Empty, 0, false, errorCode, errorMessage);
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
    DateTimeOffset CreatedAt);
