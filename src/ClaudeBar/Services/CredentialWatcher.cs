using System.IO;

namespace ClaudeBar.Services;

/// <summary>
/// Watches Claude Code's credential file and reports when the signed-in account changes.
///
/// This is what makes adding an account a non-event: sign in however you like — ClaudeBar's
/// button, `/login` in a terminal, `claude auth login` by hand — and ClaudeBar notices within
/// a second, saves that account to its own store, and updates the pill. There is no second
/// "now save it" step to forget, and no way to sign into an account that ClaudeBar then
/// cannot switch back to.
///
/// It also re-captures on every credential write, not just account changes, which keeps the
/// stored copy in step with the refresh token Claude Code rotates as it goes.
/// </summary>
public sealed class CredentialWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounce = new(900) { AutoReset = false };
    private string? _lastAccountUuid;

    /// <summary>Raised (off the UI thread) after the credential file settles.</summary>
    public event Action<string?>? Changed;

    /// <summary>
    /// Set while ClaudeBar is writing the credential file itself, so its own switch does not
    /// come back round as an external change.
    /// </summary>
    public bool Suppressed { get; set; }

    public CredentialWatcher()
    {
        _lastAccountUuid = CurrentUuid();
        _debounce.Elapsed += (_, _) =>
        {
            if (Suppressed) return;
            Changed?.Invoke(CurrentUuid());
        };

        try
        {
            var dir = CredentialStore.ClaudeDir;
            if (!Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir, ".credentials.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            // Claude Code writes this file more than once per login, so wait for it to settle.
            _watcher.Changed += (_, _) => Restart();
            _watcher.Created += (_, _) => Restart();
            _watcher.Renamed += (_, _) => Restart();
        }
        catch
        {
            // Without a watcher the pill still works; it just falls back to the poll interval.
            _watcher = null;
        }
    }

    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public static string? CurrentUuid() =>
        AccountStore.ReadAccountBlock()?["accountUuid"]?.GetValue<string>();

    /// <summary>True when the account has changed since the last time this was asked.</summary>
    public bool AccountChanged(string? uuid)
    {
        var changed = !string.Equals(uuid, _lastAccountUuid, StringComparison.OrdinalIgnoreCase);
        _lastAccountUuid = uuid;
        return changed;
    }

    public void Dispose()
    {
        _debounce.Dispose();
        _watcher?.Dispose();
    }
}
