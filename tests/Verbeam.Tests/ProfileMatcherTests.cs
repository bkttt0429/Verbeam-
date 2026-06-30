using Verbeam.Api.Tray;
using Verbeam.Core.Models;

namespace Verbeam.Tests;

public sealed class ProfileMatcherTests
{
    private static GameProfile WindowProfile(string id, string processName, string title = "")
        => new()
        {
            Id = id,
            Name = id,
            Surface = new SurfaceBinding { Kind = "window", ProcessName = processName, WindowTitlePattern = title }
        };

    [Fact]
    public void MatchesByProcessNameIgnoringExeAndCase()
    {
        var profiles = new[] { WindowProfile("er", "EldenRing") };

        Assert.Equal("er", ProfileMatcher.Match(profiles, "eldenring.exe", "ELDEN RING")?.Id);
        Assert.Equal("er", ProfileMatcher.Match(profiles, "eldenring", string.Empty)?.Id);
    }

    [Fact]
    public void RespectsTitlePatternWhenSet()
    {
        var profiles = new[] { WindowProfile("vn", "game", "Chapter \\d+") };

        Assert.NotNull(ProfileMatcher.Match(profiles, "game", "Chapter 3"));
        Assert.Null(ProfileMatcher.Match(profiles, "game", "Main Menu"));
    }

    [Fact]
    public void IgnoresNonWindowBindingsAndReturnsNullWhenNothingMatches()
    {
        var monitorBound = new GameProfile
        {
            Id = "mon",
            Name = "mon",
            Surface = new SurfaceBinding { Kind = "monitor", ProcessName = "game" }
        };

        Assert.Null(ProfileMatcher.Match(new[] { monitorBound }, "game", string.Empty));
        Assert.Null(ProfileMatcher.Match(new[] { WindowProfile("er", "eldenring") }, "notepad", string.Empty));
    }

    [Fact]
    public void MalformedTitlePatternDegradesToProcessNameMatch()
    {
        var profiles = new[] { WindowProfile("bad", "game", "(unclosed") };

        // A bad regex must not throw; it falls back to a process-name-only match.
        Assert.Equal("bad", ProfileMatcher.Match(profiles, "game", "anything")?.Id);
    }
}
