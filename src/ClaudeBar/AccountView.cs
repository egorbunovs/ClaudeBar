using System.Windows;
using System.Windows.Media;
using ClaudeBar.Models;
using ClaudeBar.Services;

namespace ClaudeBar;

/// <summary>
/// One account's section of the pill: a header line and its limit rows.
///
/// Nothing here is dimmed or greyed. An earlier version faded sections to mean "idle" or
/// "this reading is a minute old", which told the user nothing and flickered whenever a
/// poll hit the rate limit. If a reading is old, it is still the best information there is;
/// the tooltip carries the timestamp for anyone who cares.
/// </summary>
public sealed class AccountView
{
    public string AccountUuid { get; init; } = "";
    public string Email { get; init; } = "";

    // The radio is drawn, not typed. As a Segoe MDL2 glyph at 13px it was hinted onto whole
    // pixels, and depending on where the pill happened to sit, the hinting flattened the left
    // side of the ring into a straight edge - a circle with a slice missing.
    public Brush RadioStroke { get; init; } = LimitRow.Muted;
    public Brush RadioFill { get; init; } = Brushes.Transparent;
    public string Status { get; init; } = "";
    public Brush StatusBrush { get; init; } = LimitRow.Muted;
    public bool IsActive { get; init; }
    public bool ShowingAll { get; init; }

    /// <summary>Collapsed view: the switch button opens the picker.</summary>
    public Visibility SwitchVisibility => IsActive && !ShowingAll ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The expand/collapse chevron and minimize button live on the active line only.</summary>
    public Visibility ToggleVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MiniVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    // Segoe MDL2 Assets: ChevronUp / ChevronDown.
    public string ToggleGlyph => ShowingAll ? "" : "";
    public string ToggleTip => ShowingAll ? "Show only the active account" : "Show every account";

    /// <summary>Expanded view: the radio is the switch. Clicking an idle account's radio switches to it.</summary>
    public string RadioTip => IsActive ? "Active account" : ShowingAll ? "Switch to this account" : "";

    public Visibility StatusVisibility => Status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<LimitRow> Rows { get; init; } = Array.Empty<LimitRow>();
    public string Tooltip { get; init; } = "";

    private static readonly Brush Warn = LimitRow.Warn;

    public static AccountView From(AccountUsage usage, double warnAt, double criticalAt, bool showingAll)
    {
        var snap = usage.Snapshot;
        var rows = LimitRow.ForSnapshot(snap, warnAt, criticalAt);

        // A badge only for things the user can act on, or for "not here yet". A 429 or a
        // moment offline while a good reading is on screen is nobody's business.
        var (status, brush) = snap.Error switch
        {
            null => ("", LimitRow.Muted),
            "loading" => ("loading…", LimitRow.Muted),
            "signed out" => ("signed out", Warn),
            "sign in again" => ("sign in again", Warn),
            "refresh failed" => ("refresh failed", Warn),
            "no stored token" => ("not saved", Warn),
            // With nothing on screen but grey bars, say why - otherwise they look broken.
            "rate limited" when !snap.HasData => ("rate limited", LimitRow.Muted),
            "offline" when !snap.HasData => ("offline", LimitRow.Muted),
            "token expired" when !snap.HasData => ("waiting for Claude Code", LimitRow.Muted),
            _ when !snap.HasData => ("no reading yet", LimitRow.Muted),
            _ => ("", LimitRow.Muted)
        };

        var lines = new List<string> { usage.Account.Detail };
        if (snap.HasData) lines.AddRange(rows.Select(r => r.Tooltip));
        if (snap.HasData)
            lines.Add(snap.Ok
                ? $"Updated {snap.FetchedAt.ToLocalTime():HH:mm:ss}"
                : $"Last reading {snap.FetchedAt.ToLocalTime():HH:mm:ss}");

        return new AccountView
        {
            AccountUuid = usage.Account.AccountUuid,
            ShowingAll = showingAll,
            Email = usage.Account.Label,
            RadioStroke = usage.IsActive ? LimitRow.Normal : LimitRow.Muted,
            RadioFill = usage.IsActive ? LimitRow.Normal : Brushes.Transparent,
            Status = status,
            StatusBrush = brush,
            IsActive = usage.IsActive,
            Rows = rows,
            Tooltip = string.Join(Environment.NewLine, lines)
        };
    }
}
