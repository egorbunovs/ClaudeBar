using System.Windows.Media;
using ClaudeBar.Models;

namespace ClaudeBar;

/// <summary>
/// One row of the pill. Every state carries THREE cues, never colour alone:
/// a countable segment fill, the number itself, and a shape glyph. The brief calls for this
/// (red/green colour blindness), and the palette avoids a red/green pair outright:
/// teal -> amber -> magenta stays separable under both protanopia and deuteranopia.
/// </summary>
public sealed class LimitRow
{
    public const int Segments = 12;

    public static readonly Brush Normal   = Freeze("#34D399"); // teal-leaning green, never next to red
    public static readonly Brush Warn     = Freeze("#FBBF24"); // amber
    public static readonly Brush Critical = Freeze("#E879F9"); // magenta, not red

    // The unfilled track has to read as "a segment that is not lit", not as background.
    public static readonly Brush Track    = Freeze("#4C5568");
    public static readonly Brush Muted    = Freeze("#9AA5B8");

    /// <summary>Background of the rounded chip behind the percentage.</summary>
    public static readonly Brush ChipFill  = Freeze("#2B3242");

    public string Label { get; init; } = "";
    public string Percent { get; init; } = "";
    public double PercentValue { get; init; }
    public string Reset { get; init; } = "";

    /// <summary>Mini layout only: whether the reset time shows under the percentage.</summary>
    public System.Windows.Visibility MiniResetVisibility { get; set; } = System.Windows.Visibility.Visible;
    public string Glyph { get; init; } = "●";
    public Brush Accent { get; init; } = Normal;
    public string Tooltip { get; init; } = "";

    public static LimitRow From(LimitEntry limit, double warnAt, double criticalAt)
    {
        var pct = Math.Clamp(limit.Percent, 0, 100);

        // Severity from the API wins when it says something louder than our own thresholds.
        var level = limit.Severity?.ToLowerInvariant() switch
        {
            "critical" or "exhausted" or "blocked" => 2,
            "warning" or "warn" => Math.Max(1, Level(pct, warnAt, criticalAt)),
            _ => Level(pct, warnAt, criticalAt)
        };

        var accent = level switch { 2 => Critical, 1 => Warn, _ => Normal };
        var glyph  = level switch { 2 => "■", 1 => "▲", _ => "●" };

        return new LimitRow
        {
            Label = limit.ShortLabel,
            Percent = $"{pct:0}%",
            PercentValue = pct,
            // No icon here: a glyph this small reads as a smudge from across the room.
            // The chip behind the percentage does the dividing instead.
            Reset = limit.ResetText,
            Glyph = glyph,
            Accent = accent,
            Tooltip = $"{limit.Kind}: {pct:0}% used" +
                      (limit.ResetsAt is { } r ? $", resets {r.ToLocalTime():ddd HH:mm}" : "")
        };
    }

    /// <summary>
    /// Rows for a reading, or grey placeholders when there is none yet: every bar unlit, no
    /// number. An account that has never been read shows as "unknown", never as someone
    /// else's usage.
    /// </summary>
    public static List<LimitRow> ForSnapshot(UsageSnapshot snapshot, double warnAt, double criticalAt) =>
        snapshot.HasData
            ? snapshot.Limits.Select(l => From(l, warnAt, criticalAt)).ToList()
            : new List<LimitRow> { Placeholder("5h"), Placeholder("7d") };

    private static LimitRow Placeholder(string label) => new()
    {
        Label = label,
        Percent = "–",
        PercentValue = 0,
        Reset = "",
        Glyph = "○",
        Accent = Muted,
        Tooltip = "No reading for this account yet"
    };

    private static int Level(double pct, double warnAt, double criticalAt) =>
        pct >= criticalAt ? 2 : pct >= warnAt ? 1 : 0;

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
