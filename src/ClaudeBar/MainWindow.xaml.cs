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

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        Shell.Opacity = _settings.Opacity;
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
        _timer.Start();
        _countdown.Start();
        await RefreshAsync();
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
        Render(snapshot, keepLastGood: false);
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
            Shell.Opacity = _settings.Opacity;

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
            Shell.Opacity = Math.Max(0.4, _settings.Opacity - 0.25);
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
        var (x, y) = ScreenService.AboveClock(monitor, width, height,
            _settings.OffsetX, _settings.OffsetY);
        Native.MoveTo(handle, x, y);

        Diagnostics.Log(() =>
            $"place: monitor={monitor.DeviceName} work=({monitor.Left},{monitor.Top})-" +
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
        _settings.OffsetX = monitor.Right - bounds.Right;
        _settings.OffsetY = monitor.Bottom - bounds.Bottom;
        _settings.PositionPinned = true;
        _settings.Save();
        ContextMenu = BuildMenu();
    }

    // ---- menu ----------------------------------------------------------------------

    public ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += async (_, _) => await RefreshAsync();
        menu.Items.Add(refresh);

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
                _settings.OffsetX = 12;
                _settings.OffsetY = 12;
                _settings.Save();
                Reposition();
                ContextMenu = BuildMenu();
            };
            monitors.Items.Add(item);
        }
        menu.Items.Add(monitors);

        var reset = new MenuItem { Header = "Reset to above the clock" };
        reset.Click += (_, _) =>
        {
            _settings.OffsetX = 12;
            _settings.OffsetY = 12;
            _settings.PositionPinned = false;
            _settings.Save();
            Reposition();
        };
        menu.Items.Add(reset);
        menu.Items.Add(new Separator());

        var switcher = new MenuItem
        {
            Header = "Switch account...",
            IsEnabled = false,
            ToolTip = "Next up - the credential swap is designed but not wired yet."
        };
        menu.Items.Add(switcher);
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
