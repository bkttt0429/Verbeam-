using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace LocalTranslateHub.Core.Storage;

internal static class SqliteDatabase
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InitializationLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InitializedPaths = new(StringComparer.OrdinalIgnoreCase);

    public static async Task EnsureInitializedAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(databasePath);
        if (InitializedPaths.ContainsKey(normalizedPath))
        {
            return;
        }

        var gate = InitializationLocks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (InitializedPaths.ContainsKey(normalizedPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(normalizedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = await OpenConnectionAsync(normalizedPath, cancellationToken);
            await SqliteSchema.InitializeAsync(connection, cancellationToken);
            InitializedPaths.TryAdd(normalizedPath, 0);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(BuildConnectionString(databasePath));
        await connection.OpenAsync(cancellationToken);
        await ApplyConnectionPragmasAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task ApplyConnectionPragmasAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = NormalizePath(databasePath),
            Pooling = true
        };

        return builder.ConnectionString;
    }

    private static string NormalizePath(string databasePath)
        => Path.GetFullPath(databasePath);
}
