using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IMemoryContextAuditStore : IInitializableStore
{
    Task AddEntriesAsync(
        IReadOnlyList<MemoryContextAuditEntry> entries,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryContextAuditEntry>> ListAsync(
        string profileId,
        int limit,
        string? principalId = null,
        CancellationToken cancellationToken = default);
}
