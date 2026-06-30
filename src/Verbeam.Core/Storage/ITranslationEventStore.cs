using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface ITranslationEventStore : IInitializableStore
{
    Task AddEventAsync(TranslationEvent entry, CancellationToken cancellationToken = default);

    async Task AddEventsAsync(IReadOnlyList<TranslationEvent> entries, CancellationToken cancellationToken = default)
    {
        foreach (var entry in entries)
        {
            await AddEventAsync(entry, cancellationToken);
        }
    }

    Task<TranslationEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranslationEvent>> ListEventsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranslationEvent>> ListRecentContextAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        string excludeSourceText,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranslationEvent>> ListSessionSuccessEventsAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate token usage for the usage dashboard. <paramref name="sinceUtc"/> null = all time.
    /// Default returns an empty summary so non-persistent stores stay valid.
    /// </summary>
    Task<TokenUsageSummary> GetUsageSummaryAsync(
        string profileId,
        string range,
        DateTimeOffset? sinceUtc,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TokenUsageSummary(profileId, range, 0, 0, 0, 0, [], []));
}
