using System.Windows;
using System.Windows.Media;

namespace UsageDock.Ui;

/// <summary>
/// Two concentric progress rings around a provider mark, drawn directly in OnRender.
///
/// The outer ring is the short window (five hours) and the inner ring the long one (weekly).
/// Drawing rather than composing shapes keeps the visual tree tiny, which matters for a window
/// that is on screen all the time: no layout pass happens when a percentage ticks.
/// </summary>
public sealed class UsageRing : FrameworkElement
{
    private const double OuterInset = 1d;
    private const double RingGap = 2.4d;

    public static readonly DependencyProperty PercentProperty = Register(nameof(Percent), 0d);
    public static readonly DependencyProperty SecondaryPercentProperty = Register(nameof(SecondaryPercent), 0d);
    public static readonly DependencyProperty HasSecondaryProperty = Register(nameof(HasSecondary), false);
    public static readonly DependencyProperty DimmedProperty = Register(nameof(Dimmed), false);
    public static readonly DependencyProperty StrokeThicknessProperty = Register(nameof(StrokeThickness), 3.2d);

    public static readonly DependencyProperty SecondaryStrokeThicknessProperty =
        Register(nameof(SecondaryStrokeThickness), 2.4d);

    public static readonly DependencyProperty TrackBrushProperty =
        Register<Brush>(nameof(TrackBrush), Brushes.DimGray);

    public static readonly DependencyProperty ProgressBrushProperty =
        Register<Brush>(nameof(ProgressBrush), Brushes.White);

    public static readonly DependencyProperty SecondaryBrushProperty =
        Register<Brush>(nameof(SecondaryBrush), Brushes.White);

    public static readonly DependencyProperty GlyphProperty =
        Register<GlyphSpec?>(nameof(Glyph), null);

    public static readonly DependencyProperty GlyphBrushProperty =
        Register<Brush>(nameof(GlyphBrush), Brushes.White);

    /// <summary>Short window usage, 0..100. Drives the outer ring.</summary>
    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    /// <summary>Long window usage, 0..100. Drives the inner ring.</summary>
    public double SecondaryPercent
    {
        get => (double)GetValue(SecondaryPercentProperty);
        set => SetValue(SecondaryPercentProperty, value);
    }

    public bool HasSecondary
    {
        get => (bool)GetValue(HasSecondaryProperty);
        set => SetValue(HasSecondaryProperty, value);
    }

    public bool Dimmed
    {
        get => (bool)GetValue(DimmedProperty);
        set => SetValue(DimmedProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double SecondaryStrokeThickness
    {
        get => (double)GetValue(SecondaryStrokeThicknessProperty);
        set => SetValue(SecondaryStrokeThicknessProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public Brush SecondaryBrush
    {
        get => (Brush)GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public GlyphSpec? Glyph
    {
        get => (GlyphSpec?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Brush GlyphBrush
    {
        get => (Brush)GetValue(GlyphBrushProperty);
        set => SetValue(GlyphBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var centre = new Point(ActualWidth / 2d, ActualHeight / 2d);

        var outerRadius = (size - StrokeThickness) / 2d - OuterInset;
        if (outerRadius <= 0)
        {
            return;
        }

        DrawRing(dc, centre, outerRadius, StrokeThickness, Percent, ProgressBrush);

        var innerRadius = outerRadius
                          - (StrokeThickness / 2d)
                          - RingGap
                          - (SecondaryStrokeThickness / 2d);

        if (HasSecondary && innerRadius > 0)
        {
            DrawRing(dc, centre, innerRadius, SecondaryStrokeThickness, SecondaryPercent, SecondaryBrush);
        }

        DrawGlyph(dc, centre, HasSecondary && innerRadius > 0 ? innerRadius : outerRadius);
    }

    private void DrawRing(
        DrawingContext dc, Point centre, double radius, double thickness, double percent, Brush brush)
    {
        var trackPen = new Pen(TrackBrush, thickness);
        trackPen.Freeze();
        dc.DrawEllipse(null, trackPen, centre, radius, radius);

        var value = Math.Clamp(percent, 0d, 100d);
        if (Dimmed || value <= 0d)
        {
            return;
        }

        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();

        dc.DrawGeometry(null, pen, BuildArc(centre, radius, value));
    }

    /// <summary>Arc starting at the top, running clockwise.</summary>
    private static Geometry BuildArc(Point centre, double radius, double percent)
    {
        if (percent >= 99.95d)
        {
            var full = new EllipseGeometry(centre, radius, radius);
            full.Freeze();
            return full;
        }

        var angle = percent / 100d * 2d * Math.PI;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(
            centre.X + (radius * Math.Sin(angle)),
            centre.Y - (radius * Math.Cos(angle)));

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = angle > Math.PI,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Scales the provider mark to sit inside the innermost ring.</summary>
    private void DrawGlyph(DrawingContext dc, Point centre, double innerRadius)
    {
        if (Glyph is not { } glyph)
        {
            return;
        }

        // Fill the hole left by the innermost ring, keeping a little air around the mark.
        var hole = innerRadius - (SecondaryStrokeThickness / 2d) - 0.7d;
        var box = Math.Max(6d, hole * 2d * 0.88d);
        var scale = box / GlyphSpec.DesignSize;

        dc.PushTransform(new TranslateTransform(
            centre.X - (GlyphSpec.DesignSize * scale / 2d),
            centre.Y - (GlyphSpec.DesignSize * scale / 2d)));
        dc.PushTransform(new ScaleTransform(scale, scale));

        if (glyph.StrokeThickness > 0d)
        {
            var pen = new Pen(GlyphBrush, glyph.StrokeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, glyph.Geometry);
        }
        else
        {
            dc.DrawGeometry(GlyphBrush, null, glyph.Geometry);
        }

        dc.Pop();
        dc.Pop();
    }

    private static DependencyProperty Register(string name, double defaultValue) =>
        Register<double>(name, defaultValue);

    private static DependencyProperty Register(string name, bool defaultValue) =>
        Register<bool>(name, defaultValue);

    private static DependencyProperty Register<T>(string name, T? defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(UsageRing),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
