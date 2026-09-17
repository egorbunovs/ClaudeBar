using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClaudeBar.Models;
using ClaudeBar.Services;
using Microsoft.Win32;

namespace ClaudeBar;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly UsageService _usage = new();
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _countdown = new();
    private CancellationTokenSource _inFlight = new();
    private UsageSnapshot? _lastGood;
    private TrayController? _tray;
    private bool _dragging;
    private int _consecutiveFailures;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        ApplyBackgroundOpacity();
        ContextMenu = BuildMenu();

        MouseLeftButtonDown += OnDrag;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;

        // SizeToContent means the pill grows when the first reading replaces "starting...",
        // and again if the API ever reports a third window. Re-anchor whenever that happens,
        // or it creeps off the corner it was pinned to.
        SizeChanged += (_, _) => { if (!_dragging) Reposition(); };

        _timer.Interval = TimeSpan.FromSeconds(_settings.PollSeconds);
        _timer.Tick += async (_, _) => await RefreshAsync();

        // The reset countdown ticks on its own so the time left stays honest between polls.
        _countdown.Interval = TimeSpan.FromSeconds(30);
        _countdown.Tick += (_, _) => Render(_lastGood, keepLastGood: true);
    }

    public void AttachTray(TrayController tray) => _tray = tray;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Reposition();

        // Show the last known reading immediately, marked with when it was taken, so the
        // pill is never blank on startup and a restart is not forced into a fresh call.
        if (UsageCache.Load() is { } cached)
        {
            _lastGood = cached;
            Render(cached, keepLastGood: true);
            MarkStale(cached, "cached");
        }

        _timer.Start();
        _countdown.Start();
        await RefreshAsync();
    }

    private void MarkStale(UsageSnapshot snapshot, string reason)
    {
        Shell.Opacity = 0.55;
        ToolTip = $"{reason} - reading taken {snapshot.FetchedAt.ToLocalTime():HH:mm:ss}";
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(handle); // keeps it out of Alt-Tab; it is a readout, not an app
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
    }

    // ---- polling -------------------------------------------------------------------

    public async Task RefreshAsync()
    {
        _inFlight.Cancel();
        _inFlight = new CancellationTokenSource();
        var ct = _inFlight.Token;

        UsageSnapshot snapshot;
        try { snapshot = await _usage.PollAsync(ct); }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        ApplyBackoff(snapshot);
        Render(snapshot, keepLastGood: false);

        if (snapshot.Ok) UsageCache.Save(snapshot);
    }

    /// <summary>
    /// Spaces polls out after failures instead of retrying at full rate. Matters most for
    /// HTTP 429: the endpoint rate-limits, and hammering it only extends the penalty.
    /// </summary>
    private void ApplyBackoff(UsageSnapshot snapshot)
    {
        var normal = TimeSpan.FromSeconds(_settings.PollSeconds);

        if (snapshot.Ok)
        {
            _consecutiveFailures = 0;
            if (_timer.Interval != normal) _timer.Interval = normal;
            return;
        }

        _consecutiveFailures++;

        // Retry-After is authoritative, but only when it actually says to wait: the endpoint
        // has been seen returning 429 with "Retry-After: 0", which would defeat the backoff.
        // First retry waits one normal interval, then doubles: 60s, 120s, 240s ... capped.
        var backoff = TimeSpan.FromSeconds(
            _settings.PollSeconds * Math.Pow(2, Math.Min(_consecutiveFailures - 1, 5)));
        var wait = snapshot.RetryAfter is { TotalSeconds: > 0 } hinted && hinted > backoff
            ? hinted
            : backoff;

        var capped = TimeSpan.FromMinutes(15);
        if (wait > capped) wait = capped;
        if (wait < normal) wait = normal;

        _timer.Interval = wait;
        Diagnostics.Log(() =>
            $"backoff: {snapshot.Error} failures={_consecutiveFailures} next poll in {wait.TotalSeconds:0}s");
    }

    private void Render(UsageSnapshot? snapshot, bool keepLastGood)
    {
        if (snapshot is null) return;

        if (snapshot.Ok)
        {
            _lastGood = snapshot;
            var rows = snapshot.Limits
                .Select(l => LimitRow.From(l, _settings.WarnAt, _settings.CriticalAt))
                .ToList();
            Rows.ItemsSource = rows;
            Rows.Visibility = Visibility.Visible;
            MessagePanel.Visibility = Visibility.Collapsed;
            Shell.Opacity = 1.0;

            var lines = new List<string>
            {
                snapshot.Account is { } a ? "Account: " + a : "Claude Code"
            };
            lines.AddRange(rows.Select(r => r.Tooltip));
            lines.Add("Updated " + snapshot.FetchedAt.ToLocalTime().ToString("HH:mm:ss"));
            ToolTip = string.Join(Environment.NewLine, lines);

            var worst = snapshot.Worst;
            _tray?.Update(worst is null
                ? "ClaudeBar"
                : worst.ShortLabel + " " + worst.Percent.ToString("0") + "%", snapshot);
        }
        else if (_lastGood is not null)
        {
            // A blip while a good reading is on screen: leave the numbers, just dim them.
            Shell.Opacity = 0.55;
            if (!keepLastGood)
            {
                ToolTip = snapshot.Error + " - showing last reading from "
                          + _lastGood.FetchedAt.ToLocalTime().ToString("HH:mm:ss");
                _tray?.Update("ClaudeBar - " + snapshot.Error, snapshot);
            }
        }
        else
        {
            Rows.Visibility = Visibility.Collapsed;
            MessagePanel.Visibility = Visibility.Visible;
            MessageText.Text = snapshot.Error switch
            {
                "Claude Code not found" => "Claude Code not found",
                "not signed in" or "no token" or "no subscription login" => "not signed in",
                "signed out" => "signed out - run /login",
                "token expired" => "waiting for Claude Code",
                "offline" => "offline",
                "rate limited" => "rate limited - retrying",
                _ => snapshot.Error ?? "unavailable"
            };
            ToolTip = "ClaudeBar - right-click for options";
            _tray?.Update("ClaudeBar - " + MessageText.Text, snapshot);
        }

        Topmost = true; // re-assert: other topmost windows can win the z-order over time
    }

    // ---- placement -----------------------------------------------------------------

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    public void Reposition()
    {
        UpdateLayout();
        var handle = Handle;
        if (handle == IntPtr.Zero) return;

        var bounds = Native.GetBounds(handle);
        var width = bounds.Width > 0 ? bounds.Width : 248;
        var height = bounds.Height > 0 ? bounds.Height : 48;

        var monitor = ScreenService.Resolve(_settings.MonitorDeviceName);
        var (x, y) = ScreenService.Place(monitor, _settings.Anchor, width, height,
            _settings.SnapPadding, _settings.OffsetX, _settings.OffsetY);
        Native.MoveTo(handle, x, y);

        Diagnostics.Log(() =>
            $"place: monitor={monitor.DeviceName} anchor={_settings.Anchor} " +
            $"pad={_settings.SnapPadding} work=({monitor.Left},{monitor.Top})-" +
            $"({monitor.Right},{monitor.Bottom}) size={width}x{height} -> ({x},{y})");
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _dragging = true;
        try { DragMove(); }
        catch { return; }
        finally { _dragging = false; }

        // Remember where it was put, on whichever monitor it was dropped. Measured in
        // physical pixels so the offset means the same thing on every monitor.
        var bounds = Native.GetBounds(Handle);
        if (bounds.Width == 0) return;

        var monitor = ScreenService.FromPoint(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);

        _settings.MonitorDeviceName = monitor.DeviceName;
        _settings.Anchor = ScreenService.AnchorForDrop(monitor, bounds.Left, bounds.Top,
            bounds.Width, bounds.Height, _settings.SnapThreshold);

        // Free placement keeps the exact drop position; a snapped one is re-laid out.
        if (_settings.Anchor == SnapAnchor.Free)
        {
            _settings.OffsetX = monitor.Right - bounds.Right;
            _settings.OffsetY = monitor.Bottom - bounds.Bottom;
        }

        _settings.Save();
        Reposition();
        ContextMenu = BuildMenu();
    }

    // ---- menu ----------------------------------------------------------------------

    public ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += async (_, _) => await RefreshAsync();
        menu.Items.Add(refresh);

        menu.Items.Add(new Separator());

        // ---- where it sits ----
        var monitors = new MenuItem { Header = "Show on monitor" };
        var active = ScreenService.Resolve(_settings.MonitorDeviceName).DeviceName;
        foreach (var m in ScreenService.All())
        {
            var item = new MenuItem
            {
                Header = m.Label,
                IsCheckable = true,
                IsChecked = m.DeviceName == active
            };
            var device = m.DeviceName;
            item.Click += (_, _) =>
            {
                _settings.MonitorDeviceName = device;
                _settings.Save();
                Reposition();
                ContextMenu = BuildMenu();
            };
            monitors.Items.Add(item);
        }
        menu.Items.Add(monitors);

        var snap = new MenuItem { Header = "Snap to" };
        foreach (var (anchor, label) in new[]
                 {
                     (SnapAnchor.BottomRight,  "Bottom right (above the clock)"),
                     (SnapAnchor.BottomCentre, "Bottom centre"),
                     (SnapAnchor.BottomLeft,   "Bottom left"),
                     (SnapAnchor.TopRight,     "Top right"),
                     (SnapAnchor.TopCentre,    "Top centre"),
                     (SnapAnchor.TopLeft,      "Top left"),
                     (SnapAnchor.Free,         "Free (wherever it is dropped)")
                 })
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = _settings.Anchor == anchor
            };
            var target = anchor;
            item.Click += (_, _) =>
            {
                _settings.Anchor = target;
                _settings.Save();
                Reposition();
                ContextMenu = BuildMenu();
            };
            snap.Items.Add(item);
        }
        snap.Items.Add(new Separator());
        snap.Items.Add(SliderItem("Padding", _settings.SnapPadding, 0, 64, 1,
            v => $"{v:0} px",
            v => { _settings.SnapPadding = (int)v; Reposition(); },
            () => _settings.Save()));
        snap.Items.Add(SliderItem("Snap distance", _settings.SnapThreshold, 0, 200, 4,
            v => v <= 0 ? "off" : $"{v:0} px",
            v => _settings.SnapThreshold = (int)v,
            () => _settings.Save()));
        menu.Items.Add(snap);

        // ---- how it looks ----
        var look = new MenuItem { Header = "Appearance" };
        look.Items.Add(SliderItem("Background", _settings.BackgroundOpacity * 100, 0, 100, 5,
            v => $"{v:0}%",
            v => { _settings.BackgroundOpacity = v / 100.0; ApplyBackgroundOpacity(); },
            () => _settings.Save()));
        menu.Items.Add(look);

        menu.Items.Add(new Separator());

        var switcher = new MenuItem
        {
            Header = "Switch account...",
            IsEnabled = false,
            ToolTip = "Next up - the credential swap is designed but not wired yet."
        };
        menu.Items.Add(switcher);

        var allAccounts = new MenuItem
        {
            Header = "Show all accounts",
            IsCheckable = true,
            IsChecked = _settings.ShowAllAccounts,
            IsEnabled = false,
            ToolTip = "Available once accounts can be added - see README, Phase 2."
        };
        menu.Items.Add(allAccounts);

        menu.Items.Add(new Separator());

        var startup = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = Autostart.IsEnabled()
        };
        startup.Click += (_, _) =>
        {
            _settings.StartWithWindows = Autostart.Toggle();
            _settings.Save();
            ContextMenu = BuildMenu();
        };
        menu.Items.Add(startup);

        var hide = new MenuItem { Header = "Hide (tray icon keeps running)" };
        hide.Click += (_, _) => { Hide(); _settings.Visible = false; _settings.Save(); };
        menu.Items.Add(hide);

        var settingsItem = new MenuItem { Header = "Open settings.json" };
        settingsItem.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(_settings.Path) { UseShellExecute = true }); }
            catch { /* no editor associated; nothing worth crashing over */ }
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = "Quit ClaudeBar" };
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        return menu;
    }

    /// <summary>
    /// A menu row carrying a live slider. Changes apply as it is dragged so the effect is
    /// visible while choosing, and are saved once on release rather than on every tick.
    /// </summary>
    private static MenuItem SliderItem(string label, double value, double min, double max,
        double tick, Func<double, string> format, Action<double> onChange, Action onCommit)
    {
        var text = new TextBlock
        {
            Text = label,
            Width = 96,
            VerticalAlignment = VerticalAlignment.Center
        };

        var readout = new TextBlock
        {
            Text = format(value),
            Width = 44,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            Width = 130,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };

        slider.ValueChanged += (_, e) =>
        {
            readout.Text = format(e.NewValue);
            onChange(e.NewValue);
        };
        slider.PreviewMouseUp += (_, _) => onCommit();
        slider.LostMouseCapture += (_, _) => onCommit();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(text);
        panel.Children.Add(slider);
        panel.Children.Add(readout);

        // Without this the menu closes the moment the slider is grabbed.
        return new MenuItem { Header = panel, StaysOpenOnClick = true };
    }

    private void ApplyBackgroundOpacity() => ShellFill.Opacity = _settings.BackgroundOpacity;

    protected override void OnClosed(EventArgs e)
    {
        _inFlight.Cancel();
        _usage.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>HKCU Run key. User-scoped, so no elevation and nothing to clean up system-wide.</summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ClaudeBar";

    private static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string;
        }
        catch { return false; }
    }

    public static bool Toggle()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (k is null) return false;
            if (IsEnabled()) { k.DeleteValue(Name, false); return false; }
            k.SetValue(Name, "\"" + ExePath + "\"");
            return true;
        }
        catch { return false; }
    }
}
