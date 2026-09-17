using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using ClaudeBar.Models;
using ClaudeBar.Services;
using Forms = System.Windows.Forms;

namespace ClaudeBar;

/// <summary>
/// Tray icon: show/hide the pill, carry the warnings, and stay alive when the pill is hidden.
/// The icon is drawn at runtime, so there is no .ico asset to keep in step with the palette.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly AppSettings _settings;
    private Icon? _current;
    private int _lastAnnouncedLevel = -1;

    public TrayController(MainWindow window, AppSettings settings)
    {
        _window = window;
        _settings = settings;

        _icon = new Forms.NotifyIcon
        {
            Text = "ClaudeBar",
            Visible = true,
            Icon = Render(null, 0)
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) TogglePill();
        };

        _icon.ContextMenuStrip = BuildMenu();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add("Show / hide", null, (_, _) => TogglePill());
        menu.Items.Add("Refresh now", null, async (_, _) => await _window.RefreshAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var monitors = new Forms.ToolStripMenuItem("Show on monitor");
        menu.Opening += (_, _) =>
        {
            // Rebuilt on open so hot-plugging a monitor is picked up without a restart.
            monitors.DropDownItems.Clear();
            var active = ScreenService.Resolve(_settings.MonitorDeviceName).DeviceName;
            foreach (var m in ScreenService.All())
            {
                var item = new Forms.ToolStripMenuItem(m.Label) { Checked = m.DeviceName == active };
                var device = m.DeviceName;
                item.Click += (_, _) =>
                {
                    _settings.MonitorDeviceName = device;
                    _settings.OffsetX = 12;
                    _settings.OffsetY = 12;
                    _settings.Save();
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (!_window.IsVisible) ShowPill();
                        _window.Reposition();
                    });
                };
                monitors.DropDownItems.Add(item);
            }
        };
        menu.Items.Add(monitors);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit ClaudeBar", null, (_, _) =>
            _window.Dispatcher.Invoke(() => Application.Current.Shutdown()));

        return menu;
    }

    private void TogglePill()
    {
        _window.Dispatcher.Invoke(() =>
        {
            if (_window.IsVisible) { _window.Hide(); _settings.Visible = false; }
            else ShowPill();
            _settings.Save();
        });
    }

    private void ShowPill()
    {
        _window.Show();
        _window.Reposition();
        _window.Topmost = true;
        _settings.Visible = true;
    }

    public void Update(string text, UsageSnapshot snapshot)
    {
        // NotifyIcon.Text is capped at 63 chars by the shell.
        _icon.Text = text.Length > 62 ? text[..62] : text;

        var worst = snapshot.Worst;
        var percent = worst?.Percent ?? 0;
        var level = !snapshot.Ok ? -1
            : percent >= _settings.CriticalAt ? 2
            : percent >= _settings.WarnAt ? 1
            : 0;

        var old = _current;
        _current = Render(snapshot.Ok ? percent : null, level);
        _icon.Icon = _current;
        old?.Dispose();

        // Warn on the way up only, once per threshold crossing. Uses ClaudeBar's own toast:
        // shell balloons get titled with the app's AppUserModelID when it has no registered
        // shell identity, which is where "Microsoft.Explorer.Notification..." came from.
        if (level > 0 && level > _lastAnnouncedLevel && snapshot.Ok && worst is not null)
        {
            var title = level == 2 ? "Claude limit nearly gone" : "Claude limit getting close";
            var body = $"{worst.ShortLabel} window at {percent:0}%" +
                       (worst.ResetText.Length > 0 ? $", resets in {worst.ResetText}" : "");
            _window.Dispatcher.Invoke(() => _window.ShowToast(title, body, level));
        }
        if (level >= 0) _lastAnnouncedLevel = level;
    }

    /// <summary>
    /// A 16x16 gauge: fill height carries the value as well as the colour, so the tray icon
    /// is still readable without relying on hue.
    /// </summary>
    private static Icon Render(double? percent, int level)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var track = new SolidBrush(Color.FromArgb(180, 42, 46, 55));
            g.FillRectangle(track, 3, 2, 10, 12);

            if (percent is null)
            {
                using var unknown = new Pen(Color.FromArgb(200, 139, 147, 163), 1.6f);
                g.DrawLine(unknown, 4, 8, 12, 8);
            }
            else
            {
                var colour = level switch
                {
                    2 => Color.FromArgb(232, 121, 249), // magenta
                    1 => Color.FromArgb(251, 191, 36),  // amber
                    _ => Color.FromArgb(52, 211, 153)   // teal-green
                };
                var height = (int)Math.Round(Math.Clamp(percent.Value, 0, 100) / 100.0 * 12);
                if (height == 0 && percent > 0) height = 1;
                using var fill = new SolidBrush(colour);
                g.FillRectangle(fill, 3, 2 + (12 - height), 10, height);
            }

            using var border = new Pen(Color.FromArgb(150, 255, 255, 255));
            g.DrawRectangle(border, 3, 2, 10, 12);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
