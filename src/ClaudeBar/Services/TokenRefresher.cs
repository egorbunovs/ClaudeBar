using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeBar.Services;

/// <summary>
/// Refreshes an OAuth access token the way Claude Code does, for accounts ClaudeBar holds
/// but Claude Code is not currently using.
///
/// This is the ONE place the "never refresh a token" rule is relaxed, and only for idle
/// accounts. Refreshing rotates the refresh token, so doing it for the active account would
/// race Claude Code and log it out. An idle account has no other user, so there is no race;
/// the rotated token is written straight back to ClaudeBar's own store.
///
/// Endpoint, client id and request shape were read out of claude.exe 2.1.274 (see README).
/// </summary>
public sealed class TokenRefresher
{
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>Refresh this far ahead of expiry, so a token never dies mid-poll.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public TokenRefresher()
    {
        _http.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeBar/0.1");
    }

    /// <summary>
    /// CLAUDEBAR_FORCE_REFRESH=1 refreshes idle tokens on every poll, so the refresh path can
    /// be exercised on demand instead of waiting the ~8h for a token to age. Diagnostics only.
    /// </summary>
    private static readonly bool ForceRefresh =
        Environment.GetEnvironmentVariable("CLAUDEBAR_FORCE_REFRESH") == "1";

    public static bool NeedsRefresh(string oauthJson)
    {
        if (ForceRefresh) return true;
        try
        {
            using var doc = JsonDocument.Parse(oauthJson);
            if (!doc.RootElement.TryGetProperty("expiresAt", out var e) || e.ValueKind != JsonValueKind.Number)
                return true;
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64());
            return DateTimeOffset.UtcNow + Margin >= expires;
        }
        catch { return true; }
    }

    public static string? AccessTokenOf(string oauthJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(oauthJson);
            return doc.RootElement.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the refreshed OAuth object in Claude Code's own on-disk shape, or null with a
    /// reason. The caller must persist the result: the old refresh token is dead once this
    /// returns.
    /// </summary>
    public async Task<(string? OauthJson, string? Error)> RefreshAsync(string oauthJson, CancellationToken ct)
    {
        JsonObject? current;
        try { current = JsonNode.Parse(oauthJson) as JsonObject; }
        catch { current = null; }
        if (current is null) return (null, "unreadable stored token");

        var refreshToken = current["refreshToken"]?.GetValue<string>();
        if (string.IsNullOrEmpty(refreshToken)) return (null, "no refresh token stored");

        var scopes = current["scopes"] is JsonArray arr
            ? string.Join(" ", arr.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)))
            : "";

        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId
        };
        if (scopes.Length > 0) body["scope"] = scopes;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                // The body can name the reason (invalid_grant = refresh token dead). Keep only
                // the error code, never the payload, out of any log.
                string? code = null;
                try { code = (JsonNode.Parse(text) as JsonObject)?["error"]?.GetValue<string>(); }
                catch { /* not JSON */ }
                return (null, $"refresh failed: http {(int)res.StatusCode}{(code is null ? "" : " " + code)}");
            }

            var reply = JsonNode.Parse(text) as JsonObject;
            var access = reply?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(access)) return (null, "refresh reply had no access token");

            // Rewrite in Claude Code's shape, preserving what the reply does not restate.
            current["accessToken"] = access;
            if (reply?["refresh_token"]?.GetValue<string>() is { Length: > 0 } rotated)
                current["refreshToken"] = rotated;

            var expiresIn = reply?["expires_in"]?.GetValue<double>() ?? 3600;
            current["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds();

            return (current.ToJsonString(), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (null, $"refresh failed: {ex.GetType().Name}");
        }
    }
}
