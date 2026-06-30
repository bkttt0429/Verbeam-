namespace Verbeam.Core.Storage;

/// <summary>
/// Resolves a (function-domain, game-scope) pair to a physical SQLite file path.
/// Pure path math — directory and schema creation happen lazily when a store first
/// initializes that path (see <see cref="SqliteDatabase.EnsureInitializedAsync"/>).
/// </summary>
public interface IDatabaseRouter
{
    /// <summary>Resolves the absolute database file path for a domain.</summary>
    /// <param name="domain">Function axis (which logical dataset).</param>
    /// <param name="gameId">Game/profile id for game-scoped domains; ignored for fixed
    /// global/function files. Null or blank resolves to the "default" game.</param>
    string ResolvePath(DbDomain domain, string? gameId = null);
}
