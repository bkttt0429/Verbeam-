using Verbeam.Core.Models;

namespace Verbeam.Api.Tray;

/// <summary>
/// One capture region's live state inside <see cref="NativeRegionTranslator"/>: its resolved
/// screen rectangle, its own overlay and change-detection watcher, and the last sampled frame /
/// OCR text used to skip redundant work. <see cref="Normalized"/> is the profile region this
/// session came from, so window-bound sessions can be re-mapped as the window moves; it is null
/// for an ad-hoc drag-selected region whose <see cref="ScreenBounds"/> are already absolute.
/// </summary>
internal sealed class RegionSession : IDisposable
{
    public RegionSession(GameRegionRect? normalized, TranslationOverlayWindow overlay, RegionWatchStateMachine watch)
    {
        Normalized = normalized;
        Overlay = overlay;
        Watch = watch;
    }

    public GameRegionRect? Normalized { get; }

    public Rectangle ScreenBounds { get; set; }

    public string LastOcrText { get; set; } = string.Empty;

    public bool Busy { get; set; }

    /// <summary>Stable index of this region within the active profile, used as the broadcast key.</summary>
    public int Index { get; set; }

    public TranslationOverlayWindow Overlay { get; }

    public RegionWatchStateMachine Watch { get; set; }

    /// <summary>Cancellation generation for this region's in-flight OCR+translate. A newer stabilized
    /// frame cancels it (supersede) so the overlay shows the latest line instead of waiting for the
    /// previous line's tokens. Not disposed per-generation (no CancelAfter / WaitHandle, so GC
    /// reclaims it); replaced wholesale on each new frame.</summary>
    public CancellationTokenSource? Cts { get; set; }

    public void Dispose()
    {
        try { Cts?.Cancel(); } catch (ObjectDisposedException) { }
        Overlay.Dispose();
    }
}
