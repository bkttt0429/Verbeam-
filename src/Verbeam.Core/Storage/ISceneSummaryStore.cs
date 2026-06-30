using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface ISceneSummaryStore : IInitializableStore
{
    Task<SceneSummary> AddOrUpdateAsync(
        SceneSummaryUpsertRequest request,
        CancellationToken cancellationToken = default);

    Task<SceneSummary?> GetLatestAsync(
        string profileId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SceneSummary>> ListAsync(
        string profileId,
        string? sessionId,
        int limit,
        CancellationToken cancellationToken = default);
}
