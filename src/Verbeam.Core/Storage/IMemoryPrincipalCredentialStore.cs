using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IMemoryPrincipalCredentialStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryPrincipalCredential>> ListAsync(
        string? principalId = null,
        bool includeRevoked = false,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<MemoryPrincipalCredentialCreateResult> CreateAsync(
        MemoryPrincipalCredentialCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<MemoryPrincipalCredential?> ValidateAsync(
        string principalId,
        string secret,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<int> RevokePrincipalAsync(
        string principalId,
        CancellationToken cancellationToken = default);
}
