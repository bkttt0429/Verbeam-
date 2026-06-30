using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace Verbeam.Desktop.Avalonia;

public sealed class UsageGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<UsageGauge, double>(nameof(Value), 0d);

    public static readonly StyledProperty<string> CenterTextProperty =
        AvaloniaProperty.Register<UsageGauge, string>(nameof(CenterText), string.Empty);

    public static readonly StyledProperty<IBrush> AccentProperty =
        AvaloniaProperty.Register<UsageGauge, IBrush>(nameof(Accent), Brush.Parse("#22D3EE"));

    public static readonly StyledProperty<string> VariantProperty =
        AvaloniaProperty.Register<UsageGauge, string>(nameof(Variant), "liquid");

    private readonly DispatcherTimer _timer;
    private double _displayValue;
    private double _phase;

    public UsageGauge()
    {
        Width = 34;
        Height = 34;
        ClipToBounds = true;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(42)
        };
        _timer.Tick += (_, _) =>
        {
            var target = Clamp01(Value);
            _displayValue += (target - _displayValue) * 0.16;
            _phase += 0.16;
            InvalidateVisual();
        };
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string CenterText
    {
        get => GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    public IBrush Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public string Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _displayValue = Clamp01(Value);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty
            || change.Property == CenterTextProperty
            || change.Property == AccentProperty
            || change.Property == VariantProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var side = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height));
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Max(1, (side / 2) - 4.5);
        var innerRadius = Math.Max(1, radius - 2.1);
        var value = Clamp01(_displayValue);
        var accent = Accent;
        var ringBack = Brush.Parse("#0B111A");
        var strokeBack = new Pen(Brush.Parse("#263349"), 1.45);
        var strokeAccent = new Pen(accent, 2.05);

        context.DrawEllipse(ringBack, strokeBack, center, radius, radius);

        if (Variant.Equals("liquid", StringComparison.OrdinalIgnoreCase))
        {
            var liquidValue = value <= 0 ? 0 : Math.Max(value, 0.08);
            DrawLiquidFill(context, center, innerRadius, liquidValue, accent);
        }

        DrawArcSegments(context, center, radius, value, strokeAccent);

        if (!string.IsNullOrWhiteSpace(CenterText))
        {
            var text = new FormattedText(
                CenterText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("JetBrains Mono, Cascadia Code, Consolas"),
                CenterText.Length > 3 ? 7 : 8,
                Brush.Parse("#E8EEF8"));
            context.DrawText(text, center - new Vector(text.Width / 2, text.Height / 2));
        }
    }

    private void DrawLiquidFill(DrawingContext context, Point center, double radius, double value, IBrush accent)
    {
        var top = center.Y - radius;
        var left = center.X - radius;
        var size = radius * 2;
        var fillTop = top + size * (1 - value);
        var clip = new EllipseGeometry(new Rect(left, top, size, size));
        var baseBrush = WithOpacity(accent, 0.66);
        var primaryBrush = WithOpacity(accent, 0.9);
        var secondaryBrush = WithOpacity(accent, 0.42);

        using (context.PushGeometryClip(clip))
        {
            context.DrawRectangle(baseBrush, null, new Rect(left, fillTop, size, Math.Max(0, top + size - fillTop)));
            context.DrawGeometry(secondaryBrush, null, CreateLiquidWaveGeometry(left, fillTop + 0.7, size, radius, _phase * 0.55 + Math.PI, 0.7, 30));
            context.DrawGeometry(primaryBrush, null, CreateLiquidWaveGeometry(left, fillTop, size, radius, _phase * 0.75, 0.9, 34));
        }

        context.DrawEllipse(null, new Pen(Brush.Parse("#1E2C3F"), 1), center, radius, radius);
    }

    private static Geometry CreateLiquidWaveGeometry(double left, double fillTop, double size, double radius, double phase, double amplitudeScale, int steps)
    {
        var waveHeight = Math.Max(0.35, radius * 0.07 * amplitudeScale);
        var waveCycles = 0.72;
        var bottom = fillTop + size + radius;
        var geometry = new StreamGeometry();
        using var stream = geometry.Open();
        stream.BeginFigure(new Point(left, bottom), isFilled: true);
        stream.LineTo(new Point(left, fillTop));
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var x = left + size * t;
            var y = fillTop + Math.Sin(phase + t * Math.PI * 2 * waveCycles) * waveHeight;
            stream.LineTo(new Point(x, y));
        }
        stream.LineTo(new Point(left + size, bottom));
        stream.EndFigure(isClosed: true);
        return geometry;
    }

    private static IBrush WithOpacity(IBrush brush, double opacity)
    {
        if (brush is ISolidColorBrush solid)
        {
            return new SolidColorBrush(solid.Color, opacity);
        }

        return brush;
    }

    private static void DrawArcSegments(DrawingContext context, Point center, double radius, double value, Pen pen)
    {
        var totalSegments = 34;
        var activeSegments = (int)Math.Round(totalSegments * value);
        for (var i = 0; i < activeSegments; i++)
        {
            var angleA = (-90 + 360d * i / totalSegments) * Math.PI / 180;
            var angleB = (-90 + 360d * (i + 0.72) / totalSegments) * Math.PI / 180;
            var p1 = new Point(center.X + Math.Cos(angleA) * radius, center.Y + Math.Sin(angleA) * radius);
            var p2 = new Point(center.X + Math.Cos(angleB) * radius, center.Y + Math.Sin(angleB) * radius);
            context.DrawLine(pen, p1, p2);
        }
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }
        return Math.Clamp(value, 0d, 1d);
    }
}
