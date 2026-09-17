using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeBar.Services;

/// <summary>Plain JSON next to the exe, as the brief asks. Falls back to LocalAppData if that path is read-only.</summary>
public sealed class AppSettings
{
    /// <summary>Win32 device name of the monitor to park on, e.g. "\\.\DISPLAY2". Null = primary.</summary>
    public string? MonitorDeviceName { get; set; }

    /// <summary>
    /// Remembered position: physical-pixel inset from the chosen monitor's bottom-right corner.
    /// Physical pixels, to match ScreenService and SetWindowPos.
    /// </summary>
    public int OffsetX { get; set; } = 12;
    public int OffsetY { get; set; } = 12;
    public bool PositionPinned { get; set; }

    public int PollSeconds { get; set; } = 60;
    public double WarnAt { get; set; } = 70;
    public double CriticalAt { get; set; } = 90;
    public double Opacity { get; set; } = 0.94;
    public bool StartWithWindows { get; set; }
    public bool Visible { get; set; } = true;

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
        Opacity = Math.Clamp(Opacity, 0.35, 1.0);
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
