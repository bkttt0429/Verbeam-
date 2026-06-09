namespace LocalTranslateHub.Core.Models;

public sealed record PromptPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = "1";
    public required string SystemPrompt { get; init; }
    public required string UserTemplate { get; init; }
}

public sealed record PromptPresetSummary(string Id, string Name, string Description, string Version);
