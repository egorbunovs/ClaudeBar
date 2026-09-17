using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeBar;

/// <summary>
/// The outline shown while dragging, marking where the pill will land when it is let go.
///
/// It is click-through and never activates, so it cannot interrupt the drag it is
/// previewing. Like everything else in placement, it is positioned in physical pixels.
/// </summary>
public sealed class GhostWindow : Window
{
    private readonly Rectangle _outline;

    public GhostWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Focusable = false;
        UseLayoutRounding = true;

        _outline = new Rectangle
        {
            RadiusX = 9,
            RadiusY = 9,
            StrokeThickness = 2,
            // Dashed, so it reads as a preview rather than a second pill. The fill is faint
            // enough to see the desktop through, which is the point of a landing marker.
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Stroke = new SolidColorBrush(Color.FromArgb(0xE0, 0x7D, 0xD3, 0xFC)),
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x7D, 0xD3, 0xFC))
        };

        Content = new Grid { Children = { _outline } };

        SourceInitialized += (_, _) =>
            Native.MakeGhost(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Show the outline at a physical-pixel rectangle.</summary>
    public void ShowAt(int x, int y, int width, int height)
    {
        if (!IsVisible) Show();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        // Size in DIPs (WPF), position in physical pixels (Win32) — same split the pill uses.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;

        Width = width / scale;
        Height = height / scale;
        Native.MoveTo(handle, x, y);
        Topmost = true;
    }

    public void HideGhost()
    {
        if (IsVisible) Hide();
    }
}
