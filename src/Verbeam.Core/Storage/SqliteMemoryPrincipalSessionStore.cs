using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class SqliteMemoryPrincipalSessionStore : IMemoryPrincipalSessionStore
{
    private readonly string _databasePath;

    public SqliteMemoryPrincipalSessionStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryPrincipalSession>> ListAsync(
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
            SELECT id, principal_id, created_at, expires_at, revoked_at, last_seen_at
            FROM memory_principal_sessions
            {{(conditions.Count == 0 ? string.Empty : "WHERE " + string.Join("\n              AND ", conditions))}}
            ORDER BY created_at DESC
            LIMIT $limit
            """;

        var sessions = new List<MemoryPrincipalSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public async Task<MemoryPrincipalSessionCreateResult> CreateAsync(
        MemoryPrincipalSessionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var principalId = Pick(request.Principal, request.PrincipalId, "principal");
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var token = NewSessionToken();
        var tokenHash = HashSessionToken(token);

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_principal_sessions (
                id,
                principal_id,
                token_hash,
                created_at,
                expires_at,
                revoked_at,
                last_seen_at
            )
            VALUES (
                $id,
                $principal_id,
                $token_hash,
                $created_at,
                $expires_at,
                NULL,
                NULL
            )
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$principal_id", principalId);
        command.Parameters.AddWithValue("$token_hash", tokenHash);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$expires_at", request.ExpiresAt?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var session = await GetAsync(connection, id, cancellationToken)
            ?? throw new InvalidOperationException("Memory principal session was not stored.");
        return new MemoryPrincipalSessionCreateResult(session, token);
    }

    public async Task<string?> ResolvePrincipalAsync(
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        await InitializeAsync(cancellationToken);

        var tokenHash = HashSessionToken(sessionToken.Trim());
        var now = DateTimeOffset.UtcNow;
        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, principal_id
            FROM memory_principal_sessions
            WHERE token_hash = $token_hash
              AND revoked_at IS NULL
              AND (expires_at IS NULL OR expires_at > $now)
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$token_hash", tokenHash);
        command.Parameters.AddWithValue("$now", now.ToString("O"));

        string? id = null;
        string? principalId = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetString(0);
                principalId = reader.GetString(1);
            }
        }

        if (id is null || principalId is null)
        {
            return null;
        }

        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE memory_principal_sessions
            SET last_seen_at = $last_seen_at
            WHERE id = $id
            """;
        updateCommand.Parameters.AddWithValue("$id", id);
        updateCommand.Parameters.AddWithValue("$last_seen_at", now.ToString("O"));
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        return principalId;
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
            UPDATE memory_principal_sessions
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
            UPDATE memory_principal_sessions
            SET revoked_at = $revoked_at
            WHERE principal_id = $principal_id
              AND revoked_at IS NULL
            """;
        command.Parameters.AddWithValue("$principal_id", principalId.Trim());
        command.Parameters.AddWithValue("$revoked_at", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<MemoryPrincipalSession?> GetAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, principal_id, created_at, expires_at, revoked_at, last_seen_at
            FROM memory_principal_sessions
            WHERE id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    private static MemoryPrincipalSession ReadSession(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)));

    private static string NewSessionToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string HashSessionToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

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
