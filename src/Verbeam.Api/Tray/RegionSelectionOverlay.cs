namespace Verbeam.Api.Tray;

/// <summary>
/// Full-virtual-screen topmost overlay for drag-selecting a capture rectangle,
/// like a screenshot tool. Returns the selection in screen coordinates, or null
/// when the user presses Esc or releases a too-small rectangle.
/// </summary>
public sealed class RegionSelectionOverlay : Form
{
    private Point _start;
    private Rectangle _selection = Rectangle.Empty;
    private bool _dragging;
    private Rectangle? _result;

    private RegionSelectionOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.35;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    public static Rectangle? SelectRegion()
    {
        using var overlay = new RegionSelectionOverlay();
        overlay.ShowDialog();
        return overlay._result;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = true;
        _start = e.Location;
        _selection = new Rectangle(e.Location, Size.Empty);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        _selection = Rectangle.FromLTRB(
            Math.Min(_start.X, e.X),
            Math.Min(_start.Y, e.Y),
            Math.Max(_start.X, e.X),
            Math.Max(_start.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (_selection.Width >= 8 && _selection.Height >= 8)
        {
            // Client coordinates are offset by the virtual-screen origin (can be
            // negative with monitors left/above the primary one).
            _result = new Rectangle(
                _selection.X + Bounds.X,
                _selection.Y + Bounds.Y,
                _selection.Width,
                _selection.Height);
        }

        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            _result = null;
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_selection.Width <= 0 || _selection.Height <= 0)
        {
            return;
        }

        using var fill = new SolidBrush(Color.FromArgb(70, 47, 111, 255));
        e.Graphics.FillRectangle(fill, _selection);
        using var pen = new Pen(Color.DeepSkyBlue, 2);
        e.Graphics.DrawRectangle(pen, _selection);
    }
}
