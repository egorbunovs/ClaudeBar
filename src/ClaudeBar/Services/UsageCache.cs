using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeBar.Models;

namespace ClaudeBar.Services;

/// <summary>
/// Remembers the last good reading of every account across restarts.
///
/// Two reasons, both learned the hard way: the pill should show something familiar the
/// instant it starts rather than an empty shell, and the usage endpoint rate-limits, so a
/// restart that immediately re-polls can walk straight into an HTTP 429. A cached reading,
/// clearly marked as the time it was taken, is better than both.
///
/// Keyed by account, because a single "last reading" was being handed to whichever account
/// was signed in next: switch accounts, and the new one showed the old one's numbers.
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

    /// <summary>Writes every account that has a reading, keyed by account uuid.</summary>
    public static void Save(IReadOnlyDictionary<string, UsageSnapshot> readings)
    {
        try
        {
            var payload = readings
                .Where(r => r.Value.HasData)
                .ToDictionary(r => r.Key, r => new Payload(
                    r.Value.Limits.Select(l => new Entry(
                        l.Kind, l.Group, l.Percent, l.Severity, l.ResetsAt, l.IsActive, l.ScopeLabel)).ToList(),
                    r.Value.FetchedAt,
                    r.Value.Account));
            if (payload.Count == 0) return;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, Json));
            File.Move(tmp, Path, overwrite: true);
        }
        catch { /* a cache that cannot be written is not worth failing over */ }
    }

    /// <summary>
    /// Every account's last reading, keyed by account uuid. A file in the old single-reading
    /// shape does not say whose it was, so it is ignored rather than guessed at.
    /// </summary>
    public static Dictionary<string, UsageSnapshot> Load()
    {
        var result = new Dictionary<string, UsageSnapshot>();
        try
        {
            if (!File.Exists(Path)) return result;
            var payload = JsonSerializer.Deserialize<Dictionary<string, Payload>>(File.ReadAllText(Path), Json);
            if (payload is null) return result;

            foreach (var (uuid, p) in payload)
            {
                if (p?.Limits is not { Count: > 0 }) continue;

                // A reading older than a day says nothing useful about the current window.
                if (DateTimeOffset.UtcNow - p.FetchedAt > TimeSpan.FromHours(24)) continue;

                var limits = p.Limits
                    .Select(e => new LimitEntry(e.Kind, e.Group, e.Percent, e.Severity,
                        e.ResetsAt, e.IsActive, e.ScopeLabel))
                    .ToList();

                // Marked as not fresh, so it reads as "last reading" until a poll replaces it.
                result[uuid] = new UsageSnapshot(limits, p.FetchedAt, "cached", p.Account);
            }
        }
        catch { /* old shape or unreadable: start empty */ }
        return result;
    }
}
