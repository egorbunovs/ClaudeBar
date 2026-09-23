using ClaudeBar.Models;

namespace ClaudeBar.Services;

/// <summary>One account's usage, for the pill.</summary>
public sealed record AccountUsage(StoredAccount Account, bool IsActive, UsageSnapshot Snapshot);

/// <summary>
/// Reads usage for an account Claude Code is NOT currently using, from ClaudeBar's stored
/// copy of its sign-in, refreshing the token through <see cref="TokenRefresher"/> when it is
/// about to expire — the one sanctioned exception to the no-refresh rule.
/// </summary>
public sealed class MultiAccountUsage
{
    private readonly UsageService _usage;
    private readonly AccountStore _store = new();
    private readonly TokenRefresher _refresher = new();

    public MultiAccountUsage(UsageService usage) => _usage = usage;

    public async Task<UsageSnapshot> PollIdleAsync(StoredAccount account, CancellationToken ct)
    {
        var (oauthJson, error) = await EnsureFreshAsync(account, ct).ConfigureAwait(false);
        if (oauthJson is null) return UsageSnapshot.Failed(error ?? "no stored token");

        var token = TokenRefresher.AccessTokenOf(oauthJson);
        if (string.IsNullOrEmpty(token)) return UsageSnapshot.Failed("no access token");

        var snapshot = await _usage.PollWithTokenAsync(token, ct).ConfigureAwait(false);
        return snapshot.Ok ? snapshot with { Account = account.Email } : snapshot;
    }

    /// <summary>
    /// The stored sign-in, refreshed first if it is about to expire. Safe only while Claude
    /// Code is not using the account - which is also true of an account in the moment just
    /// before ClaudeBar switches to it, and that is the other caller: an idle account's copy
    /// is usually hours past expiry, and switching to it as-is left the first poll with a
    /// dead token and nothing to show.
    /// </summary>
    public async Task<(string? OauthJson, string? Error)> EnsureFreshAsync(StoredAccount account, CancellationToken ct)
    {
        if (!_store.TryRead(account.AccountUuid, out var oauthJson, out _))
            return (null, "no stored token");

        if (!TokenRefresher.NeedsRefresh(oauthJson)) return (oauthJson, null);

        var (refreshed, error) = await _refresher.RefreshAsync(oauthJson, ct).ConfigureAwait(false);
        if (refreshed is null)
        {
            Diagnostics.Log(() => $"usage[{account.Label}]: {error}");
            return (null, error?.Contains("invalid_grant") == true ? "sign in again" : "refresh failed");
        }

        // The old refresh token died the moment that call succeeded. Persist before
        // anything else can go wrong.
        if (!_store.UpdateOauth(account.AccountUuid, refreshed))
        {
            Diagnostics.Log(() => $"usage[{account.Label}]: refreshed but COULD NOT SAVE - account may need a fresh login");
            return (null, "could not save refreshed token");
        }

        Diagnostics.Log(() => $"usage[{account.Label}]: token refreshed and saved");
        return (refreshed, null);
    }
}
