using System.Runtime.InteropServices;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Api.Tray;

/// <summary>
/// System-tray background mode: NotifyIcon with menu, global hotkeys for the
/// native region translator, and optional auto-start shortcut. Runs on a
/// dedicated STA thread next to the ASP.NET host; Exit stops the web host.
/// </summary>
public static class TrayHost
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwHide = 0;

    public static void HideConsoleWindow()
    {
        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, SwHide);
        }
    }

    public static void Start(
        IServiceProvider services,
        string workbenchUrl,
        Action requestShutdown,
        CancellationToken applicationStopping,
        bool showWindow)
    {
        var thread = new Thread(() =>
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            using var context = new TrayApplicationContext(services, workbenchUrl, requestShutdown, showWindow);
            using var stopRegistration = applicationStopping.Register(() =>
            {
                try
                {
                    context.BeginExit();
                }
                catch
                {
                }
            });
            System.Windows.Forms.Application.Run(context);
        })
        {
            IsBackground = true,
            Name = "verbeam-tray"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private HotkeyWindow? _hotkeys;
    private readonly NativeRegionService _engine;
    private readonly GameProfileStore _profiles;
    private readonly VerbeamOptions _options;
    private readonly HotkeySettingsService _hotkeySettings;
    private readonly HotkeyRuntimeService _hotkeyRuntime;
    private readonly RegionSelectionSafety _selectionSafety;
    private readonly ForegroundWindowWatcher _foreground;
    private readonly Control _marshal;
    private readonly VerbeamShellWindow? _shell;
    private readonly string _workbenchUrl;
    private readonly Action _requestShutdown;
    private bool _exiting;

    public TrayApplicationContext(IServiceProvider services, string workbenchUrl, Action requestShutdown, bool showWindow)
    {
        _workbenchUrl = workbenchUrl;
        _requestShutdown = requestShutdown;
        _profiles = services.GetRequiredService<GameProfileStore>();
        _options = services.GetRequiredService<VerbeamOptions>();
        _engine = services.GetRequiredService<NativeRegionService>();
        _hotkeySettings = services.GetRequiredService<HotkeySettingsService>();
        _hotkeyRuntime = services.GetRequiredService<HotkeyRuntimeService>();
        _selectionSafety = services.GetRequiredService<RegionSelectionSafety>();
        _marshal = new Control();
        _ = _marshal.Handle;

        _foreground = new ForegroundWindowWatcher();
        _foreground.ForegroundChanged += OnForegroundChanged;
        _foreground.Enabled = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Workbench", null, (_, _) => ShowApp("/app"));
        var profilesMenu = new ToolStripMenuItem("Game Profiles");
        profilesMenu.DropDownOpening += (_, _) => PopulateProfiles(profilesMenu);
        menu.Items.Add(profilesMenu);
        var autoSwitchItem = new ToolStripMenuItem("Auto-switch by Game Window")
        {
            Checked = _foreground.Enabled,
            CheckOnClick = true
        };
        autoSwitchItem.CheckedChanged += (_, _) => _foreground.Enabled = autoSwitchItem.Checked;
        menu.Items.Add(autoSwitchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Region Capture", null, (_, _) => TriggerSnapshotSelector());
        menu.Items.Add("Toggle Loop", null, (_, _) => _engine.ToggleLoop());
        menu.Items.Add("Capture Profile Regions…", null, (_, _) => CaptureProfileRegions());
        menu.Items.Add(new ToolStripSeparator());
        var autoStartItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupShortcut.Exists()
        };
        autoStartItem.Click += (_, _) =>
        {
            autoStartItem.Checked = StartupShortcut.Toggle();
        };
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => BeginExit());

        _icon = new NotifyIcon
        {
            Icon = BrandAssets.LoadAppIcon(),
            Text = "Verbeam — local translate hub",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowApp(null);

        _hotkeyRuntime.BindingsChanged += OnHotkeyBindingsChanged;
        var bindings = Task.Run(() => _hotkeySettings.LoadEffectiveBindingsAsync()).GetAwaiter().GetResult();
        ApplyHotkeys(bindings, notifyFailures: true);

        if (showWindow)
        {
            _shell = new VerbeamShellWindow(_workbenchUrl, _options.Shell);
            _shell.Show();
        }
    }

    public void BeginExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            _foreground.Dispose();
            _hotkeyRuntime.BindingsChanged -= OnHotkeyBindingsChanged;
            _hotkeys?.Dispose();
            _marshal.Dispose();
            if (_shell is not null)
            {
                try { _shell.AllowClose(); _shell.Close(); _shell.Dispose(); }
                catch { /* shutting down */ }
            }

            _icon.Visible = false;
            _icon.Dispose();
        }
        finally
        {
            _requestShutdown();
            ExitThread();
        }
    }

    private void OpenUrl(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                // _workbenchUrl is the host root, which redirects to /health — always append the page path.
                FileName = _workbenchUrl.TrimEnd('/') + path,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    /// <summary>Bring the in-app WebView2 window forward (and optionally navigate); falls back to the
    /// system browser when running with --no-window.</summary>
    private void ShowApp(string? path)
    {
        if (_shell is not null)
        {
            _shell.RestoreAndNavigate(path);
        }
        else
        {
            OpenUrl(path ?? "/app");
        }
    }

    private void PopulateProfiles(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        parent.DropDownItems.Add("Manage Profiles…", null, (_, _) => ShowApp("/profiles"));
        parent.DropDownItems.Add(new ToolStripSeparator());

        GameProfilesDocument document;
        try
        {
            // Off the UI thread to avoid a sync-over-async deadlock on the store's gate; the
            // profiles file is tiny so the brief block on menu-open is fine.
            document = Task.Run(() => _profiles.GetDocumentAsync()).GetAwaiter().GetResult();
        }
        catch
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("(failed to load profiles)") { Enabled = false });
            return;
        }

        if (document.Profiles.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("(no profiles yet)") { Enabled = false });
            return;
        }

        foreach (var profile in document.Profiles)
        {
            var captured = profile;
            var item = new ToolStripMenuItem(string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name)
            {
                Checked = string.Equals(profile.Id, document.ActiveId, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) => ActivateProfile(captured);
            parent.DropDownItems.Add(item);
        }
    }

    private void ActivateProfile(GameProfile profile)
    {
        _ = _profiles.SetActiveAsync(profile.Id);   // persist the active pointer (fire-and-forget)
        _engine.ApplyProfile(profile);
    }

    private void TriggerSnapshotSelector()
    {
        var safety = _selectionSafety.Check();
        if (!safety.CanOpenSelector)
        {
            var status = _engine.Status();
            if (status.RegionCount > 0)
            {
                _ = _engine.SnapshotAsync(reselect: false);
                Notify("Fullscreen detected; captured the saved region set. Switch to borderless/windowed mode to edit regions.");
                return;
            }

            Notify(safety.Message);
            return;
        }

        _ = _engine.SnapshotAsync(reselect: true);
    }

    private async void CaptureProfileRegions()
    {
        var active = _engine.ActiveProfile;
        if (active is null)
        {
            Notify("Activate a game profile first, then capture its regions.");
            return;
        }

        if (!_engine.TryGetActiveSurfaceBounds(out _))
        {
            Notify($"Can't find the window for '{active.Name}'. Launch the game, then try again.");
            return;
        }

        var safety = _selectionSafety.Check();
        if (!safety.CanOpenSelector)
        {
            Notify(safety.Message);
            return;
        }

        var regions = _engine.CaptureRegionsNormalized();
        if (regions is null || regions.Count == 0)
        {
            return;
        }

        GameProfile saved;
        try
        {
            saved = await _profiles.UpsertAsync(active with { Regions = regions });
        }
        catch
        {
            Notify("Failed to save the captured regions.");
            return;
        }

        _engine.ApplyProfile(saved);
        Notify($"Saved {regions.Count} region(s) to '{saved.Name}'.");
    }

    private void Notify(string message)
    {
        try
        {
            _icon.BalloonTipTitle = "Verbeam";
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(3000);
        }
        catch
        {
        }
    }

    private void OnForegroundChanged(ForegroundWindowWatcher.ForegroundInfo info)
    {
        GameProfilesDocument document;
        try
        {
            // Off the UI thread to avoid a sync-over-async deadlock on the store's gate.
            document = Task.Run(() => _profiles.GetDocumentAsync()).GetAwaiter().GetResult();
        }
        catch
        {
            return;
        }

        var match = ProfileMatcher.Match(document.Profiles, info.ProcessName, info.Title);
        // No match leaves the current profile running; only an actual switch re-applies.
        if (match is null || string.Equals(match.Id, _engine.ActiveProfile?.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = _profiles.SetActiveAsync(match.Id);
        _engine.ApplyProfile(match);
        Notify($"Switched to '{match.Name}'.");
    }

    private const int HotkeyBase = 0xB000;

    private void OnHotkeyBindingsChanged(IReadOnlyDictionary<string, string> bindings)
    {
        if (_exiting || _marshal.IsDisposed)
        {
            return;
        }

        if (_marshal.InvokeRequired)
        {
            _marshal.Invoke(new Action(() => ApplyHotkeys(bindings, notifyFailures: true)));
        }
        else
        {
            ApplyHotkeys(bindings, notifyFailures: true);
        }
    }

    private void ApplyHotkeys(IReadOnlyDictionary<string, string> bindings, bool notifyFailures)
    {
        var previous = _hotkeys;
        if (previous is not null)
        {
            previous.HotkeyPressed -= OnHotkey;
            previous.Dispose();
        }

        _hotkeys = new HotkeyWindow(BuildHotkeyBindings(bindings));
        _hotkeys.HotkeyPressed += OnHotkey;

        var registered = new HashSet<int>(_hotkeys.RegisteredIds);
        var failed = new HashSet<int>(_hotkeys.FailedIds);
        var statuses = Enum.GetValues<HotkeyAction>().Select(action =>
        {
            var actionId = HotkeySettingsService.ActionId(action);
            var spec = bindings.GetValueOrDefault(actionId, string.Empty);
            var id = HotkeyBase + (int)action;
            if (string.IsNullOrWhiteSpace(spec))
            {
                return new HotkeyRegistrationView(actionId, "disabled", "Disabled.");
            }

            if (registered.Contains(id))
            {
                return new HotkeyRegistrationView(actionId, "registered", "Registered and ready.");
            }

            if (failed.Contains(id))
            {
                return new HotkeyRegistrationView(actionId, "failed", "Already in use by another app or Windows.");
            }

            return new HotkeyRegistrationView(actionId, "invalid", "Invalid shortcut.");
        }).ToArray();
        _hotkeyRuntime.SetRegistrationStatus(statuses);

        if (notifyFailures && _hotkeys.FailedIds.Count > 0)
        {
            var skipped = string.Join(", ", _hotkeys.FailedIds.Select(id => ((HotkeyAction)(id - HotkeyBase)).ToString()));
            Notify("Hotkeys already in use, skipped: " + skipped);
        }
    }

    private static IEnumerable<(int id, uint mods, uint vk)> BuildHotkeyBindings(IReadOnlyDictionary<string, string> bindings)
    {
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            var spec = bindings.GetValueOrDefault(HotkeySettingsService.ActionId(action), string.Empty);
            if (HotkeySpec.TryParse(spec, out var mods, out var vk))
            {
                yield return (HotkeyBase + (int)action, mods, vk);
            }
        }
    }

    private void OnHotkey(int id)
    {
        switch ((HotkeyAction)(id - HotkeyBase))
        {
            case HotkeyAction.Snapshot: TriggerSnapshotSelector(); break;
            case HotkeyAction.ToggleLoop: _engine.ToggleLoop(); break;
            case HotkeyAction.CaptureRegions: CaptureProfileRegions(); break;
            case HotkeyAction.NextProfile: CycleProfile(1); break;
            case HotkeyAction.PrevProfile: CycleProfile(-1); break;
            case HotkeyAction.ToggleOverlays: _engine.ToggleOverlays(); break;
        }
    }

    private void CycleProfile(int direction)
    {
        GameProfilesDocument document;
        try
        {
            document = Task.Run(() => _profiles.GetDocumentAsync()).GetAwaiter().GetResult();
        }
        catch
        {
            return;
        }

        var list = document.Profiles;
        if (list.Count == 0)
        {
            Notify("No game profiles to switch.");
            return;
        }

        var activeId = _engine.ActiveProfile?.Id;
        var index = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, activeId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        var next = index < 0
            ? (direction > 0 ? 0 : list.Count - 1)
            : (((index + direction) % list.Count) + list.Count) % list.Count;
        var profile = list[next];
        _ = _profiles.SetActiveAsync(profile.Id);
        _engine.ApplyProfile(profile);
        Notify("Profile: " + profile.Name);
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly List<int> _registered = new();
    private readonly List<int> _failed = new();

    public event Action<int>? HotkeyPressed;

    public IReadOnlyList<int> FailedIds => _failed;
    public IReadOnlyList<int> RegisteredIds => _registered;

    public HotkeyWindow(IEnumerable<(int id, uint mods, uint vk)> bindings)
    {
        CreateHandle(new CreateParams());
        foreach (var (id, mods, vk) in bindings)
        {
            if (RegisterHotKey(Handle, id, mods | ModNoRepeat, vk))
            {
                _registered.Add(id);
            }
            else
            {
                _failed.Add(id);
            }
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            HotkeyPressed?.Invoke(m.WParam.ToInt32());
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _registered)
        {
            UnregisterHotKey(Handle, id);
        }

        DestroyHandle();
    }
}

internal static class StartupShortcut
{
    private static string ShortcutPath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Verbeam.lnk");

    public static bool Exists() => File.Exists(ShortcutPath);

    public static bool Toggle()
    {
        if (Exists())
        {
            try
            {
                File.Delete(ShortcutPath);
            }
            catch
            {
            }

            return Exists();
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            var shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
            shortcut.Arguments = "--tray";
            shortcut.WorkingDirectory = AppContext.BaseDirectory;
            shortcut.Description = "Verbeam local translate hub (tray)";
            shortcut.Save();
        }
        catch
        {
        }

        return Exists();
    }
}
