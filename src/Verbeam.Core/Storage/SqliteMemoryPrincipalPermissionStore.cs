using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public sealed class SqliteMemoryPrincipalPermissionStore : IMemoryPrincipalPermissionStore
{
    private readonly string _databasePath;

    public SqliteMemoryPrincipalPermissionStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryPrincipalPermission>> ListAsync(
        string? profileId = null,
        string? principalId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            conditions.Add("profile_id = $profile_id");
            command.Parameters.AddWithValue("$profile_id", profileId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(principalId))
        {
            conditions.Add("principal_id = $principal_id");
            command.Parameters.AddWithValue("$principal_id", principalId.Trim());
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.CommandText = $$"""
            SELECT principal_id, profile_id, role, can_read_shared_memory, can_write_memory, can_approve_memory, created_at, updated_at
            FROM memory_principal_permissions
            {{(conditions.Count == 0 ? string.Empty : "WHERE " + string.Join("\n              AND ", conditions))}}
            ORDER BY profile_id, principal_id
            LIMIT $limit
            """;

        var permissions = new List<MemoryPrincipalPermission>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            permissions.Add(ReadPermission(reader));
        }

        return permissions;
    }

    public async Task<MemoryPrincipalPermission?> GetAsync(
        string principalId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(principalId) || string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        return await GetAsync(connection, principalId.Trim(), profileId.Trim(), cancellationToken);
    }

    public async Task<MemoryPrincipalPermission> UpsertAsync(
        MemoryPrincipalPermissionUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var principalId = Pick(request.Principal, request.PrincipalId, "principal");
        var profileId = Pick(request.Profile, request.ProfileId, "profile");
        var now = DateTimeOffset.UtcNow;

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await EnsureProfileAsync(connection, profileId, now, cancellationToken);
        var existing = await GetAsync(connection, principalId, profileId, cancellationToken);
        var requestedRole = string.IsNullOrWhiteSpace(request.Role) ? null : MemoryPrincipalRoles.Normalize(request.Role);
        var hasExplicitPermissions =
            request.CanReadSharedMemory is not null ||
            request.CanWriteMemory is not null ||
            request.CanApproveMemory is not null;

        bool canReadSharedMemory;
        bool canWriteMemory;
        bool canApproveMemory;
        if (!(requestedRole is not null &&
              !hasExplicitPermissions &&
              MemoryPrincipalRoles.TryGetPreset(requestedRole, out canReadSharedMemory, out canWriteMemory, out canApproveMemory)))
        {
            canReadSharedMemory = request.CanReadSharedMemory ?? existing?.CanReadSharedMemory ?? true;
            canWriteMemory = request.CanWriteMemory ?? existing?.CanWriteMemory ?? false;
            canApproveMemory = request.CanApproveMemory ?? existing?.CanApproveMemory ?? false;
        }

        var role = ResolveStoredRole(
            requestedRole,
            existing,
            hasExplicitPermissions,
            canReadSharedMemory,
            canWriteMemory,
            canApproveMemory);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_principal_permissions (
                principal_id,
                profile_id,
                role,
                can_read_shared_memory,
                can_write_memory,
                can_approve_memory,
                created_at,
                updated_at
            )
            VALUES (
                $principal_id,
                $profile_id,
                $role,
                $can_read_shared_memory,
                $can_write_memory,
                $can_approve_memory,
                $created_at,
                $updated_at
            )
            ON CONFLICT(principal_id, profile_id) DO UPDATE SET
                role = excluded.role,
                can_read_shared_memory = excluded.can_read_shared_memory,
                can_write_memory = excluded.can_write_memory,
                can_approve_memory = excluded.can_approve_memory,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$principal_id", principalId);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$can_read_shared_memory", canReadSharedMemory ? 1 : 0);
        command.Parameters.AddWithValue("$can_write_memory", canWriteMemory ? 1 : 0);
        command.Parameters.AddWithValue("$can_approve_memory", canApproveMemory ? 1 : 0);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetAsync(connection, principalId, profileId, cancellationToken)
            ?? throw new InvalidOperationException("Memory principal permission was not stored.");
    }

    public async Task<bool> DeleteAsync(
        string principalId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(principalId) || string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM memory_principal_permissions
            WHERE principal_id = $principal_id
              AND profile_id = $profile_id
            """;
        command.Parameters.AddWithValue("$principal_id", principalId.Trim());
        command.Parameters.AddWithValue("$profile_id", profileId.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> DeletePrincipalAsync(
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
            DELETE FROM memory_principal_permissions
            WHERE principal_id = $principal_id
            """;
        command.Parameters.AddWithValue("$principal_id", principalId.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<MemoryPrincipalPermission?> GetAsync(
        SqliteConnection connection,
        string principalId,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT principal_id, profile_id, role, can_read_shared_memory, can_write_memory, can_approve_memory, created_at, updated_at
            FROM memory_principal_permissions
            WHERE principal_id = $principal_id
              AND profile_id = $profile_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$principal_id", principalId);
        command.Parameters.AddWithValue("$profile_id", profileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPermission(reader) : null;
    }

    private static async Task EnsureProfileAsync(
        SqliteConnection connection,
        string profileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO profiles (
                id,
                display_name,
                source_language,
                target_language,
                default_mode,
                default_provider,
                created_at,
                updated_at
            )
            VALUES (
                $id,
                $display_name,
                'ja',
                'zh-TW',
                '',
                '',
                $created_at,
                $updated_at
            )
            """;
        command.Parameters.AddWithValue("$id", profileId);
        command.Parameters.AddWithValue("$display_name", profileId);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MemoryPrincipalPermission ReadPermission(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            MemoryPrincipalRoles.Normalize(reader.GetString(2)),
            reader.GetInt32(3) == 1,
            reader.GetInt32(4) == 1,
            reader.GetInt32(5) == 1,
            DateTimeOffset.Parse(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7)));

    private static string ResolveStoredRole(
        string? requestedRole,
        MemoryPrincipalPermission? existing,
        bool hasExplicitPermissions,
        bool canReadSharedMemory,
        bool canWriteMemory,
        bool canApproveMemory)
    {
        if (requestedRole is null)
        {
            return existing is not null && !hasExplicitPermissions
                ? existing.Role
                : MemoryPrincipalRoles.Infer(canReadSharedMemory, canWriteMemory, canApproveMemory);
        }

        if (requestedRole == MemoryPrincipalRoles.Custom)
        {
            return MemoryPrincipalRoles.Custom;
        }

        var inferred = MemoryPrincipalRoles.Infer(canReadSharedMemory, canWriteMemory, canApproveMemory);
        return inferred == requestedRole ? requestedRole : MemoryPrincipalRoles.Custom;
    }

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
