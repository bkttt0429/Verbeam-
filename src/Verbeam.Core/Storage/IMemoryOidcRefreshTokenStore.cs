using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed record MemoryOidcStoredRefreshToken(
    string Handle,
    string PrincipalId,
    string RefreshToken);

public interface IMemoryOidcRefreshTokenStore
{
    Task<IReadOnlyList<MemoryOidcRefreshTokenHandle>> ListAsync(
        string? principalId = null,
        bool includeRevoked = false,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<string> StoreAsync(
        string principalId,
        string refreshToken,
        DateTimeOffset? expiresAt = null,
        string? handle = null,
        CancellationToken cancellationToken = default);

    Task<MemoryOidcStoredRefreshToken?> ResolveAsync(
        string handle,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(
        string handle,
        CancellationToken cancellationToken = default);

    Task<int> RevokePrincipalAsync(
        string principalId,
        CancellationToken cancellationToken = default);
}
