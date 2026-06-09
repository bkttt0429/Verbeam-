using System.Text.Json;
using YomiBridge.Core.Models;

namespace YomiBridge.Core.Services;

public sealed class PromptPresetStore
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public PromptPresetStore(string directory)
    {
        _directory = directory;
    }

    public async Task<IReadOnlyList<PromptPresetSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var presets = await LoadAllAsync(cancellationToken);
        return presets
            .Select(preset => new PromptPresetSummary(
                preset.Id,
                preset.Name,
                preset.Description,
                preset.Version))
            .OrderBy(preset => preset.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<PromptPreset> GetRequiredAsync(string id, CancellationToken cancellationToken = default)
    {
        var presets = await LoadAllAsync(cancellationToken);
        var preset = presets.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        return preset ?? throw new InvalidOperationException($"Unknown prompt preset '{id}'.");
    }

    private async Task<IReadOnlyList<PromptPreset>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            throw new DirectoryNotFoundException($"Preset directory not found: {_directory}");
        }

        var files = Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);

        var presets = new List<PromptPreset>();
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var preset = await JsonSerializer.DeserializeAsync<PromptPreset>(stream, _jsonOptions, cancellationToken);
            if (preset is null)
            {
                continue;
            }

            presets.Add(preset);
        }

        return presets;
    }
}
