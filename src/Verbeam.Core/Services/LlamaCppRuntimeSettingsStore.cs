using System.Text.Json;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed record LlamaCppRuntimeSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Mode { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string BinaryFlavor { get; init; } = string.Empty;
    public string ComputeTarget { get; init; } = string.Empty;
    public string PinnedVersion { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Persists the llama.cpp runtime selection made through Install-and-Use so it
/// survives restarts. Without this the in-memory mode switch to "managed" is
/// lost and the provider silently reverts to remote mode (connection refused
/// on the default localhost endpoint).
/// </summary>
public sealed class LlamaCppRuntimeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LlamaCppRuntimeSettingsStore(string path)
    {
        _path = path;
    }

    public async Task<LlamaCppRuntimeSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<LlamaCppRuntimeSettings>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            // A corrupt settings file must not block startup; configured defaults apply.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LlamaCppOptions options, CancellationToken cancellationToken = default)
    {
        var settings = new LlamaCppRuntimeSettings
        {
            Mode = options.Mode,
            Model = options.Model,
            BinaryFlavor = options.BinaryFlavor,
            ComputeTarget = options.ComputeTarget,
            PinnedVersion = options.PinnedVersion,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _path + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
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
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Overrides the options with persisted values. Returns false when nothing valid is stored.</summary>
    public async Task<bool> ApplyAsync(LlamaCppOptions options, CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(cancellationToken);
        var mode = settings?.Mode.Trim().ToLowerInvariant();
        if (settings is null || mode is not ("remote" or "managed"))
        {
            return false;
        }

        options.Mode = mode;
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            options.Model = settings.Model.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.BinaryFlavor))
        {
            options.BinaryFlavor = settings.BinaryFlavor.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.ComputeTarget))
        {
            options.ComputeTarget = settings.ComputeTarget.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.PinnedVersion))
        {
            options.PinnedVersion = settings.PinnedVersion.Trim();
        }

        return true;
    }
}
