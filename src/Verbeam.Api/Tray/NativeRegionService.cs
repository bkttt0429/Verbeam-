using System.Windows.Forms;
using Verbeam.Api.Broadcast;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Api.Tray;

/// <summary>
/// Owns the native region engine (<see cref="NativeRegionTranslator"/>) on its own dedicated STA
/// message-pump thread, so the tray menu/hotkeys AND (later) the web API / WebView2 shell can all
/// drive one shared engine instead of the tray owning it privately. Public calls are marshaled onto
/// the engine thread. Registered as a DI singleton; the WinForms thread starts lazily on first use,
/// so a headless/server run never spins it up.
/// </summary>
public sealed class NativeRegionService : IDisposable
{
    private readonly OcrService _ocr;
    private readonly TranslationService _translation;
    private readonly VerbeamOptions _options;
    private readonly TranslationBroadcastHub _broadcast;
    private readonly object _gate = new();

    private Thread? _thread;
    private Control? _marshal;          // handle-only control on the engine thread, for BeginInvoke/Invoke
    private NativeRegionTranslator? _translator;
    private volatile bool _disposed;

    public NativeRegionService(OcrService ocr, TranslationService translation, VerbeamOptions options, TranslationBroadcastHub broadcast)
    {
        _ocr = ocr;
        _translation = translation;
        _options = options;
        _broadcast = broadcast;
    }

    /// <summary>The active profile (reference read off the engine thread; a slightly stale value is
    /// fine for the menu/matcher comparisons that use it).</summary>
    public GameProfile? ActiveProfile => _translator?.ActiveProfile;

    public bool LoopActive => _translator?.LoopActive ?? false;

    public Task SnapshotAsync(bool reselect)
    {
        Post(() => _ = _translator!.SnapshotAsync(reselect));
        return Task.CompletedTask;
    }

    public void ToggleLoop() => Post(() => _translator!.ToggleLoop());

    public void ToggleOverlays() => Post(() => _translator!.ToggleOverlays());

    public void SetOverlaysVisible(bool visible) => Post(() => _translator!.SetOverlaysVisible(visible));

    public void ConfigureLoop(int? minOcrGapMs) => Invoke(() => _translator!.ConfigureLoop(minOcrGapMs));

    // Invoke (not Post): shows a modal full-screen overlay on the engine thread; the caller blocks
    // until the user finishes selecting — same pattern as CaptureRegionsNormalized.
    public void SelectAndRunRegions() => Invoke(() => _translator!.SelectAndRunRegions());

    public void ResumeLoop() => Invoke(() => _translator!.ResumeLoop());

    // Synchronous (Invoke) so a caller that reads Status() right after — e.g. the /region/native/activate
    // endpoint — reliably sees the applied profile, with no engine-thread cold-start race.
    public void ApplyProfile(GameProfile profile) => Invoke(() => _translator!.ApplyProfile(profile));

    public IReadOnlyList<GameRegionRect>? CaptureRegionsNormalized()
        => Invoke(() => _translator!.CaptureRegionsNormalized());

    public bool TryGetActiveSurfaceBounds(out Rectangle bounds)
    {
        bounds = Invoke(() => _translator!.TryGetActiveSurfaceBounds(out var b) ? b : Rectangle.Empty);
        return !bounds.IsEmpty;
    }

    public void Stop() => Invoke(() => _translator!.StopLoop(hideOverlays: true));

    public void Clear() => Invoke(() => _translator!.ClearRegions());

    /// <summary>Engine status WITHOUT starting the engine thread, so it's safe to poll.</summary>
    public NativeRegionStatus Status()
    {
        var marshal = _marshal;
        var translator = _translator;
        if (translator is null || marshal is null || marshal.IsDisposed)
        {
            return new NativeRegionStatus(
                false,
                false,
                null,
                null,
                0,
                Math.Max(50, _options.Tray.WatchTickMs),
                Math.Max(500, _options.Tray.MinOcrGapMs),
                Math.Max(400, _options.Tray.ForceRefreshMs));
        }

        return marshal.InvokeRequired
            ? (NativeRegionStatus)marshal.Invoke(new Func<NativeRegionStatus>(translator.StatusSnapshot))!
            : translator.StatusSnapshot();
    }

    /// <summary>Current selected region rectangles without starting the engine thread.</summary>
    public IReadOnlyList<NativeRegionSnapshot> RegionSnapshots()
    {
        var marshal = _marshal;
        var translator = _translator;
        if (translator is null || marshal is null || marshal.IsDisposed)
        {
            return [];
        }

        return marshal.InvokeRequired
            ? (IReadOnlyList<NativeRegionSnapshot>)marshal.Invoke(new Func<IReadOnlyList<NativeRegionSnapshot>>(translator.RegionSnapshots))!
            : translator.RegionSnapshots();
    }

    private void EnsureStarted()
    {
        if (_translator is not null || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_translator is not null || _disposed)
            {
                return;
            }

            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                var marshal = new Control();
                _ = marshal.Handle;             // realize the window handle on this thread for marshaling
                _marshal = marshal;
                _translator = new NativeRegionTranslator(_ocr, _translation, _options, _broadcast);
                ready.Set();
                Application.Run();              // invisible message loop until Application.ExitThread
                _translator?.Dispose();
                marshal.Dispose();
            })
            {
                IsBackground = true,
                Name = "verbeam-region-engine"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            _thread = thread;
        }
    }

    private void Post(Action action)
    {
        EnsureStarted();
        var marshal = _marshal;
        if (marshal is null || marshal.IsDisposed)
        {
            return;
        }

        if (marshal.InvokeRequired)
        {
            marshal.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private T Invoke<T>(Func<T> func)
    {
        EnsureStarted();
        var marshal = _marshal;
        if (marshal is null || marshal.IsDisposed)
        {
            return default!;
        }

        return marshal.InvokeRequired ? (T)marshal.Invoke(func)! : func();
    }

    private void Invoke(Action action)
    {
        EnsureStarted();
        var marshal = _marshal;
        if (marshal is null || marshal.IsDisposed)
        {
            return;
        }

        if (marshal.InvokeRequired)
        {
            marshal.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        var marshal = _marshal;
        if (marshal is not null && !marshal.IsDisposed)
        {
            try
            {
                marshal.BeginInvoke(new Action(Application.ExitThread));
            }
            catch
            {
                // Engine thread already gone.
            }
        }

        _thread?.Join(2000);
    }
}

public sealed record NativeRegionStatus(
    bool LoopActive,
    bool OverlaysHidden,
    string? ProfileId,
    string? ProfileName,
    int RegionCount,
    int WatchTickMs,
    int MinOcrGapMs,
    int ForceRefreshMs);
