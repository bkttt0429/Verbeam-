using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Verbeam.Api.Tray;

/// <summary>
/// Borderless always-on-top window that shows the latest translation next to the
/// captured region. Position is locked by default; the toolbar lock button must be
/// toggled before the overlay can be dragged, which avoids accidental moves while reading.
/// </summary>
public sealed class TranslationOverlayWindow : Form
{
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;
    private const int TransientStatusDelayMs = 150;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopMost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopMost = new(-1);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private readonly Label _textLabel;
    private readonly Label _statusLabel;
    private readonly FlowLayoutPanel _toolbar;
    private readonly Button _positionLockButton;
    private readonly ToolTip _toolTip = new();
    private readonly Button? _loopButton;
    private readonly System.Windows.Forms.Timer _transientStatusTimer;
    private string _text = string.Empty;
    private string _status = "idle";
    private string? _pendingTransientStatus;
    private bool _positionLocked = true;
    private bool _manualPosition;

    public event Action? SnapshotRequested;
    public event Action? LoopToggleRequested;
    public event Action? CloseRequested;

    public TranslationOverlayWindow(bool compact = false)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(16, 16, 16);
        ForeColor = Color.White;
        MinimumSize = new Size(220, 64);
        Padding = new Padding(10, 6, 10, 8);

        _toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Cursor = Cursors.Default
        };
        _toolbar.MouseDown += (_, e) => BeginToolbarDrag(e);

        _statusLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font(FontFamily.GenericMonospace, 8f),
            Margin = new Padding(0, 6, 8, 0),
            Text = _status
        };
        _statusLabel.MouseDown += (_, e) => BeginToolbarDrag(e);

        _transientStatusTimer = new System.Windows.Forms.Timer
        {
            Interval = TransientStatusDelayMs
        };
        _transientStatusTimer.Tick += (_, _) =>
        {
            _transientStatusTimer.Stop();
            if (_pendingTransientStatus is { Length: > 0 } status)
            {
                _pendingTransientStatus = null;
                ApplyStatus(status);
            }
        };

        _positionLockButton = CreateToolButton("LOCK", "Position locked. Click to allow dragging.");
        _positionLockButton.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        _positionLockButton.Click += (_, _) => TogglePositionLock();
        _toolbar.Controls.Add(_positionLockButton);

        if (!compact)
        {
            var closeButton = CreateToolButton("x", "Close overlay");
            closeButton.Click += (_, _) => CloseRequested?.Invoke();
            _loopButton = CreateToolButton("loop", "Toggle loop translation (Alt+Shift+L)");
            _loopButton.Click += (_, _) => LoopToggleRequested?.Invoke();
            var snapshotButton = CreateToolButton("snap", "Re-capture and translate (Alt+Shift+R)");
            snapshotButton.Click += (_, _) => SnapshotRequested?.Invoke();
            _toolbar.Controls.Add(closeButton);
            _toolbar.Controls.Add(_loopButton);
            _toolbar.Controls.Add(snapshotButton);
        }

        _toolbar.Controls.Add(_statusLabel);

        _textLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = new Font("Segoe UI", 12f, FontStyle.Regular),
            ForeColor = Color.White,
            Text = string.Empty,
            UseMnemonic = false
        };

        Controls.Add(_textLabel);
        Controls.Add(_toolbar);
        UpdatePositionLockVisual();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow | WsExNoActivate | WsExTopMost;
            return cp;
        }
    }

    private Button CreateToolButton(string text, string tooltip)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(32, 32, 32),
            Margin = new Padding(4, 0, 0, 0),
            Padding = new Padding(7, 1, 7, 1),
            MinimumSize = new Size(24, 22),
            TabStop = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 64);
        _toolTip.SetToolTip(button, tooltip);
        return button;
    }

    public void ShowFor(Rectangle captureRegion)
    {
        var scale = DeviceDpi / 96.0;
        int Scaled(int px) => (int)Math.Round(px * scale);

        var screen = Screen.FromRectangle(captureRegion).WorkingArea;
        var minWidth = Scaled(260);
        var margin = Scaled(8);
        var width = Math.Clamp(captureRegion.Width, minWidth, Math.Max(minWidth, screen.Width - Scaled(16)));
        var height = Scaled(110);
        var x = Math.Clamp(captureRegion.X, screen.Left + margin, Math.Max(screen.Left + margin, screen.Right - width - margin));
        var below = captureRegion.Bottom + margin;
        var y = below + height <= screen.Bottom - margin ? below : Math.Max(screen.Top + margin, captureRegion.Top - height - margin);

        if (_manualPosition)
        {
            var manualX = Math.Clamp(Left, screen.Left + margin, Math.Max(screen.Left + margin, screen.Right - width - margin));
            var manualY = Math.Clamp(Top, screen.Top + margin, Math.Max(screen.Top + margin, screen.Bottom - height - margin));
            Bounds = new Rectangle(manualX, manualY, width, height);
        }
        else
        {
            Bounds = new Rectangle(x, y, width, height);
        }

        if (!Visible)
        {
            Show();
        }

        EnsureTopMost();
    }

    public void SetText(string text)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => SetText(text));
            return;
        }

        if (string.Equals(_text, text, StringComparison.Ordinal))
        {
            return;
        }

        _text = text;
        _textLabel.Text = text;
        EnsureTopMost();
    }

    public void SetStatus(string status)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(status));
            return;
        }

        CancelPendingStatusCore();
        ApplyStatus(status);
    }

    public void SetTransientStatus(string status)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => SetTransientStatus(status));
            return;
        }

        if (string.Equals(_status, status, StringComparison.Ordinal) ||
            string.Equals(_pendingTransientStatus, status, StringComparison.Ordinal))
        {
            return;
        }

        _pendingTransientStatus = status;
        _transientStatusTimer.Stop();
        _transientStatusTimer.Start();
    }

    public void CancelPendingStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(CancelPendingStatus);
            return;
        }

        CancelPendingStatusCore();
    }

    public void SetLoopActive(bool active)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => SetLoopActive(active));
            return;
        }

        if (_loopButton is not null)
        {
            _loopButton.Text = active ? "loop on" : "loop";
        }
        EnsureTopMost();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureTopMost();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            EnsureTopMost();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_positionLocked ? Color.FromArgb(72, 72, 72) : Color.FromArgb(72, 150, 220));
        if (!_positionLocked)
        {
            pen.DashStyle = DashStyle.Dash;
        }

        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _transientStatusTimer.Dispose();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void TogglePositionLock()
    {
        _positionLocked = !_positionLocked;
        UpdatePositionLockVisual();
    }

    private void UpdatePositionLockVisual()
    {
        _positionLockButton.Text = _positionLocked ? "LOCK" : "MOVE";
        _positionLockButton.ForeColor = _positionLocked ? Color.FromArgb(190, 190, 190) : Color.FromArgb(120, 190, 255);
        _positionLockButton.BackColor = _positionLocked ? Color.FromArgb(32, 32, 32) : Color.FromArgb(24, 52, 74);
        _positionLockButton.FlatAppearance.BorderColor = _positionLocked ? Color.FromArgb(64, 64, 64) : Color.FromArgb(72, 150, 220);
        _toolbar.Cursor = _positionLocked ? Cursors.Default : Cursors.SizeAll;
        _statusLabel.Cursor = _toolbar.Cursor;
        _toolTip.SetToolTip(
            _positionLockButton,
            _positionLocked
                ? "Position locked. Click to allow dragging."
                : "Position unlocked. Drag the top bar to move; click to lock.");
        Invalidate();
        EnsureTopMost();
    }

    private void BeginToolbarDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _positionLocked)
        {
            return;
        }

        _manualPosition = true;
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, HtCaption, 0);
    }

    private void CancelPendingStatusCore()
    {
        _pendingTransientStatus = null;
        _transientStatusTimer.Stop();
    }

    private void ApplyStatus(string status)
    {
        if (string.Equals(_status, status, StringComparison.Ordinal))
        {
            return;
        }

        _status = status;
        _statusLabel.Text = status;
        EnsureTopMost();
    }

    private void EnsureTopMost()
    {
        if (IsDisposed || !IsHandleCreated || !Visible)
        {
            return;
        }

        TopMost = true;
        SetWindowPos(
            Handle,
            HwndTopMost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
    }
}
