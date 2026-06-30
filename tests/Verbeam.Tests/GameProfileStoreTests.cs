using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class GameProfileStoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "verbeam-gameprofile-tests-" + Guid.NewGuid());

    private string ProfilesPath => Path.Combine(_tempDirectory, "game-profiles.json");

    [Fact]
    public async Task MigratesLegacySingleRegionIntoRegionsList()
    {
        Directory.CreateDirectory(_tempDirectory);
        // A document saved before multi-region existed: only the single `region` field is set.
        await File.WriteAllTextAsync(
            ProfilesPath,
            """
            {
              "schemaVersion": 1,
              "profiles": [
                {
                  "id": "legacy",
                  "name": "Legacy Game",
                  "region": { "x": 0.1, "y": 0.2, "width": 0.3, "height": 0.4 }
                }
              ]
            }
            """);

        var profile = await new GameProfileStore(ProfilesPath).GetAsync("legacy");

        Assert.NotNull(profile);
        Assert.Single(profile!.Regions);
        Assert.Equal(0.3, profile.Regions[0].Width);
        // The legacy field stays populated; migration only fills the list.
        Assert.NotNull(profile.Region);
    }

    [Fact]
    public async Task DoesNotOverwriteAnExistingRegionsList()
    {
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(
            ProfilesPath,
            """
            {
              "profiles": [
                {
                  "id": "multi",
                  "name": "Multi",
                  "region": { "x": 0, "y": 0, "width": 0.1, "height": 0.1 },
                  "regions": [
                    { "x": 0.1, "y": 0.1, "width": 0.2, "height": 0.2 },
                    { "x": 0.5, "y": 0.5, "width": 0.2, "height": 0.2 }
                  ]
                }
              ]
            }
            """);

        var profile = await new GameProfileStore(ProfilesPath).GetAsync("multi");

        Assert.NotNull(profile);
        Assert.Equal(2, profile!.Regions.Count);
    }

    [Fact]
    public async Task UpsertRoundTripsMultiRegionAndSurfaceBinding()
    {
        var store = new GameProfileStore(ProfilesPath);
        var saved = await store.UpsertAsync(new GameProfile
        {
            Name = "Elden Ring",
            Regions =
            [
                new GameRegionRect(0.1, 0.8, 0.8, 0.15),
                new GameRegionRect(0.0, 0.0, 0.3, 0.1)
            ],
            Surface = new SurfaceBinding
            {
                Kind = "window",
                ProcessName = "eldenring",
                WindowTitlePattern = "ELDEN RING"
            },
            GlossaryId = "elden-ring"
        });

        Assert.False(string.IsNullOrWhiteSpace(saved.Id));

        // Re-open from disk to prove the new fields survive serialization.
        var reloaded = await new GameProfileStore(ProfilesPath).GetAsync(saved.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Regions.Count);
        Assert.NotNull(reloaded.Surface);
        Assert.Equal("window", reloaded.Surface!.Kind);
        Assert.Equal("eldenring", reloaded.Surface.ProcessName);
        Assert.Equal("elden-ring", reloaded.GlossaryId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
