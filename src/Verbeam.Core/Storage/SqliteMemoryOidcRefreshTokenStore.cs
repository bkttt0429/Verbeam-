using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class SqliteMemoryOidcRefreshTokenStore : IMemoryOidcRefreshTokenStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _databasePath;
    private readonly byte[] _key;

    public SqliteMemoryOidcRefreshTokenStore(string databasePath, string protectionKey)
    {
        if (string.IsNullOrWhiteSpace(protectionKey))
        {
            throw new ArgumentException("OIDC refresh-token protection key is required.", nameof(protectionKey));
        }

        _databasePath = databasePath;
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(protectionKey.Trim()));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryOidcRefreshTokenHandle>> ListAsync(
        string? principalId = null,
        bool includeRevoked = false,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (!includeRevoked)
        {
            conditions.Add("revoked_at IS NULL");
        }

        if (!string.IsNullOrWhiteSpace(principalId))
        {
            conditions.Add("principal_id = $principal_id");
            command.Parameters.AddWithValue("$principal_id", principalId.Trim());
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.CommandText = $$"""
            SELECT id, principal_id, created_at, updated_at, expires_at, revoked_at, last_used_at
            FROM memory_oidc_refresh_tokens
            {{(conditions.Count == 0 ? string.Empty : "WHERE " + string.Join("\n              AND ", conditions))}}
            ORDER BY updated_at DESC
            LIMIT $limit
            """;

        var handles = new List<MemoryOidcRefreshTokenHandle>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            handles.Add(ReadHandle(reader));
        }

        return handles;
    }

    public async Task<string> StoreAsync(
        string principalId,
        string refreshToken,
        DateTimeOffset? expiresAt = null,
        string? handle = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            throw new ArgumentException("principal is required.", nameof(principalId));
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("refreshToken is required.", nameof(refreshToken));
        }

        var id = string.IsNullOrWhiteSpace(handle) ? Guid.NewGuid().ToString("N") : handle.Trim();
        var principal = principalId.Trim();
        var now = DateTimeOffset.UtcNow;
        var encrypted = Encrypt(id, principal, refreshToken.Trim());

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_oidc_refresh_tokens (
                id,
                principal_id,
                nonce,
                tag,
                ciphertext,
                created_at,
                updated_at,
                expires_at,
                revoked_at,
                last_used_at
            )
            VALUES (
                $id,
                $principal_id,
                $nonce,
                $tag,
                $ciphertext,
                $created_at,
                $updated_at,
                $expires_at,
                NULL,
                NULL
            )
            ON CONFLICT(id) DO UPDATE SET
                principal_id = excluded.principal_id,
                nonce = excluded.nonce,
                tag = excluded.tag,
                ciphertext = excluded.ciphertext,
                updated_at = excluded.updated_at,
                expires_at = excluded.expires_at,
                revoked_at = NULL
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$principal_id", principal);
        command.Parameters.Add("$nonce", SqliteType.Blob).Value = encrypted.Nonce;
        command.Parameters.Add("$tag", SqliteType.Blob).Value = encrypted.Tag;
        command.Parameters.Add("$ciphertext", SqliteType.Blob).Value = encrypted.Ciphertext;
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        command.Parameters.AddWithValue("$expires_at", expiresAt?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return id;
    }

    public async Task<MemoryOidcStoredRefreshToken?> ResolveAsync(
        string handle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return null;
        }

        await InitializeAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT principal_id, nonce, tag, ciphertext
            FROM memory_oidc_refresh_tokens
            WHERE id = $id
              AND revoked_at IS NULL
              AND (expires_at IS NULL OR expires_at > $now)
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", handle.Trim());
        command.Parameters.AddWithValue("$now", now.ToString("O"));

        string? principalId = null;
        byte[]? nonce = null;
        byte[]? tag = null;
        byte[]? ciphertext = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                principalId = reader.GetString(0);
                nonce = (byte[])reader["nonce"];
                tag = (byte[])reader["tag"];
                ciphertext = (byte[])reader["ciphertext"];
            }
        }

        if (principalId is null || nonce is null || tag is null || ciphertext is null)
        {
            return null;
        }

        var refreshToken = Decrypt(handle.Trim(), principalId, nonce, tag, ciphertext);
        if (refreshToken is null)
        {
            return null;
        }

        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE memory_oidc_refresh_tokens
            SET last_used_at = $last_used_at
            WHERE id = $id
            """;
        updateCommand.Parameters.AddWithValue("$id", handle.Trim());
        updateCommand.Parameters.AddWithValue("$last_used_at", now.ToString("O"));
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        return new MemoryOidcStoredRefreshToken(handle.Trim(), principalId, refreshToken);
    }

    public async Task<bool> RevokeAsync(
        string handle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_oidc_refresh_tokens
            SET revoked_at = $revoked_at
            WHERE id = $id
              AND revoked_at IS NULL
            """;
        command.Parameters.AddWithValue("$id", handle.Trim());
        command.Parameters.AddWithValue("$revoked_at", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> RevokePrincipalAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return 0;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_oidc_refresh_tokens
            SET revoked_at = $revoked_at
            WHERE principal_id = $principal_id
              AND revoked_at IS NULL
            """;
        command.Parameters.AddWithValue("$principal_id", principalId.Trim());
        command.Parameters.AddWithValue("$revoked_at", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private EncryptedRefreshToken Encrypt(string handle, string principalId, string refreshToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(refreshToken);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(handle, principalId));
        Array.Clear(plaintext);
        return new EncryptedRefreshToken(nonce, tag, ciphertext);
    }

    private string? Decrypt(
        string handle,
        string principalId,
        byte[] nonce,
        byte[] tag,
        byte[] ciphertext)
    {
        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            return null;
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(handle, principalId));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    private static byte[] AssociatedData(string handle, string principalId)
        => Encoding.UTF8.GetBytes($"{handle}:{principalId}");

    private static MemoryOidcRefreshTokenHandle ReadHandle(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)));

    private sealed record EncryptedRefreshToken(byte[] Nonce, byte[] Tag, byte[] Ciphertext);
}
