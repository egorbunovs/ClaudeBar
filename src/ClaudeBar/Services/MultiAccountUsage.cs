using ClaudeBar.Models;

namespace ClaudeBar.Services;

/// <summary>One account's usage, for the pill.</summary>
public sealed record AccountUsage(StoredAccount Account, bool IsActive, UsageSnapshot Snapshot);

/// <summary>
/// Reads usage for every stored account, so the pill can show which one has headroom before
/// switching.
///
/// The active account uses Claude Code's own live token and is never refreshed by us. Idle
/// accounts use ClaudeBar's stored copy, refreshed through <see cref="TokenRefresher"/> when
/// it is about to expire — the one sanctioned exception to the no-refresh rule.
/// </summary>
public sealed class MultiAccountUsage
{
    private readonly UsageService _usage;
    private readonly AccountStore _store = new();
    private readonly TokenRefresher _refresher = new();

    /// <summary>
    /// Kept per account so a blip on one (a 429, say) does not blank it: the last good
    /// reading stays on screen, dimmed, until a fresh one arrives.
    /// </summary>
    private readonly Dictionary<string, UsageSnapshot> _lastGood = new();

    /// <summary>
    /// The usage endpoint has been seen returning 429 for two calls a couple of seconds apart,
    /// so calls for different accounts are spaced out rather than fired together.
    /// </summary>
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(1500);

    public MultiAccountUsage(UsageService usage) => _usage = usage;

    public async Task<List<AccountUsage>> PollAllAsync(UsageSnapshot activeSnapshot, CancellationToken ct)
    {
        var results = new List<AccountUsage>();
        var activeUuid = CredentialWatcher.CurrentUuid();
        var accounts = _store.List();

        // Active account first, straight from the live poll that was just made.
        var active = accounts.FirstOrDefault(a => a.AccountUuid == activeUuid);
        if (active is not null)
        {
            Remember(active.AccountUuid, activeSnapshot);
            results.Add(new AccountUsage(active, true, WithFallback(active.AccountUuid, activeSnapshot)));
        }

        foreach (var account in accounts.Where(a => a.AccountUuid != activeUuid))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(Spacing, ct).ConfigureAwait(false);

            var snapshot = await PollIdleAsync(account, ct).ConfigureAwait(false);
            Remember(account.AccountUuid, snapshot);
            results.Add(new AccountUsage(account, false, WithFallback(account.AccountUuid, snapshot)));
        }

        return results;
    }

    private async Task<UsageSnapshot> PollIdleAsync(StoredAccount account, CancellationToken ct)
    {
        if (!_store.TryRead(account.AccountUuid, out var oauthJson, out _))
            return UsageSnapshot.Failed("no stored token");

        if (TokenRefresher.NeedsRefresh(oauthJson))
        {
            var (refreshed, error) = await _refresher.RefreshAsync(oauthJson, ct).ConfigureAwait(false);
            if (refreshed is null)
            {
                Diagnostics.Log(() => $"usage[{account.Label}]: {error}");
                return UsageSnapshot.Failed(error?.Contains("invalid_grant") == true ? "sign in again" : "refresh failed");
            }

            // The old refresh token died the moment that call succeeded. Persist before
            // anything else can go wrong.
            if (!_store.UpdateOauth(account.AccountUuid, refreshed))
            {
                Diagnostics.Log(() => $"usage[{account.Label}]: refreshed but COULD NOT SAVE - account may need a fresh login");
                return UsageSnapshot.Failed("could not save refreshed token");
            }

            Diagnostics.Log(() => $"usage[{account.Label}]: token refreshed and saved");
            oauthJson = refreshed;
        }

        var token = TokenRefresher.AccessTokenOf(oauthJson);
        if (string.IsNullOrEmpty(token)) return UsageSnapshot.Failed("no access token");

        var snapshot = await _usage.PollWithTokenAsync(token, ct).ConfigureAwait(false);
        return snapshot.Ok ? snapshot with { Account = account.Email } : snapshot;
    }

    private void Remember(string uuid, UsageSnapshot snapshot)
    {
        if (snapshot.Ok) _lastGood[uuid] = snapshot;
    }

    /// <summary>A failed poll shows the last good reading, flagged, rather than nothing.</summary>
    private UsageSnapshot WithFallback(string uuid, UsageSnapshot snapshot)
    {
        if (snapshot.Ok || !_lastGood.TryGetValue(uuid, out var last)) return snapshot;
        return last with { Error = snapshot.Error, RetryAfter = snapshot.RetryAfter };
    }
}
