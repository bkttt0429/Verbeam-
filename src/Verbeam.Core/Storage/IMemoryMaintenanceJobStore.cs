using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IMemoryMaintenanceJobStore : IInitializableStore
{
    Task<string> EnqueueAsync(
        string jobKind,
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryMaintenanceJob>> ListAsync(
        string? profileId = null,
        string? status = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(
        string? profileId = null,
        string? status = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryMaintenanceJob>> ClaimAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        string id,
        string errorMessage,
        int maxAttempts,
        CancellationToken cancellationToken = default);
}
