using Verbeam.Core.Options;

namespace Verbeam.Core.Storage;

/// <summary>
/// Default <see cref="IDatabaseRouter"/>. Maps each <see cref="DbDomain"/> to a file
/// under a single data directory, placing game-scoped domains in
/// <c>{GamesSubdirectory}/{gameId}/</c>. Per-domain "follows the game axis" decisions
/// live in <see cref="DatabaseRoutingOptions"/> so a domain can be flipped between the
/// shared function file and per-game files without touching this router.
/// </summary>
public sealed class DatabaseRouter : IDatabaseRouter
{
    /// <summary>Game scope used when a request carries no profile/game id.</summary>
    public const string DefaultGameId = "default";

    private readonly string _dataDirectory;
    private readonly DatabaseRoutingOptions _options;

    public DatabaseRouter(string dataDirectory, DatabaseRoutingOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _options = options;
    }

    public string ResolvePath(DbDomain domain, string? gameId = null) => domain switch
    {
        DbDomain.Global => Fixed(_options.CoreFile),
        DbDomain.OcrCache => Fixed(_options.OcrCacheFile),
        DbDomain.Realtime => GameScoped(_options.RealtimeFile, gameId),
        DbDomain.Document => Flexible(_options.DocumentFile, _options.DocumentPerGame, gameId),
        DbDomain.Speech => Flexible(_options.SpeechFile, _options.SpeechPerGame, gameId),
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unknown database domain."),
    };

    private string Fixed(string fileName)
        => Path.GetFullPath(Path.Combine(_dataDirectory, fileName));

    private string Flexible(string fileName, bool perGame, string? gameId)
        => perGame ? GameScoped(fileName, gameId) : Fixed(fileName);

    private string GameScoped(string fileName, string? gameId)
        => Path.GetFullPath(Path.Combine(
            _dataDirectory,
            _options.GamesSubdirectory,
            SanitizeGameId(gameId),
            fileName));

    /// <summary>
    /// Reduces a game id to a single filesystem-safe path segment. Any separator or
    /// otherwise-invalid filename char becomes '_', so a hostile id can never traverse
    /// outside the games directory. gameIds are slugs/GUIDs in practice, so collisions
    /// from this mapping are not a concern.
    /// </summary>
    private static string SanitizeGameId(string? gameId)
    {
        var trimmed = gameId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DefaultGameId;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(trimmed.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return safe is "." or ".." ? "_" + safe : safe;
    }
}
