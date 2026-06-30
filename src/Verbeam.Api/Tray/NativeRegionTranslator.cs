using System.Drawing.Imaging;
using Verbeam.Api.Broadcast;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Api.Tray;

/// <summary>
/// Native (browser-free) region translation. Captures one or more screen rectangles with
/// CopyFromScreen, runs OCR + translation through the in-process services (same realtime
/// semantics, caches and digit-slot templates as the workbench region mode), and shows each
/// region's result in its own always-on-top overlay. Regions come from either an ad-hoc
/// drag-selection (single region, full-toolbar overlay) or an activated <see cref="GameProfile"/>
/// (multiple regions mapped from the bound window/monitor surface, compact overlays that follow
/// the window). Must be used from the tray UI thread; service calls are awaited on the WinForms
/// synchronization context so UI updates stay on that thread.
/// </summary>
public sealed class NativeRegionTranslator : IDisposable
{
    private readonly OcrService _ocrService;
    private readonly TranslationService _translationService;
    private readonly VerbeamOptions _options;
    private readonly string _sessionId = "tray-" + Guid.NewGuid().ToString("N")[..8];

    private readonly List<RegionSession> _sessions = new();
    private ICaptureSurface? _surface;          // non-null in profile (window/monitor) mode
    private EffectiveSettings _settings;
    private System.Windows.Forms.Timer? _loopTimer;
    private bool _overlaysHidden;
    private readonly TranslationBroadcastHub? _broadcast;
    private int? _minOcrGapOverrideMs;

    public NativeRegionTranslator(
        OcrService ocrService,
        TranslationService translationService,
        VerbeamOptions options,
        TranslationBroadcastHub? broadcast = null)
    {
        _ocrService = ocrService;
        _translationService = translationService;
        _options = options;
        _broadcast = broadcast;
        _settings = AdHocSettings();
    }

    public bool LoopActive => _loopTimer is not null;

    /// <summary>The game profile currently applied (null in ad-hoc mode). Region capture writes
    /// the framed regions back to this profile.</summary>
    public GameProfile? ActiveProfile { get; private set; }

    // ---- ad-hoc single region (preserves the original tray UX) ----

    public async Task SnapshotAsync(bool reselect)
    {
        if (reselect || _sessions.Count == 0)
        {
            StopLoop();
            var selected = RegionSelectionOverlay.SelectRegion();
            if (selected is null)
            {
                return;
            }

            _surface = null;
            ActiveProfile = null;
            _settings = AdHocSettings();
            ResetSessions();
            var adHoc = CreateSession(normalized: null, bounds: selected.Value, compact: false);
            adHoc.Index = 0;
            _sessions.Add(adHoc);
        }

        await TranslateAllAsync(force: true);
    }

    // ---- profile (multi-region) ----

    /// <summary>
    /// Switches to a game profile: rebuilds one session per region, mapped from the bound
    /// surface, with compact overlays, then starts the watch loop. Regions whose window is not
    /// present yet stay hidden and light up once the watcher resolves the window.
    /// </summary>
    public void ApplyProfile(GameProfile profile)
    {
        StopLoop();
        ResetSessions();

        ActiveProfile = profile;
        _overlaysHidden = false;
        _settings = ProfileSettings(profile);
        _surface = ResolveSurface(profile);

        IReadOnlyList<GameRegionRect> regions =
            profile.Regions.Count > 0 ? profile.Regions
            : profile.Region is { } single ? new[] { single }
            : Array.Empty<GameRegionRect>();

        foreach (var region in regions)
        {
            var bounds = ResolveBounds(region) ?? Rectangle.Empty;
            var session = CreateSession(region, bounds, compact: true);
            session.Index = _sessions.Count;
            if (bounds.IsEmpty)
            {
                session.Overlay.Hide();
            }

            _sessions.Add(session);
        }

        if (_sessions.Count == 0)
        {
            return;
        }

        StartLoop();
        _ = TranslateAllAsync(force: true);
    }

    /// <summary>
    /// Frames one or more capture regions on a frozen screenshot and returns them normalized to
    /// the active surface (window client rect / monitor), or null if there is no active surface or
    /// the user cancelled. Must run on the tray UI thread (shows a modal overlay).
    /// </summary>
    public IReadOnlyList<GameRegionRect>? CaptureRegionsNormalized()
    {
        if (_surface is null || !_surface.TryGetBounds(out var surface))
        {
            return null;
        }

        var screenRects = FrozenRegionSelectionOverlay.CaptureRegions();
        if (screenRects is null || screenRects.Count == 0)
        {
            return null;
        }

        return screenRects.Select(rect => SurfaceMath.ToNormalized(surface, rect)).ToList();
    }

    /// <summary>
    /// Ad-hoc multi-region: frame any number of boxes anywhere on screen (no profile, no window
    /// binding), then run them as fixed-screen-position regions. Each box becomes its own session +
    /// compact overlay + in-window card; language/provider come from the global defaults
    /// (AdHocSettings). Must run on the engine UI thread (shows a modal full-screen overlay).
    /// </summary>
    public void SelectAndRunRegions()
    {
        // Re-open the selector pre-loaded with the regions that are already running, so the user can
        // move / resize / delete them (and add more) instead of redrawing from scratch.
        var current = _sessions.Select(s => s.ScreenBounds).Where(b => !b.IsEmpty).ToList();

        // Stop the watch loop while the full-screen selector is open. The modal pumps messages, so
        // the WinForms timer would otherwise keep firing and OCR the selector overlay itself (garbage
        // frames) behind it. Overlays stay visible for context; their bounds are pre-loaded above.
        var wasRunning = _loopTimer is not null;
        StopLoop();

        var rects = FrozenRegionSelectionOverlay.CaptureRegions(current.Count > 0 ? current : null);
        if (rects is null)
        {
            // Esc / cancel — keep the running set untouched; resume the loop if it was active.
            if (wasRunning)
            {
                ResumeLoop();
            }
            return;
        }

        ResetSessions();
        _surface = null;
        ActiveProfile = null;
        _settings = AdHocSettings();

        for (var i = 0; i < rects.Count; i++)
        {
            var session = CreateSession(normalized: null, bounds: rects[i], compact: true);
            session.Index = i;
            _sessions.Add(session);
        }

        // rects empty = the user deleted every box and confirmed → all overlays cleared, nothing runs.
        if (_sessions.Count == 0)
        {
            return;
        }

        StartLoop();
        _ = TranslateAllAsync(force: true);
    }

    /// <summary>Resume the loop on the current region set — Start after a Stop, without re-selecting.</summary>
    public void ResumeLoop()
    {
        if (_sessions.Count > 0 && _loopTimer is null)
        {
            ShowVisibleSessionOverlays();
            StartLoop();
            _ = TranslateAllAsync(force: true);
        }
    }

    public void ClearRegions()
    {
        StopLoop();
        ResetSessions();
        _surface = null;
        ActiveProfile = null;
        _overlaysHidden = false;
        _settings = AdHocSettings();
    }

    public bool TryGetActiveSurfaceBounds(out Rectangle bounds)
    {
        if (_surface is not null)
        {
            return _surface.TryGetBounds(out bounds);
        }

        bounds = Rectangle.Empty;
        return false;
    }

    /// <summary>Show/hide all region overlays without stopping the loop (declutter hotkey). While
    /// hidden, the watch loop keeps tracking but never re-shows an overlay.</summary>
    public void ToggleOverlays()
    {
        _overlaysHidden = !_overlaysHidden;
        foreach (var session in _sessions)
        {
            if (_overlaysHidden)
            {
                session.Overlay.Hide();
            }
            else if (!session.ScreenBounds.IsEmpty)
            {
                session.Overlay.ShowFor(session.ScreenBounds);
            }
        }
    }

    /// <summary>Idempotent overlay visibility (for the display-mode selector); broadcasting to the
    /// in-window display is unaffected and keeps flowing either way.</summary>
    public void SetOverlaysVisible(bool visible)
    {
        if (_overlaysHidden != !visible)
        {
            ToggleOverlays();
        }
    }

    public void ConfigureLoop(int? minOcrGapMs)
    {
        _minOcrGapOverrideMs = minOcrGapMs.HasValue
            ? Math.Clamp(minOcrGapMs.Value, 500, 30000)
            : null;

        if (_loopTimer is not null)
        {
            StartLoop();
        }
    }

    public NativeRegionStatus StatusSnapshot()
        => new(
            LoopActive: _loopTimer is not null,
            OverlaysHidden: _overlaysHidden,
            ProfileId: ActiveProfile?.Id,
            ProfileName: ActiveProfile?.Name,
            RegionCount: _sessions.Count,
            WatchTickMs: Math.Max(50, _options.Tray.WatchTickMs),
            MinOcrGapMs: CurrentMinOcrGapMs(),
            ForceRefreshMs: Math.Max(400, _options.Tray.ForceRefreshMs));

    public IReadOnlyList<NativeRegionSnapshot> RegionSnapshots()
        => _sessions
            .Where(session => !session.ScreenBounds.IsEmpty)
            .OrderBy(session => session.Index)
            .Select(session => new NativeRegionSnapshot(
                session.Index,
                ScreenRect.From(session.ScreenBounds),
                session.Busy,
                session.LastOcrText.Length <= 120 ? session.LastOcrText : session.LastOcrText[..120]))
            .ToArray();

    private async Task BroadcastRegionAsync(
        RegionSession session, string sourceText, string translatedText, string engine, long latencyMs, bool cacheHit)
    {
        if (_broadcast is null)
        {
            return;
        }

        // SourceKind "region" + a stable per-region key so the in-window UI routes each result to
        // the right region panel and replaces (not appends) on update.
        await _broadcast.BroadcastAsync(
            new TranslationBroadcastMessage(
                Type: "translation",
                SourceText: sourceText,
                TranslatedText: translatedText,
                Source: _settings.Source ?? "auto",
                Target: _settings.Target ?? string.Empty,
                Mode: _settings.Mode ?? string.Empty,
                Provider: _settings.Provider ?? string.Empty,
                Glossary: _settings.Glossary,
                Engine: engine,
                LatencyMs: latencyMs,
                CacheHit: cacheHit,
                CreatedAt: DateTimeOffset.UtcNow,
                SourceKind: "region",
                SegmentIndex: session.Index,
                StableKey: $"region-{session.Index}"),
            CancellationToken.None);
    }

    public void ToggleLoop()
    {
        if (_loopTimer is not null)
        {
            StopLoop();
            SetPrimaryStatus("loop off");
            return;
        }

        if (_sessions.Count == 0)
        {
            _ = SnapshotAsync(reselect: true).ContinueWith(
                _ => { if (_sessions.Count > 0) { StartLoop(); } },
                TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        StartLoop();
    }

    private void StartLoop()
    {
        StopLoop();
        // Two-tier watcher per session: every tick does only a cheap downsampled compare; the
        // full OCR+translate pipeline runs once a change has stabilized. One shared timer drives
        // all regions so N regions cost N cheap samples per tick, not N timers.
        foreach (var session in _sessions)
        {
            session.Watch = NewWatch();
            session.LastOcrText = string.Empty;
            session.Overlay.SetLoopActive(true);
        }

        var timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(50, _options.Tray.WatchTickMs)
        };
        timer.Tick += (_, _) => WatchTick();
        timer.Start();
        _loopTimer = timer;
        SetPrimaryStatus("loop on");
    }

    public void StopLoop(bool hideOverlays = false)
    {
        _loopTimer?.Stop();
        _loopTimer?.Dispose();
        _loopTimer = null;
        foreach (var session in _sessions)
        {
            session.Cts?.Cancel();
            session.Overlay.SetLoopActive(false);
            session.Overlay.CancelPendingStatus();
            if (hideOverlays && session.Overlay.Visible)
            {
                session.Overlay.Hide();
            }
        }
    }

    private void ShowVisibleSessionOverlays()
    {
        if (_overlaysHidden)
        {
            return;
        }

        foreach (var session in _sessions)
        {
            if (!session.ScreenBounds.IsEmpty)
            {
                session.Overlay.ShowFor(session.ScreenBounds);
            }
        }
    }

    private void WatchTick()
    {
        // Snapshot the list: an in-flight TranslateSessionAsync never mutates _sessions, only
        // ApplyProfile/ResetSessions do, and those run on this same UI thread between ticks.
        foreach (var session in _sessions)
        {
            TickSession(session);
        }
    }

    private void TickSession(RegionSession session)
    {
        // No early-out on session.Busy: sampling runs every tick even while a translation is in
        // flight, so a newer stabilized subtitle can SUPERSEDE the in-flight one (cancel + translate
        // the latest) instead of the overlay waiting for / showing a stale line.

        // Window-bound regions follow the game window; re-map every tick and park the overlay
        // while the window is gone/minimized.
        if (session.Normalized is not null && _surface is not null)
        {
            if (_surface.TryGetBounds(out var surfaceBounds))
            {
                var mapped = SurfaceMath.ToScreen(surfaceBounds, session.Normalized);
                var changed = mapped != session.ScreenBounds;
                session.ScreenBounds = mapped;
                if (_overlaysHidden)
                {
                    if (session.Overlay.Visible) { session.Overlay.Hide(); }
                }
                // Re-map (and re-show, if the window returned to the same spot after being parked).
                else if (changed || !session.Overlay.Visible)
                {
                    session.Overlay.ShowFor(mapped);
                }
            }
            else
            {
                if (session.Overlay.Visible)
                {
                    session.Overlay.Hide();
                }

                return;
            }
        }

        if (session.ScreenBounds.IsEmpty)
        {
            return;
        }

        Bitmap? bitmap = null;
        try
        {
            bitmap = CaptureRegionUnhidden(session, out var overlayMasked);
            var sample = RegionFrameComparer.Sample(bitmap);
            var now = Environment.TickCount64;
            if (!session.Watch.ShouldRunOcr(sample, now))
            {
                return;
            }

            session.Watch.MarkOcrStarted(sample, now);
            // Supersede any in-flight translation for this region, then dispatch the latest frame.
            session.Cts?.Cancel();
            var cts = new CancellationTokenSource();
            session.Cts = cts;
            if (overlayMasked)
            {
                // Masked pixels would corrupt OCR; recapture with the hide dance for this frame.
                bitmap.Dispose();
                bitmap = null;
                _ = TranslateSessionAsync(session, force: false, preCaptured: null, cts);
            }
            else
            {
                var captured = bitmap;
                bitmap = null;
                _ = TranslateSessionAsync(session, force: false, captured, cts);
            }
        }
        catch
        {
            // Sampling hiccups (display topology change mid-capture) skip the tick.
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private async Task TranslateAllAsync(bool force)
    {
        // Sequential snapshot render (initial profile activation / ad-hoc snapshot); the watch loop
        // is the concurrent + supersede path. Each session gets a fresh cancellation generation.
        foreach (var session in _sessions.ToArray())
        {
            if (!session.ScreenBounds.IsEmpty)
            {
                session.Cts?.Cancel();
                var cts = new CancellationTokenSource();
                session.Cts = cts;
                await TranslateSessionAsync(session, force, preCaptured: null, cts);
            }
        }
    }

    private async Task TranslateSessionAsync(RegionSession session, bool force, Bitmap? preCaptured, CancellationTokenSource cts)
    {
        // Supersede model: a newer stabilized subtitle cancels `cts` and replaces session.Cts. OCR
        // and translate run with `cts.Token` so the in-flight work stops promptly (freeing the OCR
        // lock + the llama slot for the latest line). A superseded run must not touch shared state —
        // guarded by `session.Cts == cts` before applying results and in the finally.
        if (session.ScreenBounds.IsEmpty)
        {
            preCaptured?.Dispose();
            return;
        }

        session.Busy = true;
        var token = cts.Token;
        try
        {
            using var bitmap = preCaptured ?? CaptureRegion(session);

            session.Overlay.SetTransientStatus("ocr");
            // Upscale small crops before OCR (see ScaleRegionForOcr).
            using var ocrBitmap = ScaleRegionForOcr(bitmap);
            // PNG (lossless) for OCR input: JPEG artifacts hurt small / thin / outlined CJK and
            // compressed game text. Localhost transfer cost is negligible.
            var imageBase64 = EncodePngBase64(ocrBitmap ?? bitmap);
            var ocr = await _ocrService.RecognizeAsync(
                new OcrRequest
                {
                    ImageBase64 = imageBase64,
                    ImageMimeType = "image/png",
                    Language = _settings.OcrLanguage,
                    Profile = "default",
                    SessionId = _sessionId,
                    Realtime = true
                },
                token);
            var text = (ocr.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                session.Overlay.SetStatus("no text");
                return;
            }

            if (!force && string.Equals(text, session.LastOcrText, StringComparison.Ordinal))
            {
                session.Overlay.CancelPendingStatus();
                return;
            }

            session.Overlay.SetTransientStatus("translating");
            var outcome = await _translationService.TranslateAsync(
                new MortTranslateRequest
                {
                    Name = "tray-region",
                    Text = text,
                    Source = _settings.Source,
                    Target = _settings.Target,
                    Mode = _settings.Mode,
                    Provider = _settings.Provider,
                    Model = _settings.Model,
                    Profile = _settings.Profile,
                    Glossary = _settings.Glossary,
                    SessionId = _sessionId,
                    Realtime = true,
                    BroadcastSourceKind = "region"
                },
                token);

            if (session.Cts != cts)
            {
                return; // a newer subtitle superseded this region mid-translate — drop the stale result
            }

            if (outcome.IsSuccess)
            {
                // Only mark this text as handled on success, so a failed line (e.g. an empty /
                // template-residue model output that returns IsSuccess=false) is retried on the
                // next force-refresh instead of being stuck behind the unchanged-text guard above.
                // Mirrors the thrown-failure path, which never reaches here and so already retries.
                session.LastOcrText = text;
                session.Overlay.SetText(outcome.Text);
                session.Overlay.SetStatus($"{outcome.Engine} {outcome.LatencyMs}ms{(outcome.CacheHit ? "*" : string.Empty)}");
                // Publish for the in-window display (Phase B); fires regardless of overlay visibility.
                await BroadcastRegionAsync(session, text, outcome.Text, outcome.Engine, outcome.LatencyMs, outcome.CacheHit);
            }
            else
            {
                session.Overlay.SetStatus("error: " + Pick(outcome.ErrorMessage, outcome.ErrorCode));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer subtitle; leave the overlay/state for the newer run.
        }
        catch (Exception ex)
        {
            if (session.Cts == cts)
            {
                session.Overlay.SetStatus("error: " + ex.Message);
            }
        }
        finally
        {
            if (session.Cts == cts)
            {
                session.Busy = false;
            }
        }
    }

    private Bitmap CaptureRegion(RegionSession session)
    {
        var region = session.ScreenBounds;
        // Hide any overlay (this session's or another's) overlapping the region while capturing,
        // so no translation window is OCR'd as part of the frame.
        var hidden = _sessions
            .Where(other => other.Overlay.Visible && other.Overlay.Bounds.IntersectsWith(region))
            .ToList();
        foreach (var other in hidden)
        {
            other.Overlay.Visible = false;
        }

        if (hidden.Count > 0)
        {
            foreach (var other in hidden)
            {
                other.Overlay.Update();
            }

            Thread.Sleep(30);
        }

        try
        {
            var bitmap = new Bitmap(region.Width, region.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(region.Location, Point.Empty, region.Size);
            return bitmap;
        }
        finally
        {
            foreach (var other in hidden)
            {
                other.Overlay.Visible = true;
            }
        }
    }

    /// <summary>
    /// Watch-tick capture: never hides overlays (toggling at the watch cadence would flicker). Any
    /// overlay overlapping the region has its pixels blacked out so its own status/translation
    /// updates can never feed back into change detection.
    /// </summary>
    private Bitmap CaptureRegionUnhidden(RegionSession session, out bool overlayMasked)
    {
        var region = session.ScreenBounds;
        var bitmap = new Bitmap(region.Width, region.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.Location, Point.Empty, region.Size);
        overlayMasked = false;
        foreach (var other in _sessions)
        {
            if (other.Overlay.Visible && other.Overlay.Bounds.IntersectsWith(region))
            {
                var overlap = Rectangle.Intersect(other.Overlay.Bounds, region);
                overlap.Offset(-region.X, -region.Y);
                graphics.FillRectangle(Brushes.Black, overlap);
                overlayMasked = true;
            }
        }

        return bitmap;
    }

    private RegionSession CreateSession(GameRegionRect? normalized, Rectangle bounds, bool compact)
    {
        var overlay = new TranslationOverlayWindow(compact);
        if (!compact)
        {
            overlay.SnapshotRequested += () => _ = SnapshotAsync(reselect: false);
            overlay.LoopToggleRequested += ToggleLoop;
            overlay.CloseRequested += () =>
            {
                StopLoop();
                overlay.Hide();
            };
        }

        var session = new RegionSession(normalized, overlay, NewWatch()) { ScreenBounds = bounds };
        if (!bounds.IsEmpty)
        {
            overlay.ShowFor(bounds);
        }

        return session;
    }

    private RegionWatchStateMachine NewWatch()
        => new(
            RegionFrameComparer.Differ,
            _options.Tray.StableTicks,
            CurrentMinOcrGapMs(),
            Math.Max(400, _options.Tray.ForceRefreshMs));

    private int CurrentMinOcrGapMs()
        => Math.Max(500, _minOcrGapOverrideMs ?? _options.Tray.MinOcrGapMs);

    private void ResetSessions()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private void SetPrimaryStatus(string status)
    {
        if (_sessions.Count > 0)
        {
            _sessions[0].Overlay.SetStatus(status);
        }
    }

    private ICaptureSurface ResolveSurface(GameProfile profile)
    {
        var binding = profile.Surface;
        if (binding is null || string.Equals(binding.Kind, "window", StringComparison.OrdinalIgnoreCase))
        {
            var processName = binding?.ProcessName ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(processName))
            {
                return new WindowCaptureSurface(new WindowLocator(processName, binding?.WindowTitlePattern ?? string.Empty));
            }
        }

        var screen = ResolveMonitor(binding?.MonitorDeviceName);
        return new MonitorCaptureSurface(() => screen.Bounds);
    }

    private Rectangle? ResolveBounds(GameRegionRect normalized)
        => _surface is not null && _surface.TryGetBounds(out var surfaceBounds)
            ? SurfaceMath.ToScreen(surfaceBounds, normalized)
            : null;

    private static Screen ResolveMonitor(string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = Screen.AllScreens.FirstOrDefault(
                screen => string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    private EffectiveSettings AdHocSettings()
        => new(
            OcrLanguage: Pick(_options.Tray.OcrLanguage, null),
            Source: Pick(_options.DefaultSource, "auto"),
            Target: _options.DefaultTarget,
            Mode: _options.DefaultMode,
            Provider: Pick(_options.Tray.TranslationProvider, _options.DefaultProvider),
            Model: Pick(_options.Tray.Model, null),
            Profile: "default",
            Glossary: null);

    private EffectiveSettings ProfileSettings(GameProfile profile)
        => new(
            // profile.OcrProvider (engine choice) is not wired here yet — OcrService selects the
            // engine globally; only the OCR language is per-request today.
            OcrLanguage: NormalizeAuto(profile.OcrLanguage),
            Source: string.IsNullOrWhiteSpace(profile.Source) ? "auto" : profile.Source.Trim(),
            Target: string.IsNullOrWhiteSpace(profile.Target) ? _options.DefaultTarget : profile.Target.Trim(),
            Mode: Pick(profile.Mode, _options.DefaultMode),
            Provider: Pick(profile.Provider, _options.DefaultProvider),
            Model: Pick(profile.Model, null),
            Profile: string.IsNullOrWhiteSpace(profile.Id) ? "default" : profile.Id,
            Glossary: NormalizeAuto(profile.GlossaryId));

    private static string? NormalizeAuto(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    private static string EncodePngBase64(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToBase64String(stream.ToArray());
    }

    // Native counterpart to the workbench's regionEncodeScale: small subtitle crops (esp. narrow
    // vertical text) fall below rapidocr-net's detector size, so upscale the short side toward
    // ~700px (capped) before OCR; downscale anything wider than 1600 to bound cost. Returns null
    // when no scaling is needed (caller keeps the original bitmap).
    private static Bitmap? ScaleRegionForOcr(Bitmap bitmap)
    {
        var scale = RegionOcrScale(bitmap.Width, bitmap.Height);
        if (scale == 1.0)
        {
            return null;
        }

        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        var result = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(bitmap, 0, 0, width, height);
        return result;
    }

    private const int RegionMaxCropWidth = 1600;
    private const int RegionMinCropShortSide = 700;
    private const int RegionMaxUpscale = 4;

    private static double RegionOcrScale(int width, int height)
    {
        if (width > RegionMaxCropWidth)
        {
            return (double)RegionMaxCropWidth / width;
        }

        var shortSide = Math.Min(width, height);
        if (shortSide <= 0 || shortSide >= RegionMinCropShortSide)
        {
            return 1.0;
        }

        var scale = Math.Min(
            Math.Min((double)RegionMaxUpscale, (double)RegionMinCropShortSide / shortSide),
            (double)RegionMaxCropWidth / Math.Max(width, height));
        return scale > 1.02 ? scale : 1.0;
    }

    private static string? Pick(string? value, string? fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public void Dispose()
    {
        StopLoop();
        ResetSessions();
    }

    private sealed record EffectiveSettings(
        string? OcrLanguage,
        string? Source,
        string? Target,
        string? Mode,
        string? Provider,
        string? Model,
        string Profile,
        string? Glossary);
}
