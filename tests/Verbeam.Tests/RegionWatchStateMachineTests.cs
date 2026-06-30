using Verbeam.Api.Tray;

namespace Verbeam.Tests;

public sealed class RegionWatchStateMachineTests
{
    // Samples are one-byte labels; null (nothing translated yet) counts as
    // different, mirroring RegionFrameComparer.Differ.
    private static bool Differ(byte[]? left, byte[]? right)
        => left is null || right is null || left[0] != right[0];

    private static byte[] S(byte label) => [label];

    private static RegionWatchStateMachine Machine(
        int stableTicks = 1,
        int minOcrGapMs = 300,
        int forceRefreshMs = 5000)
        => new(Differ, stableTicks, minOcrGapMs, forceRefreshMs);

    [Fact]
    public void ChangeMustHoldStillBeforeOcr()
    {
        var machine = Machine();
        machine.MarkOcrStarted(S(1), 0);

        // New frame appears: detection tick only arms the pending phase.
        Assert.False(machine.ShouldRunOcr(S(2), 1000));
        // Same frame again: stabilized, past the gap -> OCR now.
        Assert.True(machine.ShouldRunOcr(S(2), 1150));
    }

    [Fact]
    public void TransientThatSettlesBackIsIgnored()
    {
        var machine = Machine();
        machine.MarkOcrStarted(S(1), 0);

        // Cursor passes through and the frame returns to the translated one.
        Assert.False(machine.ShouldRunOcr(S(9), 1000));
        Assert.False(machine.ShouldRunOcr(S(1), 1150));
        Assert.False(machine.ShouldRunOcr(S(1), 1300));
        Assert.False(machine.ShouldRunOcr(S(1), 1450));
    }

    [Fact]
    public void MinGapDefersButDoesNotDropTheChange()
    {
        var machine = Machine(minOcrGapMs: 300);
        machine.MarkOcrStarted(S(1), 1000);

        // Stabilized change arrives 200ms after the last OCR: deferred...
        Assert.False(machine.ShouldRunOcr(S(2), 1100));
        Assert.False(machine.ShouldRunOcr(S(2), 1200));
        // ...and fires as soon as the gap has elapsed.
        Assert.True(machine.ShouldRunOcr(S(2), 1350));
    }

    [Fact]
    public void ContinuousMotionNeverStabilizes_OnlyForceRefreshFires()
    {
        var machine = Machine(forceRefreshMs: 5000);
        machine.MarkOcrStarted(S(0), 0);

        var label = (byte)1;
        for (var t = 150L; t < 5000; t += 150)
        {
            Assert.False(machine.ShouldRunOcr(S(label++), t));
        }

        Assert.True(machine.ShouldRunOcr(S(label), 5100));
    }

    [Fact]
    public void QuietWindowForceRefreshFiresWithoutAnyChange()
    {
        var machine = Machine(forceRefreshMs: 5000);
        machine.MarkOcrStarted(S(1), 0);

        Assert.False(machine.ShouldRunOcr(S(1), 2500));
        Assert.True(machine.ShouldRunOcr(S(1), 5000));
    }

    [Fact]
    public void FirstTickAfterStartFiresImmediately()
    {
        // Nothing translated yet: the force-refresh fallback OCRs the very
        // first frame instead of waiting for a change.
        var machine = Machine();
        Assert.True(machine.ShouldRunOcr(S(1), 0));
    }

    [Fact]
    public void HigherStableTicksRequireLongerHold()
    {
        var machine = Machine(stableTicks: 2);
        machine.MarkOcrStarted(S(1), 0);

        Assert.False(machine.ShouldRunOcr(S(2), 1000));
        Assert.False(machine.ShouldRunOcr(S(2), 1150));
        Assert.True(machine.ShouldRunOcr(S(2), 1300));
    }
}
