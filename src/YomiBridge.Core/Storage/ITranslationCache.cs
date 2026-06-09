namespace YomiBridge.Core.Storage;

public interface ITranslationCache
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<CachedTranslation?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(CachedTranslation entry, CancellationToken cancellationToken = default);
}

public sealed record CachedTranslation(
    string Key,
    string SourceText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    string Mode,
    string Provider,
    string Engine,
    string Model,
    string PresetVersion,
    string GlossaryHash,
    long LatencyMs,
    DateTimeOffset CreatedAt);
