using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface ITranslationEventStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task AddEventAsync(TranslationEvent entry, CancellationToken cancellationToken = default);

    Task<TranslationEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranslationEvent>> ListEventsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);
}
