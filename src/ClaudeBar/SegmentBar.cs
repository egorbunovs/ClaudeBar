using System.Windows;
using System.Windows.Media;

namespace ClaudeBar;

/// <summary>
/// The segmented usage bar, drawn directly so every segment is identical and every row
/// lines up exactly.
///
/// This replaces an ItemsControl of Rectangles, which looked subtly wrong: at 125% display
/// scaling a 6px-wide rectangle is 7.5 physical pixels, so WPF rounded some segments to 7
/// and others to 8, and the accumulated fractional offsets shifted one row against the
/// next. Sizes and positions are therefore computed in WHOLE PHYSICAL PIXELS here and
/// converted back to device-independent units only to draw.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    // Target sizes in device-independent units; rounded to whole physical pixels at render.
    private const double SegmentWidthDip = 7;
    private const double SegmentGapDip = 2.5;
    private const double SegmentHeightDip = 10;
    private const double CornerRadiusDip = 1.5;

    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(SegmentBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(nameof(Segments), typeof(int), typeof(SegmentBar),
            new FrameworkPropertyMetadata(12,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(SegmentBar),
            new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(SegmentBar),
            new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    private readonly record struct Metrics(int SegmentPx, int GapPx, int HeightPx, double Scale)
    {
        public double TotalWidthDip(int segments) =>
            (SegmentPx * segments + GapPx * Math.Max(0, segments - 1)) / Scale;

        public double HeightDip => HeightPx / Scale;
    }

    private Metrics Measure()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;

        return new Metrics(
            SegmentPx: Math.Max(1, (int)Math.Round(SegmentWidthDip * scale)),
            GapPx: Math.Max(1, (int)Math.Round(SegmentGapDip * scale)),
            HeightPx: Math.Max(1, (int)Math.Round(SegmentHeightDip * scale)),
            Scale: scale);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var m = Measure();
        return new Size(m.TotalWidthDip(Math.Max(1, Segments)), m.HeightDip);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var segments = Math.Max(1, Segments);
        var m = Measure();

        var pct = Math.Clamp(Percent, 0, 100);
        // Round up so any non-zero usage lights at least one segment.
        var filled = pct <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(pct / 100.0 * segments), 1, segments);

        var radius = CornerRadiusDip;
        var heightDip = m.HeightDip;
        var top = Math.Max(0, (ActualHeight - heightDip) / 2);

        for (var i = 0; i < segments; i++)
        {
            // Integer physical pixels in, DIPs out: identical segments, identical gaps,
            // and the same x positions on every row.
            var xPx = i * (m.SegmentPx + m.GapPx);
            var rect = new Rect(
                xPx / m.Scale,
                top,
                m.SegmentPx / m.Scale,
                heightDip);

            dc.DrawRoundedRectangle(i < filled ? Accent : Track, null, rect, radius, radius);
        }
    }
}
