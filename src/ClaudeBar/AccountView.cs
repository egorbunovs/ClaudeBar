using System.Windows;
using System.Windows.Media;
using ClaudeBar.Models;
using ClaudeBar.Services;

namespace ClaudeBar;

/// <summary>One account's section of the pill: a header line and its limit rows.</summary>
public sealed class AccountView
{
    public string AccountUuid { get; init; } = "";
    public string Email { get; init; } = "";
    public string Glyph { get; init; } = "●";
    public Brush GlyphBrush { get; init; } = LimitRow.Muted;
    public string Status { get; init; } = "";
    public double Opacity { get; init; } = 1.0;
    public bool IsActive { get; init; }
    public bool ShowingAll { get; init; }

    /// <summary>Collapsed view: the switch button opens the picker.</summary>
    public Visibility SwitchVisibility => IsActive && !ShowingAll ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The expand/collapse chevron lives on the active account's line only.</summary>
    public Visibility ToggleVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public string ToggleGlyph => ShowingAll ? "▴" : "▾";
    public string ToggleTip => ShowingAll ? "Show only the active account" : "Show every account";

    /// <summary>Expanded view: the radio is the switch. Clicking an idle account's radio switches to it.</summary>
    public string RadioTip => IsActive ? "Active account" : ShowingAll ? "Switch to this account" : "";

    /// <summary>The minimize button sits with the chevron, on the active line.</summary>
    public Visibility MiniVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatusVisibility => Status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<LimitRow> Rows { get; init; } = Array.Empty<LimitRow>();
    public string Tooltip { get; init; } = "";

    private static readonly Brush ActiveBrush = LimitRow.Normal;

    public static AccountView From(AccountUsage usage, double warnAt, double criticalAt, bool showingAll)
    {
        var snap = usage.Snapshot;
        var rows = snap.Limits.Select(l => LimitRow.From(l, warnAt, criticalAt)).ToList();

        // Idle accounts sit a little dimmer; a stale reading dims further, on any account.
        var opacity = (usage.IsActive ? 1.0 : 0.82) * (snap.Ok ? 1.0 : 0.6);

        var status = snap.Ok ? "" : snap.Error switch
        {
            "signed out" => "signed out",
            "sign in again" => "sign in again",
            "rate limited" => "retrying",
            "offline" => "offline",
            "refresh failed" => "refresh failed",
            _ => snap.Error ?? ""
        };

        var lines = new List<string> { usage.Account.Detail };
        lines.AddRange(rows.Select(r => r.Tooltip));
        lines.Add(snap.Ok
            ? $"Updated {snap.FetchedAt.ToLocalTime():HH:mm:ss}"
            : $"{snap.Error} - last reading {snap.FetchedAt.ToLocalTime():HH:mm:ss}");
        return new AccountView
        {
            AccountUuid = usage.Account.AccountUuid,
            ShowingAll = showingAll,
            Email = usage.Account.Label,
            Glyph = usage.IsActive ? "●" : "○",
            GlyphBrush = usage.IsActive ? ActiveBrush : LimitRow.Muted,
            Status = status,
            Opacity = opacity,
            IsActive = usage.IsActive,
            Rows = rows,
            Tooltip = string.Join(Environment.NewLine, lines)
        };
    }
}
