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
        // 3 = the wall itself. It gets its own announcement: "nearly gone" at 100% is wrong.
        var level = !snapshot.Ok ? -1
            : percent >= 100 ? 3
            : percent >= _settings.CriticalAt ? 2
            : percent >= _settings.WarnAt ? 1
            : 0;

        var old = _current;
        _current = Render(snapshot.Ok ? percent : null, Math.Min(level, 2));
        _icon.Icon = _current;
        old?.Dispose();

        // Warn on the way up only, once per threshold crossing. Uses ClaudeBar's own toast:
        // shell balloons get titled with the app's AppUserModelID when it has no registered
        // shell identity, which is where "Microsoft.Explorer.Notification..." came from.
        if (level > 0 && level > _lastAnnouncedLevel && snapshot.Ok && worst is not null)
        {
            var title = level switch
            {
                3 => "Claude limit reached",
                2 => "Claude limit nearly gone",
                _ => "Claude limit getting close"
            };
            var body = level == 3
                ? $"{worst.ShortLabel} window is used up" +
                  (worst.ResetText.Length > 0 ? $" - resets in {worst.ResetText}. " : ". ") +
                  "Switch account from the menu to carry on."
                : $"{worst.ShortLabel} window at {percent:0}%" +
                  (worst.ResetText.Length > 0 ? $", resets in {worst.ResetText}" : "");
            _window.Dispatcher.Invoke(() => _window.ShowToast(title, body, Math.Min(level, 2)));
        }
        if (level >= 0) _lastAnnouncedLevel = level;
    }

    /// <summary>
    /// A 16x16 icon that shows the number itself, coloured by state and with a fill bar
    /// under it as the second cue. Two digits are legible at tray size; 100% is drawn as a
    /// full bar with "!!", since three digits are not.
    /// </summary>
    private static Icon Render(double? percent, int level)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var back = new SolidBrush(Color.FromArgb(230, 21, 25, 34));
            using var path = RoundedRect(new Rectangle(0, 0, 15, 15), 3);
            g.FillPath(back, path);

            var colour = level switch
            {
                2 => Color.FromArgb(232, 121, 249), // magenta
                1 => Color.FromArgb(251, 191, 36),  // amber
                _ => Color.FromArgb(52, 211, 153)   // teal-green
            };

            if (percent is null)
            {
                using var unknown = new Pen(Color.FromArgb(200, 139, 147, 163), 1.6f);
                g.DrawLine(unknown, 4, 8, 12, 8);
            }
            else
            {
                var p = Math.Clamp(percent.Value, 0, 100);
                var text = p >= 100 ? "!!" : ((int)Math.Round(p)).ToString();

                using var family = new System.Drawing.FontFamily("Segoe UI");
                using var font = new Font(family, p >= 100 ? 8f : 7.5f, System.Drawing.FontStyle.Bold, GraphicsUnit.Point);
                using var ink = new SolidBrush(Color.FromArgb(255, 242, 245, 250));
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, ink, (16 - size.Width) / 2f + 0.5f, (11 - size.Height) / 2f + 0.5f);

                // The fill bar along the bottom: a second cue beside the colour.
                using var track = new SolidBrush(Color.FromArgb(160, 60, 66, 80));
                g.FillRectangle(track, 2, 12, 12, 2);
                using var fill = new SolidBrush(colour);
                var width = (int)Math.Round(p / 100.0 * 12);
                if (width == 0 && p > 0) width = 1;
                g.FillRectangle(fill, 2, 12, width, 2);
            }

            using var border = new Pen(Color.FromArgb(90, 255, 255, 255));
            g.DrawPath(border, path);
        }

        DumpForDiagnostics(bmp, percent, level);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>With CLAUDEBAR_DIAG=1, writes the icon out so it can be looked at without a tray.</summary>
    private static void DumpForDiagnostics(Bitmap bmp, double? percent, int level)
    {
        if (Environment.GetEnvironmentVariable("CLAUDEBAR_DIAG") != "1") return;
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeBar");
            System.IO.Directory.CreateDirectory(dir);
            bmp.Save(System.IO.Path.Combine(dir, $"tray-{(percent is null ? "none" : ((int)percent.Value).ToString())}-{level}.png"));
        }
        catch { /* diagnostics only */ }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
