using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class NullSceneSummaryStore : ISceneSummaryStore
{
    public static readonly NullSceneSummaryStore Instance = new();

    private NullSceneSummaryStore()
    {
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<SceneSummary> AddOrUpdateAsync(
        SceneSummaryUpsertRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SceneSummary?> GetLatestAsync(
        string profileId,
        string sessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<SceneSummary?>(null);

    public Task<IReadOnlyList<SceneSummary>> ListAsync(
        string profileId,
        string? sessionId,
        int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SceneSummary>>([]);
}
