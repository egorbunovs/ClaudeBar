namespace ClaudeBar.Models;

/// <summary>
/// One limit window as reported by /api/oauth/usage.
/// Modelled on the response's <c>limits[]</c> array rather than its top-level keys:
/// those keys carry rotating codenames (tangelo, nimbus_quill, cedar_ember, ...) that
/// change between Claude Code releases, whereas limits[] has been a stable shape.
/// </summary>
public sealed record LimitEntry(
    string Kind,
    string Group,
    double Percent,
    string? Severity,
    DateTimeOffset? ResetsAt,
    bool IsActive,
    string? ScopeLabel)
{
    /// <summary>Short label for the pill: "5h", "7d", or a model name for scoped weeklies.</summary>
    public string ShortLabel => ScopeLabel ?? Kind switch
    {
        "session" => "5h",
        "weekly_all" => "7d",
        "weekly_scoped" => "7d*",
        _ => Group switch
        {
            "session" => "5h",
            "weekly" => "7d",
            _ => Kind.Length <= 4 ? Kind : Kind[..4]
        }
    };

    public string ResetText
    {
        get
        {
            if (ResetsAt is null) return "";
            var left = ResetsAt.Value - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return "due";
            if (left.TotalHours >= 24) return $"{(int)left.TotalDays}d {left.Hours}h";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes}m";
            return $"{(int)left.TotalMinutes}m";
        }
    }
}

/// <summary>Result of one poll. <see cref="Error"/> non-null means nothing usable came back.</summary>
public sealed record UsageSnapshot(
    IReadOnlyList<LimitEntry> Limits,
    DateTimeOffset FetchedAt,
    string? Error = null,
    string? Account = null)
{
    public static UsageSnapshot Failed(string error) =>
        new(Array.Empty<LimitEntry>(), DateTimeOffset.UtcNow, error);

    public bool Ok => Error is null;

    /// <summary>The window closest to its ceiling — what the colour and tray tooltip key off.</summary>
    public LimitEntry? Worst => Limits.Count == 0 ? null : Limits.MaxBy(l => l.Percent);
}
