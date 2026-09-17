using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeBar.Models;

namespace ClaudeBar.Services;

/// <summary>
/// ClaudeBar's own copy of each account's sign-in, so switching does not need a browser.
///
/// Rules, and they are not negotiable:
///   - the OAuth blob is encrypted at rest with DPAPI, scoped to the current user. Claude
///     Code keeps it in plaintext; there is no reason for a second plaintext copy to exist;
///   - it is stored under %LOCALAPPDATA%, never in the repo, and .gitignore blocks the shape
///     of it anyway;
///   - nothing from inside the blob is ever logged, shown in the UI, or put in a tooltip.
///     Only the identity fields (email, org) are ever displayed.
/// </summary>
public sealed class AccountStore
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeBar", "accounts");

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeBar.account.v1");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>On-disk shape: identity in the clear, credentials encrypted.</summary>
    private sealed record Record(
        string AccountUuid,
        string? Email,
        string? DisplayName,
        string? OrganizationName,
        string? OrganizationUuid,
        DateTimeOffset CapturedAt,
        string ProtectedOauth,
        string? ProtectedAccountBlock);

    private static string PathFor(string uuid) =>
        Path.Combine(Root, Sanitise(uuid) + ".json");

    private static string Sanitise(string s) =>
        new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    public IReadOnlyList<StoredAccount> List()
    {
        var list = new List<StoredAccount>();
        try
        {
            if (!Directory.Exists(Root)) return list;
            foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<Record>(File.ReadAllText(file), Json);
                    if (r is null) continue;
                    list.Add(new StoredAccount(r.AccountUuid, r.Email, r.DisplayName,
                        r.OrganizationName, r.OrganizationUuid, r.CapturedAt));
                }
                catch { /* one unreadable account must not hide the rest */ }
            }
        }
        catch { /* no store yet */ }

        return list.OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Has(string accountUuid) => File.Exists(PathFor(accountUuid));

    public void Delete(string accountUuid)
    {
        try { File.Delete(PathFor(accountUuid)); } catch { /* already gone */ }
    }

    /// <summary>
    /// Copies whatever account is signed in right now into the store.
    ///
    /// This is also run immediately before every switch, which is the step that stops an
    /// account being silently logged out: Claude Code rotates the refresh token as it goes,
    /// so a copy taken when the account was added goes stale. Re-capturing first means the
    /// stored copy is always the live one.
    /// </summary>
    public StoredAccount? CaptureCurrent(out string? error)
    {
        error = null;
        try
        {
            var credentialsPath = CredentialStore.CredentialsPath;
            if (!File.Exists(credentialsPath))
            {
                error = "not signed in";
                return null;
            }

            using var credentials = JsonDocument.Parse(File.ReadAllText(credentialsPath));
            if (!credentials.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            {
                error = "no subscription login to capture";
                return null;
            }

            var account = ReadAccountBlock();
            var uuid = account?["accountUuid"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(uuid))
            {
                error = "no account id in .claude.json";
                return null;
            }

            var stored = new StoredAccount(
                uuid,
                account?["emailAddress"]?.GetValue<string>(),
                account?["displayName"]?.GetValue<string>(),
                account?["organizationName"]?.GetValue<string>(),
                account?["organizationUuid"]?.GetValue<string>(),
                DateTimeOffset.UtcNow);

            var record = new Record(
                stored.AccountUuid, stored.Email, stored.DisplayName,
                stored.OrganizationName, stored.OrganizationUuid, stored.CapturedAt,
                Protect(oauth.GetRawText()),
                account is null ? null : Protect(account.ToJsonString()));

            Directory.CreateDirectory(Root);
            var path = PathFor(stored.AccountUuid);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
            File.Move(tmp, path, overwrite: true);

            return stored;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            return null;
        }
    }

    /// <summary>The decrypted OAuth object and account block for a stored account.</summary>
    internal bool TryRead(string accountUuid, out string oauthJson, out string? accountBlockJson)
    {
        oauthJson = "";
        accountBlockJson = null;
        try
        {
            var path = PathFor(accountUuid);
            if (!File.Exists(path)) return false;

            var r = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Json);
            if (r is null) return false;

            oauthJson = Unprotect(r.ProtectedOauth);
            accountBlockJson = r.ProtectedAccountBlock is null ? null : Unprotect(r.ProtectedAccountBlock);
            return oauthJson.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>The <c>oauthAccount</c> block of ~/.claude.json, as a mutable node.</summary>
    public static JsonObject? ReadAccountBlock()
    {
        try
        {
            var path = CredentialStore.ConfigPath;
            if (!File.Exists(path)) return null;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return root?["oauthAccount"]?.AsObject()?.DeepClone().AsObject();
        }
        catch { return null; }
    }

    private static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string encoded) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser));
}
