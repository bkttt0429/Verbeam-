using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface ISpeechEventStore
{
    Task AddEventAsync(SpeechEvent entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SpeechEvent>> ListEventsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);
}
