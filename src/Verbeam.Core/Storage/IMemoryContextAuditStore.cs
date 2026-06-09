using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IMemoryContextAuditStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task AddEntriesAsync(
        IReadOnlyList<MemoryContextAuditEntry> entries,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryContextAuditEntry>> ListAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);
}
