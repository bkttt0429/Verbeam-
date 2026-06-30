namespace Verbeam.Core.Storage;

/// <summary>
/// A store backed by a single SQLite file that can lazily create its schema. Every
/// Sqlite*Store already exposes this exact signature (delegating to
/// <see cref="SqliteDatabase.EnsureInitializedAsync"/>); the marker lets generic
/// plumbing such as <see cref="SqliteStoreCache{T}"/> initialize any of them uniformly.
/// </summary>
public interface IInitializableStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
