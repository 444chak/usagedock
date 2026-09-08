using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UsageDock.Ui;

/// <summary>
/// A small progress ring drawn directly in OnRender.
///
/// Drawing rather than composing shapes keeps the visual tree tiny, which matters when the
/// window is always on screen: fewer elements means less memory and no layout churn when the
/// percentage ticks.
/// </summary>
public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent),
        typeof(double),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(
        nameof(GlyphBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DimmedProperty = DependencyProperty.Register(
        nameof(Dimmed),
        typeof(bool),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
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

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Brush GlyphBrush
    {
        get => (Brush)GetValue(GlyphBrushProperty);
        set => SetValue(GlyphBrushProperty, value);
    }

    public bool Dimmed
    {
        get => (bool)GetValue(DimmedProperty);
        set => SetValue(DimmedProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var thickness = StrokeThickness;
        var radius = (size - thickness) / 2d;
        if (radius <= 0)
        {
            return;
        }

        var centre = new Point(ActualWidth / 2d, ActualHeight / 2d);

        var trackPen = new Pen(TrackBrush, thickness);
        trackPen.Freeze();
        dc.DrawEllipse(null, trackPen, centre, radius, radius);

        var percent = Math.Clamp(Percent, 0d, 100d);
        if (!Dimmed && percent > 0d)
        {
            var pen = new Pen(ProgressBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, BuildArc(centre, radius, percent));
        }

        DrawGlyph(dc, centre, size);
    }

    /// <summary>Arc starting at 12 o clock, running clockwise.</summary>
    private static Geometry BuildArc(Point centre, double radius, double percent)
    {
        if (percent >= 99.95d)
        {
            return new EllipseGeometry(centre, radius, radius);
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

    private void DrawGlyph(DrawingContext dc, Point centre, double size)
    {
        if (string.IsNullOrEmpty(Glyph))
        {
            return;
        }

        var text = new FormattedText(
            Glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Segoe UI"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal),
            size * 0.42d,
            GlyphBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point(centre.X - (text.Width / 2d), centre.Y - (text.Height / 2d)));
    }
}
