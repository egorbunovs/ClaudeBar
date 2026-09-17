using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeBar.Models;

namespace ClaudeBar.Services;

/// <summary>
/// Swaps the signed-in account by writing Claude Code's own credential file.
///
/// Why this is the right mechanism, and not a guess: running `/login` in one terminal
/// already changes the account for every other running terminal, with no restarts. That can
/// only work because every `claude` process re-reads ~/.claude/.credentials.json from disk
/// when its cached token expires or gets a 401. Writing that file does the same thing,
/// without the browser round trip.
///
/// The safety rules this class exists to enforce:
///   1. re-capture the live credentials into the CURRENT account's slot first, so a token
///      Claude Code has since rotated is never replaced by a stale copy;
///   2. back up both files before touching them;
///   3. verify afterwards with `claude auth status --json`, and ROLL BACK if the account
///      that comes back is not the one asked for.
///
/// A tool that quietly breaks a login is far worse than one that takes an extra click.
/// </summary>
public sealed class AccountSwitcher
{
    private readonly AccountStore _store = new();

    public sealed record Result(bool Ok, string Message, bool RolledBack = false);

    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeBar", "backups");

    /// <summary>Who `claude` itself says is signed in. The authority for verification.</summary>
    public static (string? Email, string? Uuid, string? Error) CurrentAccount()
    {
        try
        {
            var exe = ClaudeExecutable();
            if (exe is null) return (null, null, "claude not found");

            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--json");

            using var proc = Process.Start(psi);
            if (proc is null) return (null, null, "could not run claude");

            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(20_000))
            {
                try { proc.Kill(true); } catch { /* best effort */ }
                return (null, null, "claude auth status timed out");
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.False)
                return (null, null, "not signed in");

            var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            var uuid = AccountStore.ReadAccountBlock()?["accountUuid"]?.GetValue<string>();
            return (email, uuid, null);
        }
        catch (Exception ex)
        {
            return (null, null, ex.GetType().Name);
        }
    }

    public StoredAccount? CaptureCurrent(out string? error) => _store.CaptureCurrent(out error);

    public IReadOnlyList<StoredAccount> Accounts() => _store.List();

    public Result SwitchTo(StoredAccount target)
    {
        if (!_store.TryRead(target.AccountUuid, out var oauthJson, out var accountBlockJson))
            return new Result(false, "no stored credentials for that account");

        // 1. Re-capture the live account first. If Claude Code has rotated its refresh token
        //    since it was added, this is what keeps the stored copy usable.
        var recaptured = _store.CaptureCurrent(out var captureError);
        if (recaptured is null)
            Diagnostics.Log(() => $"switch: could not re-capture current account ({captureError})");

        // 2. Back up both files.
        string backupDir;
        try
        {
            backupDir = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backupDir);
            CopyIfExists(CredentialStore.CredentialsPath, Path.Combine(backupDir, "credentials.json"));
            CopyIfExists(CredentialStore.ConfigPath, Path.Combine(backupDir, "claude.json"));
        }
        catch (Exception ex)
        {
            return new Result(false, $"could not back up first ({ex.GetType().Name}) - nothing changed");
        }

        // 3. Write the target's credentials, then patch the identity block.
        try
        {
            WriteCredentials(oauthJson);
            if (accountBlockJson is not null) PatchAccountBlock(accountBlockJson);
        }
        catch (Exception ex)
        {
            Restore(backupDir);
            return new Result(false, $"switch failed ({ex.GetType().Name}) - rolled back", true);
        }

        // 4. Verify with claude itself, and roll back if it disagrees.
        var (email, _, error) = CurrentAccount();
        if (error is not null)
        {
            Diagnostics.Log(() => $"switch: could not verify ({error}); leaving the switch in place");
            return new Result(true, $"switched to {target.Label} (unverified: {error})");
        }

        var expected = target.Email;
        if (expected is { Length: > 0 } &&
            !string.Equals(email, expected, StringComparison.OrdinalIgnoreCase))
        {
            Restore(backupDir);
            return new Result(false,
                $"claude reports {email ?? "nobody"} after switching to {expected} - rolled back", true);
        }

        Diagnostics.Log(() => $"switch: now signed in as {email}");
        return new Result(true, $"switched to {target.Label}");
    }

    private static void WriteCredentials(string oauthJson)
    {
        var path = CredentialStore.CredentialsPath;

        // Preserve any other top-level keys Claude Code keeps in there.
        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch { root = new JsonObject(); }

        root["claudeAiOauth"] = JsonNode.Parse(oauthJson);
        AtomicWrite(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void PatchAccountBlock(string accountBlockJson)
    {
        var path = CredentialStore.ConfigPath;
        if (!File.Exists(path)) return;

        // Only oauthAccount is replaced; the other ~79KB of settings are left exactly as is.
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (root is null) return;

        root["oauthAccount"] = JsonNode.Parse(accountBlockJson);
        AtomicWrite(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AtomicWrite(string path, string contents)
    {
        var tmp = path + ".claudebar.tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    private static void CopyIfExists(string from, string to)
    {
        if (File.Exists(from)) File.Copy(from, to, overwrite: true);
    }

    private static void Restore(string backupDir)
    {
        try
        {
            CopyIfExists(Path.Combine(backupDir, "credentials.json"), CredentialStore.CredentialsPath);
            CopyIfExists(Path.Combine(backupDir, "claude.json"), CredentialStore.ConfigPath);
            Diagnostics.Log(() => $"switch: rolled back from {backupDir}");
        }
        catch (Exception ex)
        {
            // Nothing else to try; say where the originals are so they can be put back by hand.
            Diagnostics.Log(() => $"switch: ROLLBACK FAILED ({ex.GetType().Name}); backups in {backupDir}");
        }
    }

    public static string? ClaudeExecutable()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), "claude.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
