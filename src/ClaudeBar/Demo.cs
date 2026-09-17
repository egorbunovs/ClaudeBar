using System.Windows;
using System.Windows.Media;
using ClaudeBar.Models;
using ClaudeBar.Services;

namespace ClaudeBar;

/// <summary>
/// `ClaudeBar.exe --demo`: three invented accounts with invented numbers, so the README's
/// screenshots can show the pill doing its job without anyone's real email or usage on
/// screen.
///
/// Demo mode is a dead end by design. When it is on, the pill never reads the credential
/// file, never calls the usage endpoint, never captures or switches an account, and never
/// writes the usage cache — <see cref="MainWindow.RefreshAsync"/> and
/// <see cref="MainWindow.CurrentAccounts"/> divert here before any of that can happen.
/// Clicking a different account only moves a marker in this class.
///
/// The readings are fixed offsets from startup rather than literal dates, so the reset
/// countdowns look alive and a re-shot screenshot matches the last one.
/// </summary>
internal static class Demo
{
    public static bool Enabled { get; private set; }

    /// <summary>
    /// Layout scale for screenshots: `--demo --scale=3` lays the whole pill out three times
    /// bigger, and the capture script shrinks the shot back down. Text, the segment bars and
    /// the glyphs are all drawn at the larger size and then resampled, which is what makes a
    /// README image look like the app instead of like a magnified screenshot of it.
    /// </summary>
    public static double Scale { get; private set; } = 1;

    public static void Enable(double scale = 1)
    {
        Enabled = true;
        Scale = scale is >= 1 and <= 8 ? scale : 1;
    }

    /// <summary>Applies the screenshot scale to a pill or a menu. A no-op outside demo mode.</summary>
    public static void ApplyScale(FrameworkElement? element)
    {
        if (element is null || Scale == 1) return;
        element.LayoutTransform = new ScaleTransform(Scale, Scale);
    }

    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    private static string _activeUuid = "demo-john";

    /// <summary>Three accounts with three different amounts of headroom — the case for the expanded view.</summary>
    public static IReadOnlyList<StoredAccount> Accounts { get; } = new[]
    {
        new StoredAccount("demo-john", "john.doe@example.com", "John Doe", "Example Inc", "demo-org", Started),
        new StoredAccount("demo-jane", "jane.roe@example.com", "Jane Roe", "Example Inc", "demo-org", Started),
        new StoredAccount("demo-sam",  "sam.rivera@example.com", "Sam Rivera", "Example Inc", "demo-org", Started)
    };

    public static string ActiveUuid => _activeUuid;

    /// <summary>The whole of a demo "switch": no file is written, nothing is verified.</summary>
    public static void SwitchTo(string accountUuid)
    {
        if (Accounts.Any(a => a.AccountUuid == accountUuid)) _activeUuid = accountUuid;
    }

    /// <summary>
    /// What the pill draws: the active account first, the rest only when the expanded view
    /// asked for them — the same contract as the real <see cref="MainWindow.CurrentAccounts"/>.
    /// </summary>
    public static List<AccountUsage> Model(bool showAll)
    {
        var active = Accounts.First(a => a.AccountUuid == _activeUuid);
        var list = new List<AccountUsage> { new(active, true, SnapshotFor(active)) };

        if (showAll)
            list.AddRange(Accounts
                .Where(a => a.AccountUuid != _activeUuid)
                .Select(a => new AccountUsage(a, false, SnapshotFor(a))));

        return list;
    }

    private static UsageSnapshot SnapshotFor(StoredAccount account) => account.AccountUuid switch
    {
        // Close enough to the amber line to show a warning, with the weekly still comfortable.
        "demo-jane" => Snapshot(account, session: 96, sessionIn: TimeSpan.FromMinutes(48),
                                         weekly: 88, weeklyIn: TimeSpan.FromHours(54)),
        // The account with room left: what the expanded view is for.
        "demo-sam" => Snapshot(account, session: 9, sessionIn: TimeSpan.FromHours(4.7),
                                        weekly: 24, weeklyIn: TimeSpan.FromHours(123)),
        _ => Snapshot(account, session: 82, sessionIn: TimeSpan.FromHours(3.2),
                               weekly: 61, weeklyIn: TimeSpan.FromHours(46))
    };

    private static UsageSnapshot Snapshot(
        StoredAccount account, double session, TimeSpan sessionIn, double weekly, TimeSpan weeklyIn) =>
        new(
            new[]
            {
                new LimitEntry("session", "session", session, null, Started + sessionIn, true, null),
                new LimitEntry("weekly_all", "weekly", weekly, null, Started + weeklyIn, true, null)
            },
            Started,
            Account: account.Email);
}
