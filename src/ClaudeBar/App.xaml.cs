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
        // One pill only: a second copy would just double the polling and fight over settings.json.
        _single = new Mutex(true, @"Local\ClaudeBar.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var settings = AppSettings.Load();
        var window = new MainWindow(settings);
        _tray = new TrayController(window, settings);
        window.AttachTray(_tray);

        if (settings.Visible) window.Show();

        // The pill is a readout: a stray exception in a poll must not take the app down.
        DispatcherUnhandledException += (_, args) => args.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
