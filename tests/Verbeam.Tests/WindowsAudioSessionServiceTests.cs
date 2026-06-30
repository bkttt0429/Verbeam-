using Verbeam.Api.Audio;

namespace Verbeam.Tests;

public sealed class WindowsAudioSessionServiceTests
{
    [Fact]
    public void ListSessions_ReturnsStableSnapshot()
    {
        var snapshot = new WindowsAudioSessionService().ListSessions();

        Assert.NotEqual(default, snapshot.CapturedAt);
        Assert.NotNull(snapshot.Sessions);

        if (!OperatingSystem.IsWindows())
        {
            Assert.False(snapshot.Supported);
            Assert.Empty(snapshot.Sessions);
            return;
        }

        if (!snapshot.Supported)
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.ErrorMessage));
            Assert.Empty(snapshot.Sessions);
            return;
        }

        Assert.Equal("multimedia", snapshot.EndpointRole);
        foreach (var session in snapshot.Sessions)
        {
            Assert.InRange(session.Peak, 0f, 1f);
            Assert.InRange(session.Volume, 0f, 1f);
            Assert.NotNull(session.ProcessName);
            Assert.NotNull(session.DisplayName);
            Assert.NotNull(session.State);
        }
    }
}
