using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly AccountSwitcher _switcher = new();
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _countdown = new();
    private CancellationTokenSource _inFlight = new();
    private UsageSnapshot? _lastGood;
    private TrayController? _tray;
    private bool _mouseDown;
    private bool _dragging;
    private int _dragOffsetX, _dragOffsetY;
    private int _pressX, _pressY;
    private GhostWindow? _ghost;
    private ToastWindow? _toast;
    private SnapAnchor _pendingAnchor = SnapAnchor.Free;
    private int _consecutiveFailures;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        ApplyOpacity();
        ContextMenu = BuildMenu();

        MouseLeftButtonDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseLeftButtonUp += OnDragEnd;
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
        Rows.Opacity = 0.55;
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
            Rows.Opacity = 1.0;

            if (snapshot.Account is { Length: > 0 } who)
            {
                AccountLine.Text = who;
                AccountLine.Visibility = Visibility.Visible;
            }
            else
            {
                AccountLine.Visibility = Visibility.Collapsed;
            }

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
            // This dims the ROWS, never the shell: the shell's alpha belongs to the user's
            // opacity slider, and writing it here made the pill jump back to full opacity.
            Rows.Opacity = 0.55;
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
            AccountLine.Visibility = Visibility.Collapsed;
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

    /// <summary>
    /// Dragging is done by hand rather than with Window.DragMove, because DragMove runs its
    /// own blocking modal loop: nothing else gets a look in while it is up, so there is no
    /// way to update a snap preview as the pill moves.
    /// </summary>
    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var bounds = Native.GetBounds(Handle);
        if (bounds.Width == 0) return;

        var (cx, cy) = Native.CursorPosition();
        _dragOffsetX = cx - bounds.Left;
        _dragOffsetY = cy - bounds.Top;
        _pressX = cx;
        _pressY = cy;

        // Pressed, but not yet dragging: a plain click must not move the pill or flash a
        // ghost. The drag only begins once the pointer has actually travelled.
        _mouseDown = true;
        _dragging = false;
        _pendingAnchor = _settings.Anchor;

        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>Windows' own drag threshold, converted to the physical pixels used here.</summary>
    private bool PastDragThreshold(int cx, int cy)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;

        var minX = SystemParameters.MinimumHorizontalDragDistance * scale;
        var minY = SystemParameters.MinimumVerticalDragDistance * scale;
        return Math.Abs(cx - _pressX) >= minX || Math.Abs(cy - _pressY) >= minY;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown || e.LeftButton != MouseButtonState.Pressed) return;

        var (px, py) = Native.CursorPosition();
        if (!_dragging)
        {
            if (!PastDragThreshold(px, py)) return;
            _dragging = true;
        }

        var handle = Handle;
        var bounds = Native.GetBounds(handle);
        if (bounds.Width == 0) return;

        var (cx, cy) = Native.CursorPosition();
        var x = cx - _dragOffsetX;
        var y = cy - _dragOffsetY;
        Native.MoveTo(handle, x, y);

        // Work out where letting go would put it, and show that as a ghost.
        var monitor = ScreenService.FromPoint(x + bounds.Width / 2, y + bounds.Height / 2);
        _pendingAnchor = ScreenService.AnchorForDrop(monitor, x, y,
            bounds.Width, bounds.Height, _settings.SnapThreshold);

        if (_pendingAnchor == SnapAnchor.Free)
        {
            _ghost?.HideGhost();
            return;
        }

        var (gx, gy) = ScreenService.Place(monitor, _pendingAnchor,
            bounds.Width, bounds.Height, _settings.SnapPadding, 0, 0);

        _ghost ??= new GhostWindow();
        _ghost.ShowAt(gx, gy, bounds.Width, bounds.Height);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseDown) return;

        _mouseDown = false;
        ReleaseMouseCapture();

        // A click that never became a drag changes nothing.
        if (!_dragging) return;

        _dragging = false;
        _ghost?.HideGhost();

        var bounds = Native.GetBounds(Handle);
        if (bounds.Width == 0) return;

        var monitor = ScreenService.FromPoint(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);

        _settings.MonitorDeviceName = monitor.DeviceName;
        _settings.Anchor = _pendingAnchor;

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
        menu.Items.Add(snap);

        var settingsPanel = new MenuItem { Header = "Settings (sliders)..." };
        settingsPanel.Click += (_, _) => ShowSettingsWindow();
        menu.Items.Add(settingsPanel);

        menu.Items.Add(new Separator());

        menu.Items.Add(BuildAccountMenu());

        menu.Items.Add(new Separator());

        var startup = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = Autostart.IsEnabled()
        };
        startup.Click += (_, _) =>
        {
            Autostart.Toggle();
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

    // ---- account switching ---------------------------------------------------------

    private MenuItem BuildAccountMenu()
    {
        var root = new MenuItem { Header = "Account" };
        var accounts = _switcher.Accounts();
        var currentUuid = AccountStore.ReadAccountBlock()?["accountUuid"]?.GetValue<string>();

        if (accounts.Count == 0)
        {
            root.Items.Add(new MenuItem
            {
                Header = "No accounts saved yet",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var account in accounts)
            {
                var isCurrent = account.AccountUuid == currentUuid;
                var item = new MenuItem
                {
                    Header = account.Label + (isCurrent ? "  (current)" : ""),
                    ToolTip = account.Detail + $" — saved {account.CapturedAt.ToLocalTime():d MMM HH:mm}",
                    IsCheckable = true,
                    IsChecked = isCurrent,
                    IsEnabled = !isCurrent
                };
                var target = account;
                item.Click += (_, _) => SwitchAccount(target);
                root.Items.Add(item);
            }
        }

        root.Items.Add(new Separator());

        var save = new MenuItem
        {
            Header = "Save the signed-in account",
            ToolTip = "Copies the current sign-in into ClaudeBar so it can switch back to it later."
        };
        save.Click += (_, _) =>
        {
            var captured = _switcher.CaptureCurrent(out var error);
            Notify(captured is not null
                ? $"Saved {captured.Label}"
                : $"Could not save the account: {error}");
            ContextMenu = BuildMenu();
        };
        root.Items.Add(save);

        var add = new MenuItem
        {
            Header = "Add another account (opens a browser)...",
            ToolTip = "Runs: claude auth login. When it finishes, use 'Save the signed-in account'."
        };
        add.Click += (_, _) => StartLogin();
        root.Items.Add(add);

        return root;
    }

    private async void SwitchAccount(StoredAccount target)
    {
        Notify($"Switching to {target.Label}...");

        // The switch shells out to `claude auth status` to verify, so keep it off the UI thread.
        var result = await Task.Run(() => _switcher.SwitchTo(target));

        Notify(result.Ok
            ? result.Message + ". Running sessions pick this up when their token next refreshes."
            : result.Message);

        Diagnostics.Log(() => $"switch result: ok={result.Ok} rolledBack={result.RolledBack} {result.Message}");

        ContextMenu = BuildMenu();
        if (result.Ok) await RefreshAsync();
    }

    private void StartLogin()
    {
        try
        {
            // A visible console: this is an interactive login and the user has to see it.
            Process.Start(new ProcessStartInfo("cmd.exe", "/k claude auth login")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Notify($"Could not start claude auth login ({ex.GetType().Name})");
        }
    }

    private void Notify(string message) => ShowToast("ClaudeBar", message, 0);

    /// <summary>
    /// ClaudeBar's own notification, placed above the pill rather than handed to the shell.
    /// </summary>
    public void ShowToast(string title, string body, int level)
    {
        Diagnostics.Log(() => $"toast[{level}]: {title} - {body}");

        var monitor = ScreenService.Resolve(_settings.MonitorDeviceName);
        var pill = Native.GetBounds(Handle);
        var bottom = pill.Height > 0 && IsVisible
            ? pill.Top - 8
            : monitor.Bottom - _settings.SnapPadding;

        _toast ??= new ToastWindow();
        _toast.Show(title, body, level,
            monitor.Right - _settings.SnapPadding, bottom,
            TimeSpan.FromSeconds(level > 0 ? 8 : 5));
    }

    /// <summary>
    /// Fades the whole pill. Staleness dims Rows.Opacity separately, and the two multiply,
    /// so neither fights the other for the same property.
    /// </summary>
    private void ApplyOpacity()
    {
        Opacity = Math.Clamp(_settings.Opacity, 0.2, 1.0);
        Diagnostics.Log(() => $"opacity: window={Opacity:0.00}");
    }

    private SettingsWindow? _settingsWindow;

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, ApplyLiveSettings);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ContextMenu = BuildMenu();
        };
        _settingsWindow.Show();
    }

    /// <summary>Applies whatever the sliders just changed, immediately.</summary>
    private void ApplyLiveSettings()
    {
        ApplyOpacity();
        Reposition();

        var interval = TimeSpan.FromSeconds(_settings.PollSeconds);
        if (_consecutiveFailures == 0 && _timer.Interval != interval)
            _timer.Interval = interval;

        // Thresholds changed: recolour what is already on screen without re-polling.
        if (_lastGood is not null) Render(_lastGood, keepLastGood: true);
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
