namespace CodexBarWindows;

/// <summary>
/// One banked Codex rate-limit reset credit, as reported by the Codex app-server under
/// <c>account/rateLimits/read</c> → <c>rateLimitResetCredits.credits[]</c>.
/// </summary>
/// <remarks>
/// Distinct from the <c>credits</c> balance on a rate-limit bucket, which is prepaid
/// spend rather than a banked window reset.
/// </remarks>
public sealed record CodexResetCredit(
    string Id,
    string? Status,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt,
    string? Title,
    string? Description)
{
    /// <summary>
    /// Whether this credit can still be redeemed. An unrecognised status is treated as
    /// available so a backend schema change surfaces the credit instead of hiding it;
    /// redeeming a stale credit fails safely with <see cref="CodexResetOutcome.NoCredit"/>.
    /// </summary>
    public bool IsAvailable =>
        string.IsNullOrWhiteSpace(Status) ||
        !string.Equals(Status, "redeemed", StringComparison.OrdinalIgnoreCase);

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Full reset" : Title.Trim();
}

/// <summary>
/// The reset-credit inventory for one Codex account.
/// </summary>
/// <param name="AvailableCount">
/// Backend-reported count of redeemable credits. The backend may cap
/// <paramref name="Credits"/>, so this can exceed the detail row count.
/// </param>
/// <param name="Credits">
/// Detail rows when the backend supplied them; empty when only the count is known.
/// </param>
public sealed record CodexResetCredits(int AvailableCount, IReadOnlyList<CodexResetCredit> Credits)
{
    public static readonly CodexResetCredits None = new(0, []);

    public bool HasAny => AvailableCount > 0;

    /// <summary>Redeemable credits, soonest-expiring first; non-expiring credits sort last.</summary>
    public IReadOnlyList<CodexResetCredit> AvailableByExpiry => Credits
        .Where(credit => credit.IsAvailable)
        .OrderBy(credit => credit.ExpiresAt ?? DateTimeOffset.MaxValue)
        .ToArray();

    /// <summary>
    /// The credit to spend first — use-it-or-lose-it. Null when the backend reported a
    /// count without detail rows, in which case there is no id to redeem explicitly.
    /// </summary>
    public CodexResetCredit? NextExpiring => AvailableByExpiry.FirstOrDefault();

    /// <summary>Finds a credit by opaque backend id, or null when it is no longer offered.</summary>
    public CodexResetCredit? Find(string creditId) => Credits
        .FirstOrDefault(credit => string.Equals(credit.Id, creditId, StringComparison.Ordinal));
}

/// <summary>
/// A user-confirmed request to spend <paramref name="Credit"/> on the Codex account behind
/// <paramref name="ProviderKey"/>. Both halves travel together so the credit can never be
/// charged against a different account's CLI binary.
/// </summary>
public sealed record CodexResetRedeemRequest(string ProviderKey, CodexResetCredit Credit);
