namespace ClaudeBar.Models;

/// <summary>
/// An account ClaudeBar knows how to switch to.
///
/// Identity only — the OAuth blob lives beside this, encrypted, and is never held in a
/// type that might end up in a log line or a tooltip.
/// </summary>
public sealed record StoredAccount(
    string AccountUuid,
    string? Email,
    string? DisplayName,
    string? OrganizationName,
    string? OrganizationUuid,
    DateTimeOffset CapturedAt)
{
    /// <summary>What the switcher menu shows.</summary>
    public string Label =>
        Email
        ?? DisplayName
        ?? (OrganizationName is { Length: > 0 } org ? org : AccountUuid[..Math.Min(8, AccountUuid.Length)]);

    public string Detail =>
        OrganizationName is { Length: > 0 } org && Email is { Length: > 0 }
            ? $"{Email} — {org}"
            : Label;
}
