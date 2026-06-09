using Verbeam.Core.Models;
using Microsoft.Data.Sqlite;

namespace Verbeam.Core.Storage;

public sealed class SqliteMemoryContextAuditStore : IMemoryContextAuditStore
{
    private readonly string _databasePath;

    public SqliteMemoryContextAuditStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteDatabase.EnsureInitializedAsync(_databasePath, cancellationToken);
    }

    public async Task AddEntriesAsync(
        IReadOnlyList<MemoryContextAuditEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        foreach (var entry in entries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO rag_context_audit (
                    id, request_id, profile_id, session_id, translation_key,
                    memory_id, memory_kind, snippet_hash, context_hash, trust_level,
                    source_hash, policy_version, created_at
                )
                VALUES (
                    $id, $request_id, $profile_id, $session_id, $translation_key,
                    $memory_id, $memory_kind, $snippet_hash, $context_hash, $trust_level,
                    $source_hash, $policy_version, $created_at
                )
                """;
            AddParameters(command, entry);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MemoryContextAuditEntry>> ListAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await SqliteDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, request_id, profile_id, session_id, translation_key,
                   memory_id, memory_kind, snippet_hash, context_hash, trust_level,
                   source_hash, policy_version, created_at
            FROM rag_context_audit
            WHERE profile_id = $profile_id
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var values = new List<MemoryContextAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadEntry(reader));
        }

        return values;
    }

    private static void AddParameters(SqliteCommand command, MemoryContextAuditEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$request_id", entry.RequestId);
        command.Parameters.AddWithValue("$profile_id", entry.ProfileId);
        command.Parameters.AddWithValue("$session_id", entry.SessionId);
        command.Parameters.AddWithValue("$translation_key", (object?)entry.TranslationKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$memory_id", entry.MemoryId);
        command.Parameters.AddWithValue("$memory_kind", entry.MemoryKind);
        command.Parameters.AddWithValue("$snippet_hash", entry.SnippetHash);
        command.Parameters.AddWithValue("$context_hash", entry.ContextHash);
        command.Parameters.AddWithValue("$trust_level", entry.TrustLevel);
        command.Parameters.AddWithValue("$source_hash", entry.SourceHash);
        command.Parameters.AddWithValue("$policy_version", entry.PolicyVersion);
        command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));
    }

    private static MemoryContextAuditEntry ReadEntry(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            DateTimeOffset.Parse(reader.GetString(12)));
}
