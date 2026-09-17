using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeBar.Models;
using ClaudeBar.Services;

namespace ClaudeBar;

/// <summary>
/// Exercises the account store and switcher end to end, against whatever CLAUDE_CONFIG_DIR
/// points at. Run it against a THROWAWAY COPY of the config, never the live one:
///
///   $env:CLAUDE_CONFIG_DIR = "C:\temp\sandbox"; .\ClaudeBar.exe --selftest
///
/// It covers the two paths that matter and cannot be checked by clicking around: that a
/// captured account survives the encrypt/decrypt round trip, and that a switch whose
/// verification fails actually rolls the credentials back.
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var log = new StringBuilder();
        void Say(string line)
        {
            log.AppendLine(line);
            Console.WriteLine(line);
        }

        var ok = true;
        try
        {
            Say($"config dir      : {CredentialStore.ClaudeDir}");
            Say($"credentials file: {(File.Exists(CredentialStore.CredentialsPath) ? "present" : "MISSING")}");
            Say($"claude.json     : {(File.Exists(CredentialStore.ConfigPath) ? "present" : "MISSING")}");

            var store = new AccountStore();
            var switcher = new AccountSwitcher();

            // 1. capture
            var captured = store.CaptureCurrent(out var captureError);
            if (captured is null)
            {
                Say($"FAIL capture    : {captureError}");
                return Finish(log, 1);
            }
            Say($"PASS capture    : {captured.Label} (org {captured.OrganizationName ?? "-"})");

            // 2. encrypted round trip - lengths only, never contents
            if (!store.TryRead(captured.AccountUuid, out var oauthJson, out var blockJson))
            {
                Say("FAIL round trip : could not read back what was just written");
                return Finish(log, 1);
            }

            using (var doc = JsonDocument.Parse(oauthJson))
            {
                var hasToken = doc.RootElement.TryGetProperty("accessToken", out var t)
                               && t.GetString() is { Length: > 0 };
                var hasRefresh = doc.RootElement.TryGetProperty("refreshToken", out var r)
                                 && r.GetString() is { Length: > 0 };
                Say($"{(hasToken && hasRefresh ? "PASS" : "FAIL")} round trip : oauth decrypted, "
                    + $"accessToken={(hasToken ? "yes" : "no")} refreshToken={(hasRefresh ? "yes" : "no")} "
                    + $"({oauthJson.Length} chars, not shown)");
                ok &= hasToken && hasRefresh;
            }
            Say($"INFO account blk: {(blockJson is null ? "none" : blockJson.Length + " chars")}");

            // 3. the dangerous path: a switch whose verification fails must roll back
            var before = File.ReadAllText(CredentialStore.CredentialsPath);
            var bogus = captured with { Email = "definitely-not-this-account@example.invalid" };
            var result = switcher.SwitchTo(bogus);
            var after = File.ReadAllText(CredentialStore.CredentialsPath);

            var restored = before == after;
            Say($"{(!result.Ok && restored ? "PASS" : "FAIL")} rollback    : ok={result.Ok} "
                + $"rolledBack={result.RolledBack} credentialsRestored={restored}");
            Say($"                  message: {result.Message}");
            ok &= !result.Ok && restored;

            // 4. a switch to the real identity should succeed and verify
            var good = switcher.SwitchTo(captured);
            Say($"{(good.Ok ? "PASS" : "FAIL")} switch      : {good.Message}");
            ok &= good.Ok;
        }
        catch (Exception ex)
        {
            Say($"FAIL exception  : {ex}");
            ok = false;
        }

        return Finish(log, ok ? 0 : 1);
    }

    private static int Finish(StringBuilder log, int code)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeBar", "selftest.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, log.ToString());
            Console.WriteLine($"(written to {path})");
        }
        catch { /* the console output is the real result */ }
        return code;
    }
}
