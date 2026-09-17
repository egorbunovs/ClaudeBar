using System.Windows;
using System.Windows.Media;

namespace ClaudeBar;

/// <summary>
/// The account radio: a ring, filled when that account is the active one.
///
/// Drawn rather than composed, for the same reason <see cref="SegmentBar"/> is. Two earlier
/// versions both lost the left-hand side of the circle:
///
///   - as a Segoe MDL2 glyph, text hinting snapped it onto whole pixels and flattened the
///     left of the ring into a straight edge;
///   - as an Ellipse with a Stroke, the ring's left edge came out a single 65%-alpha pixel
///     while its right edge was a solid one, because the pill's padding and this control's
///     negative margin put the circle's box on a fractional physical pixel at 125% scaling.
///     A circle drawn half a pixel off-grid loses one side and doubles the other.
///
/// So: the diameter is a whole, even number of physical pixels, and the origin is snapped to
/// the pixel grid before anything is drawn. Both edges then get identical coverage, which is
/// what makes it read as a circle at 12 pixels across.
/// </summary>
public sealed class RadioDot : FrameworkElement
{
    private const double DiameterDip = 12;
    private const double RingDip = 1.4;
    private const double DotDip = 6;

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(RadioDot),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(RadioDot),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public RadioDot()
    {
        // Both of these are on for the window as a whole, and both are wrong for a circle:
        // they snap geometry edges onto whole pixels, which on a curve means one side of the
        // ring is pulled in and the other pushed out. A 12-pixel circle cannot survive that.
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
    }

    private double Scale
    {
        get
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return scale > 0 && !double.IsNaN(scale) ? scale : 1.0;
        }
    }

    /// <summary>
    /// A pixel of slack on every side. The circle used to fill its box exactly, so the
    /// fraction of a pixel it gets nudged by took its left edge outside the element - and
    /// the Border around it has a corner radius, which makes WPF clip whatever leaves.
    /// That clip, not the anti-aliasing, is what sliced the ring.
    /// </summary>
    private const int SlackPx = 1;

    /// <summary>Diameter in whole physical pixels, kept even so the centre lands on the grid.</summary>
    private int DiameterPx
    {
        get
        {
            var px = Math.Max(6, (int)Math.Round(DiameterDip * Scale));
            return px % 2 == 0 ? px : px + 1;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = (DiameterPx + 2 * SlackPx) / Scale;
        return new Size(side, side);
    }

    private static double Snap(double value, double block) => Math.Round(value / block) * block;

    /// <summary>
    /// How far to shift, in this control's own units, to land on a whole physical pixel.
    /// Works through whatever transform is between here and the window, so it is still right
    /// when the screenshot scale is on.
    /// </summary>
    private (double X, double Y) GridOffset(double scale)
    {
        try
        {
            if (Window.GetWindow(this) is not { } win) return (0, 0);

            var transform = TransformToAncestor(win);
            var origin = transform.Transform(new Point(0, 0));
            var unitX = transform.Transform(new Point(1, 0)).X - origin.X;
            var unitY = transform.Transform(new Point(0, 1)).Y - origin.Y;

            // The window itself is positioned in whole physical pixels, so "distance from the
            // window's edge" and "distance from the pixel grid" are the same question.
            var offsetX = origin.X * scale;
            var offsetY = origin.Y * scale;

            // Under the screenshot scale, N physical pixels become one pixel of the saved
            // image, so the grid that matters is N pixels wide. Landing on a whole physical
            // pixel but a third of a block leaves the shot's ring thin on one side.
            var block = Demo.Scale >= 1 ? Demo.Scale : 1;

            return (
                unitX > 0 ? (Snap(offsetX, block) - offsetX) / (unitX * scale) : 0,
                unitY > 0 ? (Snap(offsetY, block) - offsetY) / (unitY * scale) : 0);
        }
        catch
        {
            // Not in a window yet, or no transform to it: drawing un-nudged is still a
            // circle, just a softer one.
            return (0, 0);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var scale = Scale;
        var diameter = DiameterPx;
        var ring = Math.Max(2, (int)Math.Round(RingDip * scale));

        var dot = Math.Max(2, (int)Math.Round(DotDip * scale));
        if (dot % 2 != 0) dot++;

        // How far this control's own origin sits from a whole physical pixel, measured from
        // inside the window - the padding, the negative margin and a 10.5pt line above it
        // land it on a fraction (0.75 of a pixel, as it happens). A circle drawn three
        // quarters of a pixel to the right loses its left edge to anti-aliasing and doubles
        // its right one, which is the whole bug.
        //
        // Not PointToScreen: that rounds to whole screen pixels and so always reports the
        // offset as zero, which is what made this look fixed when it was not.
        var (nudgeX, nudgeY) = GridOffset(scale);

        var centre = new Point(
            nudgeX + (SlackPx + diameter / 2.0) / scale,
            nudgeY + (SlackPx + diameter / 2.0) / scale);

        Diagnostics.Log(() =>
            $"radio: scale={scale:0.###} ring={diameter}px/{ring}px dot={dot}px " +
            $"nudge={nudgeX * scale:0.###},{nudgeY * scale:0.###}px");

        // The pen straddles the radius, so the ring's OUTER edge lands on the diameter.
        var ringRadius = (diameter - ring) / 2.0 / scale;
        var pen = new Pen(Stroke, ring / scale);
        pen.Freeze();
        dc.DrawEllipse(null, pen, centre, ringRadius, ringRadius);

        if (Fill != Brushes.Transparent)
            dc.DrawEllipse(Fill, null, centre, dot / 2.0 / scale, dot / 2.0 / scale);
    }
}
