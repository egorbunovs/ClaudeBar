using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeBar.Models;

namespace ClaudeBar.Services;

/// <summary>
/// Polls the same endpoint Claude Code's own /usage reads.
///
/// Verified on 2026-09-17 against claude.exe 2.1.274 (see README "Spike 1"):
///   GET https://api.anthropic.com/api/oauth/usage
///   Authorization: Bearer &lt;claudeAiOauth.accessToken&gt;
/// returns five_hour / seven_day objects AND a limits[] array. We read limits[].
/// </summary>
public sealed class UsageService : IDisposable
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CredentialStore _credentials = new();

    public UsageService()
    {
        _http.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeBar/0.1");
    }

    /// <summary>Usage for the signed-in account, using Claude Code's own live token.</summary>
    public async Task<UsageSnapshot> PollAsync(CancellationToken ct = default)
    {
        var cred = _credentials.Read();
        if (cred.State != CredentialStore.State.Ok || cred.Token is null)
            return UsageSnapshot.Failed(cred.Detail ?? "no credentials");

        var snapshot = await PollWithTokenAsync(cred.Token.AccessToken, ct).ConfigureAwait(false);
        return snapshot.Ok ? snapshot with { Account = _credentials.ReadAccountEmail() } : snapshot;
    }

    /// <summary>Usage for whichever account this token belongs to.</summary>
    public async Task<UsageSnapshot> PollWithTokenAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                // Deliberately NOT refreshing: that rotates the refresh token under Claude Code.
                return UsageSnapshot.Failed("signed out");

            // The endpoint rate-limits. Polling all day, and especially restarting often,
            // will hit it, so respect Retry-After and let the caller back off rather than
            // hammering it into a longer penalty.
            if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                return UsageSnapshot.Failed("rate limited", RetryAfterOf(res));

            if (!res.IsSuccessStatusCode)
                return UsageSnapshot.Failed($"http {(int)res.StatusCode}");

            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var limits = Parse(body);
            return limits.Count == 0
                ? UsageSnapshot.Failed("no limits reported")
                : new UsageSnapshot(limits, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Offline, DNS down, laptop asleep: keep the last good reading on screen.
            return UsageSnapshot.Failed("offline");
        }
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage res)
    {
        var header = res.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }
        return null;
    }

    internal static List<LimitEntry> Parse(string json)
    {
        var result = new List<LimitEntry>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(item, "kind") ?? "unknown";
                var group = Str(item, "group") ?? kind;
                if (!TryNum(item, "percent", out var percent)) continue;

                string? scope = null;
                if (item.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object
                    && sc.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                    scope = Str(model, "display_name");

                result.Add(new LimitEntry(
                    kind, group, percent,
                    Str(item, "severity"),
                    Time(item, "resets_at"),
                    item.TryGetProperty("is_active", out var act) && act.ValueKind == JsonValueKind.True,
                    scope));
            }
        }

        // Fallback for a response that drops limits[]: the two named windows we know of.
        if (result.Count == 0)
        {
            foreach (var (key, kind, group) in new[]
                     {
                         ("five_hour", "session", "session"),
                         ("seven_day", "weekly_all", "weekly")
                     })
            {
                if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!TryNum(w, "utilization", out var util)) continue;
                result.Add(new LimitEntry(kind, group, util, null, Time(w, "resets_at"), true, null));
            }
        }

        // Session first, then weekly, then anything new the API grows.
        return result
            .OrderBy(l => l.Group == "session" ? 0 : l.Group == "weekly" ? 1 : 2)
            .ThenByDescending(l => l.Percent)
            .ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryNum(JsonElement e, string name, out double value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return false;
        value = v.GetDouble();
        return true;
    }

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, out var t) ? t : null;

    public void Dispose() => _http.Dispose();
}
