using System.Runtime.ExceptionServices;

namespace Verbeam.Api.Tray;

public sealed class CapturedScreenRegion : IDisposable
{
    public CapturedScreenRegion(Bitmap image, Rectangle bounds)
    {
        Image = image;
        Bounds = bounds;
    }

    public Bitmap Image { get; }

    public Rectangle Bounds { get; }

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Captures the virtual screen first, then lets the user select region(s) on the frozen bitmap.
/// This keeps moving video/game frames stable while dragging. Single mode returns one captured
/// region (with its cropped image). Multi mode is a small editor: drag an empty area to add a box,
/// drag inside a box to move it, drag a corner handle to resize it, Del / right-click to remove the
/// box under the cursor; each box shows its number. Enter finishes, Esc cancels; returns the boxes'
/// screen-coordinate bounds.
/// </summary>
public sealed class FrozenRegionSelectionOverlay : Form
{
    private enum Drag { None, Draw, Move, NW, NE, SW, SE }

    private const int HandleHit = 10;   // px radius for grabbing a corner
    private const int MinSize = 8;

    private readonly Bitmap _screenImage;
    private readonly Rectangle _virtualBounds;
    private readonly bool _multi;
    private readonly List<Rectangle> _committed = new();
    private Point _start;
    private Rectangle _selection = Rectangle.Empty;
    private bool _dragging;                 // single mode
    private Drag _mode = Drag.None;         // multi mode
    private int _activeIndex = -1;          // box being moved/resized
    private Rectangle _origRect;            // box bounds at drag start
    private Point _dragStart;
    private Point _mouse;
    private CapturedScreenRegion? _result;
    private IReadOnlyList<Rectangle>? _multiResult;

    private FrozenRegionSelectionOverlay(Bitmap screenImage, Rectangle virtualBounds, bool multi)
    {
        _screenImage = screenImage;
        _virtualBounds = virtualBounds;
        _multi = multi;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualBounds;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    /// <summary>Single-region capture (returns the cropped image + bounds), or null if cancelled.</summary>
    public static CapturedScreenRegion? CaptureRegion()
        => RunOnSta(() =>
        {
            var virtualBounds = SystemInformation.VirtualScreen;
            using var screenImage = CaptureVirtualScreen(virtualBounds);
            using var overlay = new FrozenRegionSelectionOverlay(screenImage, virtualBounds, multi: false);
            overlay.ShowDialog();
            return overlay._result;
        });

    /// <summary>Multi-region capture: screen-coordinate bounds of every framed rectangle, or null
    /// if the user cancelled (Esc) or framed nothing.</summary>
    public static IReadOnlyList<Rectangle>? CaptureRegions(IReadOnlyList<Rectangle>? initial = null)
        => RunOnSta(() =>
        {
            var virtualBounds = SystemInformation.VirtualScreen;
            using var screenImage = CaptureVirtualScreen(virtualBounds);
            using var overlay = new FrozenRegionSelectionOverlay(screenImage, virtualBounds, multi: true);
            if (initial is not null)
            {
                // Pre-load the current regions (screen coords → client coords) so the user can move /
                // resize / delete them, then re-confirm with Enter.
                foreach (var r in initial)
                {
                    overlay._committed.Add(new Rectangle(r.X - virtualBounds.X, r.Y - virtualBounds.Y, r.Width, r.Height));
                }
            }

            overlay.ShowDialog();
            return overlay._multiResult;
        });

    private static T RunOnSta<T>(Func<T> core)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return core();
        }

        T result = default!;
        ExceptionDispatchInfo? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = core();
            }
            catch (Exception ex)
            {
                exception = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        exception?.Throw();
        return result;
    }

    private static Bitmap CaptureVirtualScreen(Rectangle virtualBounds)
    {
        var bitmap = new Bitmap(virtualBounds.Width, virtualBounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            virtualBounds.Location,
            Point.Empty,
            virtualBounds.Size,
            CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _mouse = e.Location;

        if (_multi)
        {
            if (e.Button == MouseButtons.Right)
            {
                DeleteBoxAt(e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            MultiMouseDown(e.Location);
            return;
        }

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
        _mouse = e.Location;

        if (_multi)
        {
            MultiMouseMove(e.Location);
            return;
        }

        if (!_dragging)
        {
            return;
        }

        _selection = NormalizeRectangle(_start, e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_multi)
        {
            if (_mode == Drag.Draw)
            {
                if (_selection.Width >= MinSize && _selection.Height >= MinSize)
                {
                    _committed.Add(_selection);
                }

                _selection = Rectangle.Empty;
            }
            else if (_mode != Drag.None && _activeIndex >= 0 && _activeIndex < _committed.Count)
            {
                // Drop a box shrunk below the minimum during a resize.
                var r = _committed[_activeIndex];
                if (r.Width < MinSize || r.Height < MinSize)
                {
                    _committed.RemoveAt(_activeIndex);
                }
            }

            _mode = Drag.None;
            _activeIndex = -1;
            Invalidate();
            return;
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _selection = NormalizeRectangle(_start, e.Location);
        if (_selection.Width >= MinSize && _selection.Height >= MinSize)
        {
            var selectedImage = _screenImage.Clone(_selection, _screenImage.PixelFormat);
            _result = new CapturedScreenRegion(selectedImage, ToScreen(_selection));
        }

        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            _result = null;
            _multiResult = null;
            Close();
        }
        else if (_multi && e.KeyCode == Keys.Enter)
        {
            _multiResult = _committed.Select(ToScreen).ToList();
            Close();
        }
        else if (_multi && (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back))
        {
            DeleteBoxAt(_mouse);
        }
    }

    private void MultiMouseDown(Point pt)
    {
        // Topmost box first: a corner handle starts a resize, the interior starts a move.
        for (var i = _committed.Count - 1; i >= 0; i--)
        {
            var corner = HitCorner(_committed[i], pt);
            if (corner != Drag.None)
            {
                _mode = corner;
                _activeIndex = i;
                _origRect = _committed[i];
                _dragStart = pt;
                return;
            }
        }

        for (var i = _committed.Count - 1; i >= 0; i--)
        {
            if (_committed[i].Contains(pt))
            {
                _mode = Drag.Move;
                _activeIndex = i;
                _origRect = _committed[i];
                _dragStart = pt;
                return;
            }
        }

        // Empty area → draw a new box.
        _mode = Drag.Draw;
        _start = pt;
        _selection = new Rectangle(pt, Size.Empty);
        Invalidate();
    }

    private void MultiMouseMove(Point pt)
    {
        switch (_mode)
        {
            case Drag.Draw:
                _selection = NormalizeRectangle(_start, pt);
                Invalidate();
                break;
            case Drag.Move:
                var moved = _origRect;
                moved.Offset(pt.X - _dragStart.X, pt.Y - _dragStart.Y);
                _committed[_activeIndex] = ClampToClient(moved);
                Invalidate();
                break;
            case Drag.None:
                UpdateCursor(pt);
                break;
            default: // a resize corner
                _committed[_activeIndex] = ResizeRect(_origRect, _mode, pt);
                Invalidate();
                break;
        }
    }

    private void DeleteBoxAt(Point pt)
    {
        for (var i = _committed.Count - 1; i >= 0; i--)
        {
            if (_committed[i].Contains(pt))
            {
                _committed.RemoveAt(i);
                Invalidate();
                return;
            }
        }
    }

    private void UpdateCursor(Point pt)
    {
        for (var i = _committed.Count - 1; i >= 0; i--)
        {
            var corner = HitCorner(_committed[i], pt);
            if (corner is Drag.NW or Drag.SE)
            {
                Cursor = Cursors.SizeNWSE;
                return;
            }

            if (corner is Drag.NE or Drag.SW)
            {
                Cursor = Cursors.SizeNESW;
                return;
            }

            if (_committed[i].Contains(pt))
            {
                Cursor = Cursors.SizeAll;
                return;
            }
        }

        Cursor = Cursors.Cross;
    }

    private static Drag HitCorner(Rectangle box, Point pt)
    {
        if (Near(pt, box.Left, box.Top)) return Drag.NW;
        if (Near(pt, box.Right, box.Top)) return Drag.NE;
        if (Near(pt, box.Left, box.Bottom)) return Drag.SW;
        if (Near(pt, box.Right, box.Bottom)) return Drag.SE;
        return Drag.None;
    }

    private static bool Near(Point p, int x, int y)
        => Math.Abs(p.X - x) <= HandleHit && Math.Abs(p.Y - y) <= HandleHit;

    private static Rectangle ResizeRect(Rectangle orig, Drag corner, Point pt)
    {
        int l = orig.Left, t = orig.Top, r = orig.Right, b = orig.Bottom;
        switch (corner)
        {
            case Drag.NW: l = pt.X; t = pt.Y; break;
            case Drag.NE: r = pt.X; t = pt.Y; break;
            case Drag.SW: l = pt.X; b = pt.Y; break;
            case Drag.SE: r = pt.X; b = pt.Y; break;
        }

        return Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
    }

    private Rectangle ClampToClient(Rectangle r)
    {
        var x = Math.Max(0, Math.Min(r.X, ClientSize.Width - r.Width));
        var y = Math.Max(0, Math.Min(r.Y, ClientSize.Height - r.Height));
        return new Rectangle(x, y, r.Width, r.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawImageUnscaled(_screenImage, Point.Empty);

        if (_multi)
        {
            using var dimAll = new SolidBrush(Color.FromArgb(90, Color.Black));
            e.Graphics.FillRectangle(dimAll, ClientRectangle);
            for (var i = 0; i < _committed.Count; i++)
            {
                DrawSelection(e.Graphics, _committed[i]);
                DrawHandles(e.Graphics, _committed[i]);
                DrawNumber(e.Graphics, _committed[i], i + 1);
            }

            if (_mode == Drag.Draw && _selection.Width > 0 && _selection.Height > 0)
            {
                DrawSelection(e.Graphics, _selection);
                DrawNumber(e.Graphics, _selection, _committed.Count + 1);
            }

            DrawHelpText(
                e.Graphics,
                $"{_committed.Count} region(s)  ·  drag empty = add  ·  drag inside = move  ·  corners = resize  ·  Del = remove  ·  Enter = done  ·  Esc = cancel");
            return;
        }

        if (_selection.Width <= 0 || _selection.Height <= 0)
        {
            using var dimAll = new SolidBrush(Color.FromArgb(90, Color.Black));
            e.Graphics.FillRectangle(dimAll, ClientRectangle);
            DrawHelpText(e.Graphics, "Drag to capture a region. Press Esc to cancel.");
            return;
        }

        using var dim = new SolidBrush(Color.FromArgb(120, Color.Black));
        foreach (var region in RegionsOutsideSelection(ClientRectangle, _selection))
        {
            e.Graphics.FillRectangle(dim, region);
        }

        DrawSelection(e.Graphics, _selection);
    }

    private static void DrawSelection(Graphics graphics, Rectangle rectangle)
    {
        using var fill = new SolidBrush(Color.FromArgb(35, 59, 130, 246));
        graphics.FillRectangle(fill, rectangle);
        using var pen = new Pen(Color.DeepSkyBlue, 2);
        graphics.DrawRectangle(pen, rectangle);
    }

    private static void DrawHandles(Graphics graphics, Rectangle box)
    {
        using var fill = new SolidBrush(Color.White);
        using var pen = new Pen(Color.DeepSkyBlue, 1);
        Span<Point> corners =
        [
            new(box.Left, box.Top),
            new(box.Right, box.Top),
            new(box.Left, box.Bottom),
            new(box.Right, box.Bottom)
        ];
        foreach (var c in corners)
        {
            var handle = new Rectangle(c.X - 4, c.Y - 4, 8, 8);
            graphics.FillRectangle(fill, handle);
            graphics.DrawRectangle(pen, handle);
        }
    }

    private static void DrawNumber(Graphics graphics, Rectangle box, int number)
    {
        const int diameter = 20;
        var badge = new Rectangle(box.Left, box.Top, diameter, diameter);
        using var bg = new SolidBrush(Color.FromArgb(235, 59, 130, 246));
        graphics.FillEllipse(bg, badge);
        using var font = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var text = new SolidBrush(Color.White);
        var label = number.ToString();
        var size = graphics.MeasureString(label, font);
        graphics.DrawString(label, font, text, badge.Left + (diameter - size.Width) / 2f, badge.Top + (diameter - size.Height) / 2f);
    }

    private Rectangle ToScreen(Rectangle clientRectangle)
        => new(
            clientRectangle.X + _virtualBounds.X,
            clientRectangle.Y + _virtualBounds.Y,
            clientRectangle.Width,
            clientRectangle.Height);

    private static Rectangle NormalizeRectangle(Point start, Point end)
        => Rectangle.FromLTRB(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X),
            Math.Max(start.Y, end.Y));

    private static IEnumerable<Rectangle> RegionsOutsideSelection(Rectangle bounds, Rectangle selection)
    {
        yield return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, selection.Top);
        yield return Rectangle.FromLTRB(bounds.Left, selection.Bottom, bounds.Right, bounds.Bottom);
        yield return Rectangle.FromLTRB(bounds.Left, selection.Top, selection.Left, selection.Bottom);
        yield return Rectangle.FromLTRB(selection.Right, selection.Top, bounds.Right, selection.Bottom);
    }

    private static void DrawHelpText(Graphics graphics, string text)
    {
        using var font = new Font("Segoe UI", 14f, FontStyle.Regular);
        var size = graphics.MeasureString(text, font);
        var x = Math.Max(16, (SystemInformation.VirtualScreen.Width - size.Width) / 2);
        var y = 32f;
        var box = new RectangleF(x - 14, y - 10, size.Width + 28, size.Height + 20);
        using var fill = new SolidBrush(Color.FromArgb(210, 16, 16, 16));
        using var border = new Pen(Color.FromArgb(90, 255, 255, 255));
        graphics.FillRectangle(fill, box);
        graphics.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
        using var brush = new SolidBrush(Color.White);
        graphics.DrawString(text, font, brush, x, y);
    }
}
