using System.Text.Json;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

/// <summary>
/// JSON-backed per-game profile store (region + OCR/translation settings), the single
/// source of truth shared by the workbench and (later) the tray. Mirrors
/// <see cref="TranslationRouteStore"/>'s gate + atomic-write pattern; a corrupt file
/// degrades to an empty document rather than blocking the app.
/// </summary>
public sealed class GameProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GameProfileStore(string path)
    {
        _path = path;
    }

    public async Task<GameProfilesDocument> GetDocumentAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadDocumentAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GameProfile?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Find((await GetDocumentAsync(cancellationToken)).Profiles, id);

    public async Task<GameProfile> UpsertAsync(GameProfile profile, CancellationToken cancellationToken = default)
    {
        Validate(profile);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var profiles = document.Profiles.ToList();
            var saved = profile with
            {
                Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim(),
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var index = profiles.FindIndex(item => item.Id.Equals(saved.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                profiles[index] = saved;
            }
            else
            {
                profiles.Add(saved);
            }

            await SaveDocumentAsync(document with { Profiles = profiles }, cancellationToken);
            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var key = (id ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var profiles = document.Profiles.ToList();
            if (profiles.RemoveAll(item => item.Id.Equals(key, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            // Clear the active pointer if it referenced the removed profile.
            var activeId = string.Equals(document.ActiveId, key, StringComparison.OrdinalIgnoreCase)
                ? null
                : document.ActiveId;
            await SaveDocumentAsync(document with { Profiles = profiles, ActiveId = activeId }, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GameProfile?> SetActiveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var match = Find(document.Profiles, id);
            if (match is null)
            {
                return null;
            }

            await SaveDocumentAsync(document with { ActiveId = match.Id }, cancellationToken);
            return match;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static GameProfile? Find(IReadOnlyList<GameProfile> profiles, string id)
    {
        var key = (id ?? string.Empty).Trim();
        return key.Length == 0
            ? null
            : profiles.FirstOrDefault(item => item.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Back-compat: profiles saved before multi-region carried a single <see cref="GameProfile.Region"/>.
    /// Promote it into <see cref="GameProfile.Regions"/> so the rest of the app only reads the list.
    /// Allocates a new list only when at least one profile actually needs migrating.
    /// </summary>
    private static GameProfilesDocument MigrateRegions(GameProfilesDocument document)
    {
        if (document.Profiles.Count == 0)
        {
            return document;
        }

        List<GameProfile>? migrated = null;
        for (var i = 0; i < document.Profiles.Count; i++)
        {
            var profile = document.Profiles[i];
            if (profile.Regions.Count == 0 && profile.Region is not null)
            {
                migrated ??= document.Profiles.ToList();
                migrated[i] = profile with { Regions = new[] { profile.Region } };
            }
        }

        return migrated is null ? document : document with { Profiles = migrated };
    }

    private async Task<GameProfilesDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new GameProfilesDocument();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<GameProfilesDocument>(stream, JsonOptions, cancellationToken);
            return MigrateRegions(document ?? new GameProfilesDocument());
        }
        catch (JsonException)
        {
            // A corrupt profiles file must not block the app; treat as empty.
            return new GameProfilesDocument();
        }
    }

    private async Task SaveDocumentAsync(GameProfilesDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        }

        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _path);
        }
    }

    private static void Validate(GameProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new InvalidOperationException("Game profile requires a name.");
        }
    }
}
