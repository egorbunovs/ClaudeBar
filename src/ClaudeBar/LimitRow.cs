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
    // #2A2E37 was too close to the #151922 shell to see at a glance.
    public static readonly Brush Empty    = Freeze("#4C5568");
    public static readonly Brush Muted    = Freeze("#9AA5B8");

    public string Label { get; init; } = "";
    public string Percent { get; init; } = "";
    public string Reset { get; init; } = "";
    public string Glyph { get; init; } = "●";
    public Brush Accent { get; init; } = Normal;
    public string Tooltip { get; init; } = "";

    /// <summary>Twelve brushes: filled ones take the accent, the rest stay dark. Countable by eye.</summary>
    public IReadOnlyList<Brush> Cells { get; init; } = Array.Empty<Brush>();

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

        // Round up so any non-zero usage lights at least one segment.
        var filled = pct <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(pct / 100.0 * Segments), 1, Segments);
        var cells = Enumerable.Range(0, Segments).Select(i => i < filled ? accent : Empty).ToArray();

        var reset = limit.ResetText;
        return new LimitRow
        {
            Label = limit.ShortLabel,
            Percent = $"{pct:0}%",
            Reset = reset.Length == 0 ? "" : $"↻{reset}",
            Glyph = glyph,
            Accent = accent,
            Cells = cells,
            Tooltip = $"{limit.Kind}: {pct:0}% used" +
                      (limit.ResetsAt is { } r ? $", resets {r.ToLocalTime():ddd HH:mm}" : "")
        };
    }

    private static int Level(double pct, double warnAt, double criticalAt) =>
        pct >= criticalAt ? 2 : pct >= warnAt ? 1 : 0;

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
