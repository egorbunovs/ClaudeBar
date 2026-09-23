using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeBar.Services;

namespace ClaudeBar;

/// <summary>
/// The sliders live here rather than in the context menu.
///
/// A WPF ContextMenu takes mouse capture for its own navigation, so a Slider placed inside
/// one never sees a continuous drag: the value only lurches when the menu happens to let an
/// event through. A small ordinary window gets normal input and updates live, which is the
/// whole point of a slider.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;

    public SettingsWindow(AppSettings settings, Action onChanged)
    {
        _settings = settings;
        _onChanged = onChanged;

        Title = "ClaudeBar settings";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Heading("ClaudeBar"));

        panel.Children.Add(Slider("Opacity", _settings.Opacity * 100, 20, 100, 1,
            v => $"{v:0}%",
            v => { _settings.Opacity = v / 100.0; _onChanged(); }));

        panel.Children.Add(Slider("Snap padding", _settings.SnapPadding, 0, 64, 1,
            v => $"{v:0} px",
            v => { _settings.SnapPadding = (int)v; _onChanged(); }));

        panel.Children.Add(Slider("Snap distance", _settings.SnapThreshold, 0, 200, 1,
            v => v <= 0 ? "off" : $"{v:0} px",
            v => _settings.SnapThreshold = (int)v));

        panel.Children.Add(Slider("Refresh every", _settings.PollSeconds, 15, 600, 5,
            v => $"{v:0}s",
            v => { _settings.PollSeconds = (int)v; _onChanged(); }));

        panel.Children.Add(Slider("Warn at", _settings.WarnAt, 10, 100, 1,
            v => $"{v:0}%",
            v => { _settings.WarnAt = v; _onChanged(); }));

        panel.Children.Add(Slider("Critical at", _settings.CriticalAt, 10, 100, 1,
            v => $"{v:0}%",
            v => { _settings.CriticalAt = v; _onChanged(); }));

        panel.Children.Add(Toggle("Show reset times in the mini pill", _settings.MiniResetTimes,
            v => { _settings.MiniResetTimes = v; _onChanged(); }));

        var close = new Button
        {
            Content = "Close",
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(14, 4, 14, 5),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand
        };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);

        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1C, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = panel
        };

        // Borderless, so it needs its own drag.
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* released early */ } };

        // Save once when it closes, rather than on every tick of every slider.
        Closed += (_, _) => _settings.Save();
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xFA)),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private static UIElement Toggle(string label, bool value, Action<bool> onChange)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xE4)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 4),
            Cursor = Cursors.Hand
        };
        box.Checked += (_, _) => onChange(true);
        box.Unchecked += (_, _) => onChange(false);
        return box;
    }

    private static UIElement Slider(string label, double value, double min, double max,
        double tick, Func<double, string> format, Action<double> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var text = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xE4)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        var readout = new TextBlock
        {
            Text = format(value),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB8)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var slider = new System.Windows.Controls.Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };

        slider.ValueChanged += (_, e) =>
        {
            readout.Text = format(e.NewValue);
            onChange(e.NewValue);
        };

        Grid.SetColumn(text, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        grid.Children.Add(text);
        grid.Children.Add(slider);
        grid.Children.Add(readout);
        return grid;
    }
}
