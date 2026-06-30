using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IMemoryPrincipalSessionStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryPrincipalSession>> ListAsync(
        string? principalId = null,
        bool includeRevoked = false,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<MemoryPrincipalSessionCreateResult> CreateAsync(
        MemoryPrincipalSessionCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<string?> ResolvePrincipalAsync(
        string sessionToken,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<int> RevokePrincipalAsync(
        string principalId,
        CancellationToken cancellationToken = default);
}
