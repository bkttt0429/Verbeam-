using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.Json;
using Verbeam.Core.Options;

namespace Verbeam.Api.Tray;

/// <summary>
/// The desktop app window: a borderless-free WinForms Form hosting a WebView2 that loads the local
/// workbench (`/app`). This is the single "App" front-end for the unified WebView2 + native-engine
/// design — same process as the tray and the native region engine. Closing (X) hides to tray rather
/// than exiting; the tray "Exit" calls <see cref="AllowClose"/> first. The web host may not be
/// listening yet when the shell starts (the tray/shell start just before app.Run), so a failed first
/// navigation retries a few times.
/// </summary>
internal sealed class VerbeamShellWindow : Form
{
    private enum ChromeNavGlyph
    {
        Back,
        Home
    }

    private readonly WebView2 _web;
    private readonly string _baseUrl;     // e.g. http://localhost:5757 (no trailing slash)
    private readonly ShellOptions _shellOptions;
    private readonly List<Button> _chromeButtons = new();
    private readonly ToolTip _chromeToolTip = new()
    {
        AutomaticDelay = 250,
        ReshowDelay = 80,
        AutoPopDelay = 5000,
        ShowAlways = true
    };
    private FlowLayoutPanel? _chromeBar;
    private Button? _backButton;
    private bool _backButtonCanGoBack;
    private bool _chromeLight;
    private bool _exiting;
    private bool _loaded;
    private int _navAttempts;

    public VerbeamShellWindow(string baseUrl, ShellOptions shellOptions)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _shellOptions = shellOptions;

        Text = "Verbeam";
        ClientSize = new Size(1200, 800);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ChromeWindowBackColor();
        try { Icon = BrandAssets.LoadAppIcon(); } catch { /* icon optional */ }

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);

        _chromeBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            BackColor = ChromeBarBackColor(),
            Padding = new Padding(7, 3, 6, 3),
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        _chromeBar.Paint += (sender, e) =>
        {
            if (sender is not Control bar)
            {
                return;
            }

            using var pen = new Pen(ChromeBorderColor());
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };
        _backButton = MakeChromeButton(
            "Back",
            ChromeNavGlyph.Back,
            () =>
            {
                if (!_backButtonCanGoBack)
                {
                    return;
                }

                var core = _web.CoreWebView2;
                if (core?.CanGoBack == true)
                {
                    core.GoBack();
                }
            });
        SetBackButtonAvailability(false);
        _chromeBar.Controls.Add(_backButton);
        _chromeBar.Controls.Add(MakeChromeButton("Home", ChromeNavGlyph.Home, () => NavigateTo("/app"), accent: true));
        Controls.Add(_chromeBar);   // added after _web so it docks to the top; _web fills the rest

        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var environmentOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = ShellSettingsService.BuildWebView2Arguments(_shellOptions)
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, null, environmentOptions);
            await _web.EnsureCoreWebView2Async(environment);
            _web.CoreWebView2.WebMessageReceived += (_, e) => HandleWebMessage(e.WebMessageAsJson);
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // Open target=_blank links (e.g. "Manage profiles ↗") in this same window so Back works.
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.CoreWebView2.HistoryChanged += (_, _) => UpdateNavigationChrome();
            _web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    _loaded = true;
                }
                else if (!_loaded && _navAttempts < 30)
                {
                    // Web host likely not listening yet at startup — retry shortly.
                    _navAttempts++;
                    var retry = new System.Windows.Forms.Timer { Interval = 400 };
                    retry.Tick += (_, _) =>
                    {
                        retry.Stop();
                        retry.Dispose();
                        NavigateTo("/app");
                    };
                    retry.Start();
                }

                UpdateNavigationChrome();
            };
            NavigateTo("/app");
        }
        catch (Exception ex)
        {
            ShowRuntimeHint(ex);
        }
    }

    private Button MakeChromeButton(string name, ChromeNavGlyph glyph, Action onClick, bool accent = false)
    {
        var button = new ChromeNavButton
        {
            IconGlyph = glyph,
            AccessibleName = name,
            AutoSize = false,
            Width = 28,
            Height = 24,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 4, 0),
            MinimumSize = new Size(28, 24),
            MaximumSize = new Size(28, 24),
            TabStop = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            Tag = accent,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.EnabledChanged += (_, _) => ApplyChromeButtonTheme(button);
        button.Click += (_, _) => onClick();
        _chromeButtons.Add(button);
        ApplyChromeButtonTheme(button);
        _chromeToolTip.SetToolTip(button, name);
        return button;
    }

    private Color ChromeWindowBackColor() => _chromeLight
        ? Color.FromArgb(248, 250, 252)
        : Color.FromArgb(9, 9, 9);

    private Color ChromeBarBackColor() => _chromeLight
        ? Color.FromArgb(241, 245, 249)
        : Color.FromArgb(13, 13, 13);

    private Color ChromeBorderColor() => _chromeLight
        ? Color.FromArgb(207, 216, 228)
        : Color.FromArgb(42, 42, 42);

    private Color ChromeNavNeutralBackColor() => _chromeLight
        ? Color.FromArgb(241, 245, 249)
        : Color.FromArgb(13, 13, 13);

    private bool ChromeButtonAccent(Button button) => button.Tag is bool accent && accent;

    private bool ChromeButtonDisabled(Button button)
        => !button.Enabled || (ReferenceEquals(button, _backButton) && !_backButtonCanGoBack);

    private Color ChromeButtonBackColor(Button button)
    {
        var accent = ChromeButtonAccent(button);
        if (ChromeButtonDisabled(button))
        {
            return ChromeNavNeutralBackColor();
        }

        return _chromeLight
            ? accent ? Color.FromArgb(225, 238, 255) : Color.FromArgb(248, 250, 252)
            : accent ? Color.FromArgb(18, 38, 70) : Color.FromArgb(24, 24, 24);
    }

    private Color ChromeButtonHoverColor(Button button)
    {
        var accent = ChromeButtonAccent(button);
        if (ChromeButtonDisabled(button))
        {
            return ChromeButtonBackColor(button);
        }

        return _chromeLight
            ? accent ? Color.FromArgb(211, 230, 255) : Color.FromArgb(232, 238, 247)
            : accent ? Color.FromArgb(28, 55, 98) : Color.FromArgb(32, 32, 32);
    }

    private Color ChromeButtonDownColor(Button button)
    {
        var accent = ChromeButtonAccent(button);
        if (ChromeButtonDisabled(button))
        {
            return ChromeButtonBackColor(button);
        }

        return _chromeLight
            ? accent ? Color.FromArgb(184, 216, 254) : Color.FromArgb(218, 226, 237)
            : accent ? Color.FromArgb(37, 72, 126) : Color.FromArgb(42, 42, 42);
    }

    private Color ChromeButtonBorderColor(Button button)
    {
        var accent = ChromeButtonAccent(button);
        if (ChromeButtonDisabled(button))
        {
            return ChromeNavNeutralBackColor();
        }

        return _chromeLight
            ? accent ? Color.FromArgb(111, 169, 245) : Color.FromArgb(188, 200, 216)
            : accent ? Color.FromArgb(76, 133, 213) : Color.FromArgb(64, 64, 64);
    }

    private Color ChromeButtonForegroundColor(Button button)
    {
        var accent = ChromeButtonAccent(button);
        if (ChromeButtonDisabled(button))
        {
            return _chromeLight ? Color.FromArgb(74, 88, 108) : Color.FromArgb(152, 162, 178);
        }

        return _chromeLight
            ? accent ? Color.FromArgb(30, 90, 210) : Color.FromArgb(38, 52, 72)
            : accent ? Color.FromArgb(171, 207, 255) : Color.FromArgb(218, 224, 232);
    }

    private void ApplyChromeButtonTheme(Button button)
    {
        button.ForeColor = ChromeButtonForegroundColor(button);
        var baseColor = ChromeButtonBackColor(button);
        var borderColor = ChromeButtonBorderColor(button);
        var hoverColor = ChromeButtonHoverColor(button);
        var downColor = ChromeButtonDownColor(button);
        button.BackColor = baseColor;
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.MouseOverBackColor = hoverColor;
        button.FlatAppearance.MouseDownBackColor = downColor;
        if (button is ChromeNavButton navButton)
        {
            navButton.BaseBackColor = baseColor;
            navButton.HoverBackColor = hoverColor;
            navButton.DownBackColor = downColor;
            navButton.ChromeBorderColor = borderColor;
            navButton.ShowFrame = false;
            navButton.Interactive = !ChromeButtonDisabled(button);
            navButton.Invalidate();
        }
        button.Cursor = ChromeButtonDisabled(button) ? Cursors.Default : Cursors.Hand;
    }

    private sealed class ChromeNavButton : Button
    {
        private bool _hovered;
        private bool _pressed;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ChromeNavGlyph IconGlyph { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BaseBackColor { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color HoverBackColor { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color DownBackColor { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color ChromeBorderColor { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowFrame { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Interactive { get; set; }

        public ChromeNavButton()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = Interactive;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (Interactive && mevent.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }

            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? BackColor);
            var rect = ClientRectangle;
            rect.Inflate(-2, -2);
            var fill = Interactive && _pressed ? DownBackColor : Interactive && _hovered ? HoverBackColor : BaseBackColor;

            if (ShowFrame || (Interactive && (_hovered || _pressed)))
            {
                using var path = RoundedRectangle(rect, 7);
                using var brush = new SolidBrush(fill);
                using var pen = new Pen(ChromeBorderColor);
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }

            DrawGlyph(e.Graphics, rect, ForeColor, IconGlyph);
        }

        private static void DrawGlyph(Graphics graphics, Rectangle bounds, Color color, ChromeNavGlyph glyph)
        {
            using var pen = new Pen(color, 1.85f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            var cx = bounds.Left + bounds.Width / 2f;
            var cy = bounds.Top + bounds.Height / 2f;
            if (glyph == ChromeNavGlyph.Back)
            {
                var left = bounds.Left + 7.5f;
                var right = bounds.Right - 7f;
                graphics.DrawLine(pen, right, cy, left, cy);
                graphics.DrawLine(pen, left + 5f, cy - 5f, left, cy);
                graphics.DrawLine(pen, left + 5f, cy + 5f, left, cy);
                return;
            }

            var roofLeft = bounds.Left + 7f;
            var roofRight = bounds.Right - 7f;
            var roofTop = bounds.Top + 6.5f;
            var wallTop = bounds.Top + 11.5f;
            var wallLeft = bounds.Left + 9f;
            var wallRight = bounds.Right - 9f;
            var wallBottom = bounds.Bottom - 6f;
            graphics.DrawLine(pen, roofLeft, wallTop, cx, roofTop);
            graphics.DrawLine(pen, cx, roofTop, roofRight, wallTop);
            graphics.DrawLine(pen, wallLeft, wallTop, wallLeft, wallBottom);
            graphics.DrawLine(pen, wallRight, wallTop, wallRight, wallBottom);
            graphics.DrawLine(pen, wallLeft, wallBottom, wallRight, wallBottom);
            graphics.DrawLine(pen, cx, wallBottom, cx, wallBottom - 3.5f);
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private void ApplyChromeTheme(bool light)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ApplyChromeTheme(light)));
            return;
        }

        _chromeLight = light;
        BackColor = ChromeWindowBackColor();
        if (_chromeBar is not null)
        {
            _chromeBar.BackColor = ChromeBarBackColor();
            _chromeBar.Invalidate();
        }

        foreach (var button in _chromeButtons)
        {
            ApplyChromeButtonTheme(button);
        }
    }

    private void HandleWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "appearance", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("colorScheme", out var colorScheme))
            {
                return;
            }

            ApplyChromeTheme(string.Equals(colorScheme.GetString(), "light", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
        }
    }

    private void UpdateNavigationChrome()
    {
        SetBackButtonAvailability(_web.CoreWebView2?.CanGoBack == true);
    }

    private void SetBackButtonAvailability(bool canGoBack)
    {
        _backButtonCanGoBack = canGoBack;
        if (_backButton is null)
        {
            return;
        }

        _backButton.Enabled = true;
        ApplyChromeButtonTheme(_backButton);
        _chromeToolTip.SetToolTip(_backButton, canGoBack ? "Back" : "No previous page");
    }

    public void NavigateTo(string path)
    {
        var core = _web.CoreWebView2;
        if (core is not null)
        {
            core.Navigate(_baseUrl + path);
        }
    }

    /// <summary>Bring the window back from the tray and optionally navigate to a page.</summary>
    public void RestoreAndNavigate(string? path)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => RestoreAndNavigate(path)));
            return;
        }

        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
        if (path is not null)
        {
            NavigateTo(path);
        }
    }

    /// <summary>Let the next close actually close the window (called by the tray's Exit).</summary>
    public void AllowClose() => _exiting = true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X / Alt+F4 hides to tray instead of quitting; the tray Exit sets _exiting first.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    private void ShowRuntimeHint(Exception ex)
    {
        // WebView2 Evergreen runtime missing (or init failed) — render a fallback hint.
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(9, 9, 9),
            Font = new Font("Segoe UI", 11f),
            Padding = new Padding(24),
            Text = "The WebView2 runtime could not start, so the in-app window can't render.\n\n"
                 + "Install the Microsoft Edge WebView2 Runtime (Evergreen), then relaunch — "
                 + $"or open {_baseUrl}/app in your browser.\n\nDetails: {ex.Message}"
        };
        Controls.Add(hint);
        hint.BringToFront();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chromeToolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
