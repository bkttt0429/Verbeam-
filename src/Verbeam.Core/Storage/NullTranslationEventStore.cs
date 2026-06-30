using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class NullTranslationEventStore : ITranslationEventStore
{
    public static readonly NullTranslationEventStore Instance = new();

    private NullTranslationEventStore()
    {
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task AddEventAsync(TranslationEvent entry, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<TranslationEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<TranslationEvent?>(null);

    public Task<IReadOnlyList<TranslationEvent>> ListEventsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TranslationEvent>>([]);

    public Task<IReadOnlyList<TranslationEvent>> ListRecentContextAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        string excludeSourceText,
        int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TranslationEvent>>([]);

    public Task<IReadOnlyList<TranslationEvent>> ListSessionSuccessEventsAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TranslationEvent>>([]);
}
