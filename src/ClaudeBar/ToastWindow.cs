using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeBar;

/// <summary>
/// ClaudeBar's own notification, shown next to the pill.
///
/// NotifyIcon.ShowBalloonTip is routed through the Windows toast system, which titles the
/// toast with the app's AppUserModelID when the app has no registered shell identity — so
/// warnings arrived headed "Microsoft.Explorer.Notification..." plus a hash. Registering a
/// Start Menu shortcut purely to fix a title is a lot of machinery for a status pill, and
/// this way the warning matches the pill's own styling and colour rules.
/// </summary>
public sealed class ToastWindow : Window
{
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly TextBlock _glyph;
    private readonly Border _shell;
    private readonly DispatcherTimer _dismiss = new();

    public ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        _glyph = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 0)
        };

        _title = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xFA)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        };

        _body = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xE4)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var text = new StackPanel();
        text.Children.Add(_title);
        text.Children.Add(_body);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_glyph);
        row.Children.Add(text);

        _shell = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 9, 14, 10),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x15, 0x19, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = row
        };

        Content = _shell;

        // Click to dismiss early.
        MouseLeftButtonDown += (_, _) => Hide();

        _dismiss.Tick += (_, _) => { _dismiss.Stop(); Hide(); };

        SourceInitialized += (_, _) =>
            Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// Shows a message. <paramref name="level"/> 0 = info, 1 = warning, 2 = critical, and
    /// like everywhere else it carries a glyph as well as a colour.
    /// </summary>
    public void Show(string title, string body, int level, int monitorX, int monitorBottom,
        TimeSpan duration)
    {
        _title.Text = title;
        _body.Text = body;
        _body.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;

        var (glyph, colour) = level switch
        {
            2 => ("■", Color.FromRgb(0xE8, 0x79, 0xF9)),
            1 => ("▲", Color.FromRgb(0xFB, 0xBF, 0x24)),
            _ => ("●", Color.FromRgb(0x38, 0xBD, 0xF8))
        };
        _glyph.Text = glyph;
        _glyph.Foreground = new SolidColorBrush(colour);
        _shell.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, colour.R, colour.G, colour.B));

        if (!IsVisible) Show();
        UpdateLayout();

        var handle = new WindowInteropHelper(this).Handle;
        var bounds = Native.GetBounds(handle);
        var width = bounds.Width > 0 ? bounds.Width : 300;
        var height = bounds.Height > 0 ? bounds.Height : 70;

        // Sit just above where the pill lives, on the same monitor.
        Native.MoveTo(handle, monitorX - width, monitorBottom - height);
        Topmost = true;

        _dismiss.Stop();
        _dismiss.Interval = duration;
        _dismiss.Start();
    }
}
