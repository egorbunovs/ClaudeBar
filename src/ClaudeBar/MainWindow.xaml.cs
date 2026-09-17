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
    private readonly MultiAccountUsage _multi;
    private readonly AccountSwitcher _switcher = new();
    private readonly CredentialWatcher _credentials = new();
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
        _multi = new MultiAccountUsage(_usage);
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
        _countdown.Tick += (_, _) => RenderFromModel();
    }

    public void AttachTray(TrayController tray) => _tray = tray;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Reposition();

        if (Demo.Enabled)
        {
            // No timers, no watcher, no cache: the demo model never changes on its own, and
            // nothing here may touch a real credential file.
            RenderFromModel();
            return;
        }

        // Show the last known reading immediately so the pill is never blank on startup, and a
        // restart is not forced into a fresh call it would likely get a 429 for anyway.
        if (UsageCache.Load() is { } cached)
        {
            _lastGood = cached;
            SetActiveUsage(cached);
            RenderFromModel();
        }

        _timer.Start();
        _countdown.Start();

        // Notice sign-ins from anywhere: the pill's own button, /login in a terminal, anything.
        _credentials.Changed += uuid => Dispatcher.Invoke(() => OnCredentialsChanged(uuid));

        await RefreshAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(handle); // keeps it out of Alt-Tab; it is a readout, not an app
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
    }

    /// <summary>
    /// The credential file changed. Save whatever account is now signed in - so an account can
    /// never be signed into without ClaudeBar being able to switch back to it - and if it is a
    /// different account, say so and refresh straight away rather than waiting for the poll.
    /// </summary>
    private async void OnCredentialsChanged(string? uuid)
    {
        var switched = _credentials.AccountChanged(uuid);
        var captured = _switcher.CaptureCurrent(out var error);

        Diagnostics.Log(() =>
            $"credentials changed: uuid={uuid} switched={switched} captured={captured?.Label ?? error}");

        if (switched && captured is not null)
            ShowToast("Account changed", $"Now on {captured.Label}.", 0);

        await RefreshAsync();
    }

    // ---- the model -----------------------------------------------------------------
    //
    // One entry per stored account: the latest reading ClaudeBar has for it, however old.
    // Every render reads from here and filters by the CURRENT mode, so a fetch that finishes
    // after the view was collapsed can update the model but can never put a second account
    // on screen. This replaced a "last rendered list" that did exactly that.

    private readonly Dictionary<string, UsageSnapshot> _model = new();

    private void SetActiveUsage(UsageSnapshot snapshot)
    {
        var uuid = CredentialWatcher.CurrentUuid();
        if (uuid is not null) _model[uuid] = snapshot;
    }

    internal List<AccountUsage> CurrentAccounts()
    {
        if (Demo.Enabled) return Demo.Model(_settings.ShowAllAccounts);

        var activeUuid = CredentialWatcher.CurrentUuid();
        var stored = _switcher.Accounts();
        var list = new List<AccountUsage>();

        // The active account first, even if it is not (yet) in the store.
        var active = stored.FirstOrDefault(a => a.AccountUuid == activeUuid);
        if (active is null && activeUuid is not null)
            active = new StoredAccount(activeUuid, _lastGood?.Account, null, null, null, DateTimeOffset.UtcNow);
        if (active is not null)
            list.Add(new AccountUsage(active, true, _model.GetValueOrDefault(active.AccountUuid) ?? UsageSnapshot.Failed("loading")));

        if (!_settings.ShowAllAccounts) return list;

        foreach (var account in stored.Where(a => a.AccountUuid != activeUuid))
            list.Add(new AccountUsage(account, false,
                _model.GetValueOrDefault(account.AccountUuid) ?? UsageSnapshot.Failed("loading")));

        return list;
    }

    // ---- polling -------------------------------------------------------------------

    /// <param name="pollActive">
    /// False reuses the last reading of the active account instead of fetching it again.
    /// Expanding the view is the case: the active account is already on screen, and a second
    /// call within seconds of the last one is what the endpoint answers with a 429.
    /// </param>
    public async Task RefreshAsync(bool pollActive = true)
    {
        if (Demo.Enabled)
        {
            RenderFromModel();
            return;
        }

        _inFlight.Cancel();
        _inFlight = new CancellationTokenSource();
        var ct = _inFlight.Token;

        var madeACall = false;
        if (pollActive || _lastGood is null)
        {
            UsageSnapshot active;
            try { active = await _usage.PollAsync(ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            madeACall = true;

            ApplyBackoff(active);
            if (active.Ok)
            {
                _lastGood = active;
                UsageCache.Save(active);
                SetActiveUsage(active);
            }
            else if (_lastGood is not null)
            {
                // Keep the last good numbers on screen, carrying the error for the tooltip.
                SetActiveUsage(_lastGood with { Error = active.Error, RetryAfter = active.RetryAfter });
            }
            else
            {
                SetActiveUsage(active);
            }
        }

        RenderFromModel();
        if (!_settings.ShowAllAccounts) return;

        // The other accounts, one at a time, each drawn as soon as it arrives. Calls are
        // spaced out because the endpoint 429s two calls a couple of seconds apart.
        var activeUuid = CredentialWatcher.CurrentUuid();
        foreach (var account in _switcher.Accounts().Where(a => a.AccountUuid != activeUuid))
        {
            if (madeACall)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(1500), ct); }
                catch (OperationCanceledException) { return; }
            }

            UsageSnapshot snapshot;
            try { snapshot = await _multi.PollIdleAsync(account, ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            madeACall = true;

            // A failed refetch keeps the previous reading rather than replacing it with nothing.
            if (snapshot.HasData || !_model.ContainsKey(account.AccountUuid))
                _model[account.AccountUuid] = snapshot;
            else if (_model[account.AccountUuid].HasData)
                _model[account.AccountUuid] = _model[account.AccountUuid] with { Error = snapshot.Error };

            RenderFromModel();
        }
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

        // First retry waits one normal interval, then doubles: 60s, 120s, 240s ... capped.
        var backoff = TimeSpan.FromSeconds(
            _settings.PollSeconds * Math.Pow(2, Math.Min(_consecutiveFailures - 1, 5)));

        // Retry-After is authoritative, but only when it actually says to wait: the endpoint
        // has been seen returning 429 with "Retry-After: 0", which would defeat the backoff.
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

    // ---- rendering -----------------------------------------------------------------

    /// <summary>Draws whatever the model knows, filtered by the current mode. The only render path.</summary>
    private void RenderFromModel()
    {
        var accounts = CurrentAccounts();
        var active = accounts.FirstOrDefault(a => a.IsActive);

        Diagnostics.Log(() =>
            $"render: showAll={_settings.ShowAllAccounts} mini={_settings.Mini} " +
            $"[{string.Join(", ", accounts.Select(a => $"{a.Account.Label}:{(a.IsActive ? "active" : "idle")}:{a.Snapshot.Error ?? "ok"}:{a.Snapshot.Limits.Count}"))}]");

        var anyData = accounts.Any(a => a.Snapshot.HasData);

        if (anyData && _settings.Mini)
        {
            // Just the active account's numbers, one line. Everything else collapses.
            var source = active is { Snapshot.HasData: true } ? active : accounts.First(a => a.Snapshot.HasData);
            MiniRows.ItemsSource = source.Snapshot.Limits
                .Select(l => LimitRow.From(l, _settings.WarnAt, _settings.CriticalAt))
                .ToList();
            MiniPanel.Visibility = Visibility.Visible;
            Accounts.Visibility = Visibility.Collapsed;
            MessagePanel.Visibility = Visibility.Collapsed;
            ToolTip = source.Account.Label;
        }
        else if (anyData)
        {
            // Sections for every account in the current mode. An account with no reading yet
            // still gets its header, marked "loading", so expanding shows the list at once.
            Accounts.ItemsSource = accounts
                .Select(a => AccountView.From(a, _settings.WarnAt, _settings.CriticalAt, _settings.ShowAllAccounts))
                .ToList();
            Accounts.Visibility = Visibility.Visible;
            MiniPanel.Visibility = Visibility.Collapsed;
            MessagePanel.Visibility = Visibility.Collapsed;
            ToolTip = null;
        }
        else
        {
            Accounts.Visibility = Visibility.Collapsed;
            MiniPanel.Visibility = Visibility.Collapsed;
            MessagePanel.Visibility = Visibility.Visible;
            MessageText.Text = active?.Snapshot.Error switch
            {
                "Claude Code not found" => "Claude Code not found",
                "not signed in" or "no token" or "no subscription login" => "not signed in",
                "signed out" => "signed out - run /login",
                "token expired" => "waiting for Claude Code",
                "offline" => "offline",
                "rate limited" => "rate limited - retrying",
                "loading" => "starting…",
                _ => active?.Snapshot.Error ?? "unavailable"
            };
            ToolTip = "ClaudeBar - right-click for options";
        }

        // The tray and its warnings follow the ACTIVE account: that is the one being used.
        if (active is not null)
        {
            var snap = active.Snapshot;
            var worst = snap.Worst;
            _tray?.Update(
                !snap.HasData ? "ClaudeBar - " + (snap.Error ?? "starting")
                : worst is null ? "ClaudeBar"
                : worst.ShortLabel + " " + worst.Percent.ToString("0") + "%",
                snap.HasData && !snap.Ok ? snap with { Error = null } : snap);
        }

        Topmost = true; // re-assert: other topmost windows can win the z-order over time
    }

    // ---- placement -----------------------------------------------------------------

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// The transparent border around the shell, in device-independent pixels, that the drop
    /// shadow is drawn into. Must match the Margin on Shell in MainWindow.xaml.
    /// </summary>
    private const double ShadowDip = 10;

    /// <summary>
    /// The same margin in physical pixels, which is what every placement calculation is in.
    /// </summary>
    private int ShadowPx
    {
        get
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
            return (int)Math.Round(ShadowDip * scale);
        }
    }

    /// <summary>
    /// Where the pill IS, as opposed to where its window is: the window rect minus the
    /// shadow margin. Snapping, the ghost, the free-placement offsets and the toast all work
    /// from this, so the gap the user sees at a screen edge is the gap they asked for rather
    /// than that plus an invisible border.
    /// </summary>
    private Native.Rect PillBounds
    {
        get
        {
            var b = Native.GetBounds(Handle);
            if (b.Width == 0) return b;
            var m = ShadowPx;
            return new Native.Rect { Left = b.Left + m, Top = b.Top + m, Right = b.Right - m, Bottom = b.Bottom - m };
        }
    }

    public void Reposition()
    {
        UpdateLayout();
        var handle = Handle;
        if (handle == IntPtr.Zero) return;

        var bounds = PillBounds;
        var width = bounds.Width > 0 ? bounds.Width : 248;
        var height = bounds.Height > 0 ? bounds.Height : 48;

        var monitor = ScreenService.Resolve(_settings.MonitorDeviceName);
        var (x, y) = ScreenService.Place(monitor, _settings.Anchor, width, height,
            _settings.SnapPadding, _settings.OffsetX, _settings.OffsetY);

        // Place the pill there; the window itself starts a shadow's width earlier.
        Native.MoveTo(handle, x - ShadowPx, y - ShadowPx);

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

        // Where the pill now is, inside that window, and where letting go would put it.
        var m = ShadowPx;
        var pillX = x + m;
        var pillY = y + m;
        var pill = PillBounds;
        var width = pill.Width > 0 ? pill.Width : bounds.Width;
        var height = pill.Height > 0 ? pill.Height : bounds.Height;

        var monitor = ScreenService.FromPoint(pillX + width / 2, pillY + height / 2);
        _pendingAnchor = ScreenService.AnchorForDrop(monitor, pillX, pillY,
            width, height, _settings.SnapThreshold);

        if (_pendingAnchor == SnapAnchor.Free)
        {
            _ghost?.HideGhost();
            return;
        }

        var (gx, gy) = ScreenService.Place(monitor, _pendingAnchor,
            width, height, _settings.SnapPadding, 0, 0);

        _ghost ??= new GhostWindow();
        _ghost.ShowAt(gx, gy, width, height);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseDown) return;

        _mouseDown = false;
        ReleaseMouseCapture();

        // A click that never became a drag goes to whichever control is under the pointer.
        if (!_dragging)
        {
            RouteClick(e.GetPosition(this));
            return;
        }

        _dragging = false;
        _ghost?.HideGhost();

        var bounds = PillBounds;
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

    /// <summary>
    /// Finds the tagged control under a click and acts on it. The window captures the mouse
    /// for dragging, so elements inside the pill never receive Click themselves.
    /// </summary>
    private void RouteClick(Point point)
    {
        var hit = VisualTreeHelper.HitTest(this, point)?.VisualHit as DependencyObject;

        FrameworkElement? target = null;
        for (var node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Tag: string } fe)
            {
                target = fe;
                break;
            }
        }
        if (target is null) return;

        var view = target.DataContext as AccountView;
        switch (target.Tag as string)
        {
            case "switch":
                OpenAccountPicker();
                break;

            case "toggle":
                ToggleShowAll();
                break;

            case "mini":
                SetMini(true);
                break;

            case "restore":
                SetMini(false);
                break;

            case "radio":
                // In the expanded view the radios ARE the switcher.
                if (view is { IsActive: false, ShowingAll: true } &&
                    (Demo.Enabled ? Demo.Accounts : _switcher.Accounts())
                        .FirstOrDefault(a => a.AccountUuid == view.AccountUuid) is { } account)
                    SwitchAccount(account);
                break;
        }
    }

    private void SetMini(bool mini)
    {
        _settings.Mini = mini;
        _settings.Save();
        RenderFromModel();
    }

    private async void ToggleShowAll()
    {
        _settings.ShowAllAccounts = !_settings.ShowAllAccounts;
        _settings.Save();

        if (!_settings.ShowAllAccounts)
        {
            // Collapsing: stop any fetch that expanding started, and draw the active account.
            _inFlight.Cancel();
            RenderFromModel();
            return;
        }

        // Expanding: every account appears at once (from the model, or "loading"), and the
        // ones without a reading are fetched and drawn as they arrive. The active account
        // is not re-polled - it is already on screen.
        RenderFromModel();
        await RefreshAsync(pollActive: false);
    }

    // ---- menu ----------------------------------------------------------------------

    public ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += async (_, _) => await RefreshAsync();
        menu.Items.Add(refresh);

        menu.Items.Add(new Separator());

        // Every submenu repopulates when it opens. Built once, they went stale as soon as an
        // account was added or switched from anywhere else, or a monitor was plugged in.
        var monitors = new MenuItem { Header = "Show on monitor" };
        monitors.SubmenuOpened += (_, _) => PopulateMonitorMenu(monitors);
        PopulateMonitorMenu(monitors);
        menu.Items.Add(monitors);

        var snap = new MenuItem { Header = "Snap to" };
        snap.SubmenuOpened += (_, _) => PopulateSnapMenu(snap);
        PopulateSnapMenu(snap);
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

        var mini = new MenuItem
        {
            Header = _settings.Mini ? "Restore full pill" : "Minimize to just the numbers"
        };
        mini.Click += (_, _) => { SetMini(!_settings.Mini); ContextMenu = BuildMenu(); };
        menu.Items.Add(mini);

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

        // Which build this is, where a version is looked for: bottom of the menu, not clickable.
        menu.Items.Add(new MenuItem
        {
            Header = BuildInfo.MenuLabel,
            IsEnabled = false,
            ToolTip = "Version 1.0.<commit count>. Quote this when reporting anything."
        });

        var quit = new MenuItem { Header = "Quit ClaudeBar" };
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        return menu;
    }

    private void PopulateMonitorMenu(MenuItem monitors)
    {
        monitors.Items.Clear();
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
            };
            monitors.Items.Add(item);
        }
    }

    private void PopulateSnapMenu(MenuItem snap)
    {
        snap.Items.Clear();
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
            };
            snap.Items.Add(item);
        }
    }

    // ---- account switching ---------------------------------------------------------

    private MenuItem BuildAccountMenu()
    {
        var root = new MenuItem { Header = "Account" };

        // Repopulated every time it opens. Built once, it went stale the moment an account
        // was added or switched from anywhere else, and showed the wrong account as current.
        root.SubmenuOpened += (_, _) => PopulateAccountMenu(root);
        PopulateAccountMenu(root);
        return root;
    }

    private void PopulateAccountMenu(MenuItem root)
    {
        root.Items.Clear();
        foreach (var item in AccountItems()) root.Items.Add(item);
    }

    /// <summary>
    /// The account picker: left-click anywhere on the pill. Same items as the submenu, one
    /// click away instead of three.
    /// </summary>
    private void OpenAccountPicker()
    {
        var picker = new ContextMenu
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };
        foreach (var item in AccountItems()) picker.Items.Add(item);
        picker.IsOpen = true;
    }

    private IEnumerable<Control> AccountItems()
    {
        var accounts = Demo.Enabled ? Demo.Accounts : _switcher.Accounts();
        var currentUuid = Demo.Enabled ? Demo.ActiveUuid : CredentialWatcher.CurrentUuid();

        if (accounts.Count == 0)
        {
            yield return new MenuItem { Header = "No accounts yet - sign in once and it is saved", IsEnabled = false };
        }

        foreach (var account in accounts)
        {
            var isCurrent = account.AccountUuid == currentUuid;
            var item = new MenuItem
            {
                Header = account.Label + (isCurrent ? "   (current)" : ""),
                ToolTip = account.Detail,
                IsCheckable = true,
                IsChecked = isCurrent,
                IsEnabled = !isCurrent
            };
            var target = account;
            item.Click += (_, _) => SwitchAccount(target);
            yield return item;
        }

        yield return new Separator();

        var all = new MenuItem
        {
            Header = "Show every account's usage",
            IsCheckable = true,
            IsChecked = _settings.ShowAllAccounts,
            ToolTip = "See which account has headroom before switching."
        };
        all.Click += (_, _) => ToggleShowAll();
        yield return all;

        var add = new MenuItem
        {
            Header = "Add an account (opens a browser)...",
            ToolTip = "Runs: claude auth login. The new account is saved automatically."
        };
        add.Click += (_, _) => StartLogin();
        yield return add;
    }

    private async void SwitchAccount(StoredAccount target)
    {
        if (Demo.Enabled)
        {
            Demo.SwitchTo(target.AccountUuid);
            Notify($"Switched to {target.Label}.");
            RenderFromModel();
            return;
        }

        Notify($"Switching to {target.Label}...");

        // The switch shells out to `claude auth status` to verify, so keep it off the UI thread.
        _credentials.Suppressed = true;
        AccountSwitcher.Result result;
        try { result = await Task.Run(() => _switcher.SwitchTo(target)); }
        finally { _credentials.Suppressed = false; }

        Notify(result.Ok
            ? result.Message + ". Running sessions pick this up when their token next refreshes."
            : result.Message);

        Diagnostics.Log(() => $"switch result: ok={result.Ok} rolledBack={result.RolledBack} {result.Message}");

        if (result.Ok) await RefreshAsync();
    }

    private void StartLogin()
    {
        if (Demo.Enabled)
        {
            Notify("Demo mode: signing in is disabled.");
            return;
        }

        // Save the account being replaced first. `claude auth login` overwrites the live
        // credentials, so without this the account signed in right now could be lost.
        var outgoing = _switcher.CaptureCurrent(out _);

        var exe = AccountSwitcher.ClaudeExecutable();
        if (exe is null)
        {
            Notify("Could not find claude.exe to start a login");
            return;
        }

        try
        {
            // claude.exe directly, not `cmd /k`: the console belongs to the login and closes
            // with it, instead of leaving a stray window behind.
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("login");
            Process.Start(psi);

            ShowToast("Signing in",
                outgoing is null
                    ? "Finish in the browser. ClaudeBar will save the account automatically."
                    : $"Saved {outgoing.Label} first. Finish in the browser - the new account "
                      + "is saved automatically.", 0);
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
        var pill = PillBounds;
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
        RenderFromModel();
    }

    protected override void OnClosed(EventArgs e)
    {
        _inFlight.Cancel();
        _usage.Dispose();
        _credentials.Dispose();
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
