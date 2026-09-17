using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeBar.Models;

namespace ClaudeBar.Services;

/// <summary>
/// Remembers the last good reading across restarts.
///
/// Two reasons, both learned the hard way: the pill should show something familiar the
/// instant it starts rather than an empty shell, and the usage endpoint rate-limits, so a
/// restart that immediately re-polls can walk straight into an HTTP 429. A cached reading,
/// clearly marked as the time it was taken, is better than both.
///
/// Holds percentages and reset times only — nothing from the credentials file.
/// </summary>
public static class UsageCache
{
    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeBar", "last-usage.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Entry(
        string Kind, string Group, double Percent, string? Severity,
        DateTimeOffset? ResetsAt, bool IsActive, string? ScopeLabel);

    private sealed record Payload(List<Entry> Limits, DateTimeOffset FetchedAt, string? Account);

    public static void Save(UsageSnapshot snapshot)
    {
        if (!snapshot.Ok) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var payload = new Payload(
                snapshot.Limits.Select(l => new Entry(
                    l.Kind, l.Group, l.Percent, l.Severity, l.ResetsAt, l.IsActive, l.ScopeLabel)).ToList(),
                snapshot.FetchedAt,
                snapshot.Account);

            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, Json));
            File.Move(tmp, Path, overwrite: true);
        }
        catch { /* a cache that cannot be written is not worth failing over */ }
    }

    public static UsageSnapshot? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(Path), Json);
            if (payload is null || payload.Limits.Count == 0) return null;

            // A reading older than a day says nothing useful about the current window.
            if (DateTimeOffset.UtcNow - payload.FetchedAt > TimeSpan.FromHours(24)) return null;

            var limits = payload.Limits
                .Select(e => new LimitEntry(e.Kind, e.Group, e.Percent, e.Severity,
                    e.ResetsAt, e.IsActive, e.ScopeLabel))
                .ToList();

            return new UsageSnapshot(limits, payload.FetchedAt, null, payload.Account);
        }
        catch { return null; }
    }
}
