using System.IO;
using System.Text.Json;

namespace ClaudeBar.Services;

/// <summary>
/// Reads the OAuth access token Claude Code keeps in %USERPROFILE%\.claude\.credentials.json.
///
/// Rules of this file, which the rest of the app must honour:
///   - the token is NEVER logged, surfaced in the UI, or written anywhere;
///   - ClaudeBar only ever READS it. Refreshing is left to Claude Code, because a refresh
///     rotates the refresh token and racing Claude Code for that would log the user out.
///     If the token is expired we show "signed out" and wait for Claude Code to renew it.
/// </summary>
public sealed class CredentialStore
{
    public static string ClaudeDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string CredentialsPath => Path.Combine(ClaudeDir, ".credentials.json");
    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public sealed record Token(string AccessToken, DateTimeOffset ExpiresAt)
    {
        public bool Expired => DateTimeOffset.UtcNow >= ExpiresAt;
    }

    public enum State { Ok, NotInstalled, NotSignedIn, Expired, Unreadable }

    public sealed record Result(State State, Token? Token, string? Detail = null);

    public Result Read()
    {
        if (!Directory.Exists(ClaudeDir))
            return new Result(State.NotInstalled, null, "Claude Code not found");
        if (!File.Exists(CredentialsPath))
            return new Result(State.NotSignedIn, null, "not signed in");

        try
        {
            // Claude Code rewrites this file on refresh; tolerate a torn read by retrying once.
            using var doc = JsonDocument.Parse(ReadShared(CredentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                return new Result(State.NotSignedIn, null, "no subscription login");

            var access = oauth.TryGetProperty("accessToken", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(access))
                return new Result(State.NotSignedIn, null, "no token");

            var expires = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64())
                : DateTimeOffset.UtcNow.AddMinutes(1);

            var token = new Token(access, expires);
            return token.Expired
                ? new Result(State.Expired, token, "token expired")
                : new Result(State.Ok, token);
        }
        catch (Exception ex)
        {
            return new Result(State.Unreadable, null, ex.GetType().Name);
        }
    }

    /// <summary>The signed-in account's email, for the pill's tooltip. Best effort.</summary>
    public string? ReadAccountEmail()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            using var doc = JsonDocument.Parse(ReadShared(ConfigPath));
            return doc.RootElement.TryGetProperty("oauthAccount", out var acct)
                && acct.TryGetProperty("emailAddress", out var mail)
                ? mail.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>Open even while Claude Code holds the file.</summary>
    private static string ReadShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(60);
            }
        }
    }
}
