using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class OllamaRuntimeTests
{
    [Fact]
    public void BuildExecutableCandidates_UsesConfiguredPathPathAndKnownWindowsLocations()
    {
        var root = Path.Combine(Path.GetTempPath(), "verbeam-ollama-test-" + Guid.NewGuid().ToString("N"));
        var pathDirectory = Path.Combine(root, "path-bin");
        var localAppData = Path.Combine(root, "local-app-data");
        var programFiles = Path.Combine(root, "program-files");
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = pathDirectory,
            ["LOCALAPPDATA"] = localAppData,
            ["APPDATA"] = Path.Combine(root, "app-data"),
            ["ProgramFiles"] = programFiles
        };

        var candidates = OllamaRuntimeManager.BuildExecutableCandidates(
            root,
            Path.Combine("tools", "ollama.exe"),
            environment);

        Assert.Equal(Path.Combine(root, "tools", "ollama.exe"), candidates[0]);
        Assert.Contains(Path.Combine(pathDirectory, "ollama.exe"), candidates);
        Assert.Contains(Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe"), candidates);
        Assert.Contains(Path.Combine(localAppData, "Ollama", "ollama.exe"), candidates);
        Assert.Contains(Path.Combine(programFiles, "Ollama", "ollama.exe"), candidates);
    }
}
