namespace Verbeam.Api.Tray;

/// <summary>
/// Pure decision logic for the two-tier native region loop, mirroring the
/// workbench JS watcher (watch tick / stable ticks / min OCR gap / force
/// refresh): a cheap downsampled frame sample arrives every tick; OCR runs only
/// once a change has held still for the configured number of ticks, rate
/// limited by the minimum gap, with a quiet-window forced refresh as a safety
/// net for changes the sampler cannot see. Kept free of WinForms/capture
/// dependencies so the timing behavior is unit-testable.
/// </summary>
public sealed class RegionWatchStateMachine
{
    private readonly Func<byte[]?, byte[]?, bool> _differ;
    private readonly int _stableTicks;
    private readonly int _minOcrGapMs;
    private readonly int _forceRefreshMs;

    private byte[]? _lastTranslatedSample;
    private byte[]? _lastTickSample;
    private bool _pending;
    private int _stable;
    private long _lastOcrAtMs = long.MinValue / 2;

    public RegionWatchStateMachine(
        Func<byte[]?, byte[]?, bool> differ,
        int stableTicks,
        int minOcrGapMs,
        int forceRefreshMs)
    {
        _differ = differ;
        _stableTicks = Math.Max(0, stableTicks);
        _minOcrGapMs = Math.Max(0, minOcrGapMs);
        _forceRefreshMs = Math.Max(1, forceRefreshMs);
    }

    public bool ShouldRunOcr(byte[] sample, long nowMs)
    {
        var lastTick = _lastTickSample;
        _lastTickSample = sample;

        if (!_pending)
        {
            if (_differ(_lastTranslatedSample, sample))
            {
                _pending = true;
                _stable = 0;
            }
        }
        else
        {
            _stable = _differ(lastTick, sample) ? 0 : _stable + 1;
            if (_stable >= _stableTicks)
            {
                if (!_differ(_lastTranslatedSample, sample))
                {
                    // Transient (e.g. cursor pass-through) settled back to the
                    // already-translated frame.
                    _pending = false;
                    _stable = 0;
                }
                else if (nowMs - _lastOcrAtMs >= _minOcrGapMs)
                {
                    return true;
                }
            }
        }

        return nowMs - _lastOcrAtMs >= _forceRefreshMs;
    }

    public void MarkOcrStarted(byte[] sample, long nowMs)
    {
        _lastOcrAtMs = nowMs;
        _lastTranslatedSample = sample;
        _pending = false;
        _stable = 0;
    }
}
