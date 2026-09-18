using System.Threading;
using System.Windows;
using ClaudeBar.Services;

namespace ClaudeBar;

public partial class App : Application
{
    private Mutex? _single;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest"))
        {
            Native.AttachConsole();
            Environment.ExitCode = SelfTest.Run();
            Shutdown();
            return;
        }

        // Invented accounts for the README's screenshots. Must be set before the window is
        // built: it decides, once, whether anything real is ever read. `--scale=3` lays the
        // pill out three times bigger for a shot that survives being resampled.
        if (e.Args.Contains("--demo"))
        {
            var scaleArg = e.Args.FirstOrDefault(a => a.StartsWith("--scale=", StringComparison.Ordinal));
            var scale = scaleArg is null ? 1
                : double.TryParse(scaleArg["--scale=".Length..],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
            Demo.Enable(scale);
        }

        // One pill only: a second copy would just double the polling and fight over settings.json.
        // A demo pill is a different thing and gets its own name, so it can sit beside a real one.
        _single = new Mutex(true,
            Demo.Enabled ? @"Local\ClaudeBar.Demo" : @"Local\ClaudeBar.SingleInstance",
            out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // An update arrives under a new versioned filename, which leaves yesterday's Run key
        // pointing at an exe that is gone - and Windows skips a missing target without a word.
        // Running this copy is the moment we can put that right. A demo pill must never touch
        // the real entry, hence the guard.
        if (!Demo.Enabled) Autostart.Refresh();

        var settings = AppSettings.Load();
        var window = new MainWindow(settings);
        _tray = new TrayController(window, settings);
        window.AttachTray(_tray);

        if (settings.Visible) window.Show();

        // The pill is a readout: a stray exception in a poll must not take the app down.
        DispatcherUnhandledException += (_, args) =>
        {
            // Keep the pill alive, but never silently: swallowing these hid a real bug
            // (a frozen brush throwing on every opacity change) for two rounds of fixes.
            Diagnostics.Log(() => $"unhandled: {args.Exception}");
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
