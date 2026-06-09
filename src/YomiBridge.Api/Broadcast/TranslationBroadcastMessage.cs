namespace YomiBridge.Api.Broadcast;

public sealed record TranslationBroadcastMessage(
    string Type,
    string SourceText,
    string TranslatedText,
    string Source,
    string Target,
    string Mode,
    string Provider,
    string? Glossary,
    string Engine,
    long LatencyMs,
    bool CacheHit,
    DateTimeOffset CreatedAt);
