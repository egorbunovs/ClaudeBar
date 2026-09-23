using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeBar.Services;

/// <summary>Where the pill sits when it is snapped to an edge or corner.</summary>
public enum SnapAnchor
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
    BottomCentre,
    TopCentre,
    /// <summary>Dropped somewhere that is not near an edge; keep the exact offsets.</summary>
    Free
}

/// <summary>Plain JSON next to the exe, as the brief asks. Falls back to LocalAppData if that path is read-only.</summary>
public sealed class AppSettings
{
    /// <summary>Win32 device name of the monitor to park on, e.g. "\\\\.\\DISPLAY2". Null = primary.</summary>
    public string? MonitorDeviceName { get; set; }

    /// <summary>Which edge or corner the pill snaps to on the chosen monitor.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SnapAnchor Anchor { get; set; } = SnapAnchor.BottomRight;

    /// <summary>Gap in physical pixels between the pill and the edges it is snapped to.</summary>
    public int SnapPadding { get; set; } = 12;

    /// <summary>
    /// How close to an edge (physical pixels) a drop has to be before it snaps there.
    /// </summary>
    public int SnapThreshold { get; set; } = 64;

    /// <summary>
    /// Free-placement position: physical-pixel inset from the monitor's bottom-right corner.
    /// Only used when <see cref="Anchor"/> is <see cref="SnapAnchor.Free"/>.
    /// </summary>
    public int OffsetX { get; set; } = 12;
    public int OffsetY { get; set; } = 12;

    public int PollSeconds { get; set; } = 60;
    public double WarnAt { get; set; } = 80;
    public double CriticalAt { get; set; } = 90;

    /// <summary>
    /// Opacity of the whole pill.
    ///
    /// This used to fade only the background brush, on the theory that keeping text opaque
    /// protected readability. In practice that was invisible: over a dark desktop the change
    /// between 0% and 100% was about ten levels of grey, so the slider looked broken.
    /// Fading the window is what "transparency" is taken to mean, and it is unmistakable.
    /// Floored at 0.2 so the pill can never be made impossible to find.
    /// </summary>
    public double Opacity { get; set; } = 0.92;

    // Autostart deliberately has no setting here: the HKCU Run key is the single source of
    // truth, and a copy in this file only ever gets to disagree with it.
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Show usage for every stored account, not just the signed-in one.
    /// Has no effect until the account switcher exists — see README, Phase 2.
    /// </summary>
    public bool ShowAllAccounts { get; set; }

    /// <summary>Mini layout: one line, just the numbers. Click it to get the full pill back.</summary>
    public bool Mini { get; set; }

    /// <summary>Mini layout: the time to each reset, in small text under its percentage.</summary>
    public bool MiniResetTimes { get; set; } = true;

    [JsonIgnore] public string Path { get; private set; } = "";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        foreach (var candidate in CandidatePaths())
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate), Json);
                if (s is null) continue;
                s.Path = candidate;
                s.Clamp();
                return s;
            }
            catch { /* corrupt settings must never stop the app starting */ }
        }

        var fresh = new AppSettings { Path = CandidatePaths().First() };
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        // A demo pill must leave nothing behind. Its clicks — expand, minimize — are staging
        // for a screenshot, not preferences, and it may be sharing this file with a real pill.
        if (Demo.Enabled) return;

        Clamp();
        foreach (var candidate in new[] { Path }.Concat(CandidatePaths()).Where(p => !string.IsNullOrEmpty(p)))
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(candidate)!);
                // Write-and-swap so a crash mid-save cannot leave a truncated file.
                var tmp = candidate + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, candidate, overwrite: true);
                Path = candidate;
                return;
            }
            catch { /* try the next location */ }
        }
    }

    private void Clamp()
    {
        PollSeconds = Math.Clamp(PollSeconds, 15, 3600);
        WarnAt = Math.Clamp(WarnAt, 1, 100);
        CriticalAt = Math.Clamp(CriticalAt, WarnAt, 100);
        Opacity = Math.Clamp(Opacity, 0.2, 1.0);
        SnapPadding = Math.Clamp(SnapPadding, 0, 200);
        SnapThreshold = Math.Clamp(SnapThreshold, 0, 400);
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var exeDir = AppContext.BaseDirectory;
        yield return System.IO.Path.Combine(exeDir, "settings.json");
        yield return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeBar", "settings.json");
    }
}
