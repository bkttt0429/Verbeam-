using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IMemoryPrincipalPermissionStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryPrincipalPermission>> ListAsync(
        string? profileId = null,
        string? principalId = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<MemoryPrincipalPermission?> GetAsync(
        string principalId,
        string profileId,
        CancellationToken cancellationToken = default);

    Task<MemoryPrincipalPermission> UpsertAsync(
        MemoryPrincipalPermissionUpsertRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string principalId,
        string profileId,
        CancellationToken cancellationToken = default);

    Task<int> DeletePrincipalAsync(
        string principalId,
        CancellationToken cancellationToken = default);
}
