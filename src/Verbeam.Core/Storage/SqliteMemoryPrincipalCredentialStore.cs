using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class SqliteMemoryPrincipalCredentialStore : IMemoryPrincipalCredentialStore
{
    private readonly string _databasePath;

    public SqliteMemoryPrincipalCredentialStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryPrincipalCredential>> ListAsync(
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
            SELECT id, principal_id, label, created_at, expires_at, revoked_at, last_used_at
            FROM memory_principal_credentials
            {{(conditions.Count == 0 ? string.Empty : "WHERE " + string.Join("\n              AND ", conditions))}}
            ORDER BY created_at DESC
            LIMIT $limit
            """;

        var credentials = new List<MemoryPrincipalCredential>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            credentials.Add(ReadCredential(reader));
        }

        return credentials;
    }

    public async Task<MemoryPrincipalCredentialCreateResult> CreateAsync(
        MemoryPrincipalCredentialCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var principalId = Pick(request.Principal, request.PrincipalId, "principal");
        var secret = string.IsNullOrWhiteSpace(request.Secret) ? NewSecret() : request.Secret.Trim();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var secretHash = HashSecret(secret);

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_principal_credentials (
                id,
                principal_id,
                label,
                secret_hash,
                created_at,
                expires_at,
                revoked_at,
                last_used_at
            )
            VALUES (
                $id,
                $principal_id,
                $label,
                $secret_hash,
                $created_at,
                $expires_at,
                NULL,
                NULL
            )
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$principal_id", principalId);
        command.Parameters.AddWithValue("$label", request.Label?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$secret_hash", secretHash);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$expires_at", request.ExpiresAt?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var credential = await GetAsync(connection, id, cancellationToken)
            ?? throw new InvalidOperationException("Memory principal credential was not stored.");
        return new MemoryPrincipalCredentialCreateResult(credential, secret);
    }

    public async Task<MemoryPrincipalCredential?> ValidateAsync(
        string principalId,
        string secret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(principalId) || string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        await InitializeAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, principal_id, label, created_at, expires_at, revoked_at, last_used_at
            FROM memory_principal_credentials
            WHERE principal_id = $principal_id
              AND secret_hash = $secret_hash
              AND revoked_at IS NULL
              AND (expires_at IS NULL OR expires_at > $now)
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$principal_id", principalId.Trim());
        command.Parameters.AddWithValue("$secret_hash", HashSecret(secret.Trim()));
        command.Parameters.AddWithValue("$now", now.ToString("O"));

        MemoryPrincipalCredential? credential;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            credential = await reader.ReadAsync(cancellationToken) ? ReadCredential(reader) : null;
        }

        if (credential is null)
        {
            return null;
        }

        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE memory_principal_credentials
            SET last_used_at = $last_used_at
            WHERE id = $id
            """;
        updateCommand.Parameters.AddWithValue("$id", credential.Id);
        updateCommand.Parameters.AddWithValue("$last_used_at", now.ToString("O"));
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        return credential with { LastUsedAt = now };
    }

    public async Task<bool> RevokeAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_principal_credentials
            SET revoked_at = $revoked_at
            WHERE id = $id
              AND revoked_at IS NULL
            """;
        command.Parameters.AddWithValue("$id", id.Trim());
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
            UPDATE memory_principal_credentials
            SET revoked_at = $revoked_at
            WHERE principal_id = $principal_id
              AND revoked_at IS NULL
            """;
        command.Parameters.AddWithValue("$principal_id", principalId.Trim());
        command.Parameters.AddWithValue("$revoked_at", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<MemoryPrincipalCredential?> GetAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, principal_id, label, created_at, expires_at, revoked_at, last_used_at
            FROM memory_principal_credentials
            WHERE id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCredential(reader) : null;
    }

    private static MemoryPrincipalCredential ReadCredential(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)));

    private static string NewSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string HashSecret(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string Pick(string? first, string? second, string name)
    {
        var value = string.IsNullOrWhiteSpace(first) ? second : first;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.");
        }

        return value.Trim();
    }
}
