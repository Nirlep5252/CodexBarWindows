namespace CodexBarWindows;

public enum UsageProvider
{
    Codex,
    Claude,
    Cursor
}

public sealed record ProviderUsageSnapshot(
    UsageProvider Provider,
    DateTimeOffset ObservedAt,
    string? PlanType,
    ProviderUsageWindow Primary,
    ProviderUsageWindow? Secondary,
    string Source,
    ProviderUsageWindow? Tertiary = null,
    ProviderUsageCost? Cost = null,
    string? AccountEmail = null,
    IReadOnlyList<ProviderUsageWindow>? AdditionalWindows = null,
    // Codex-only: no other provider banks redeemable window resets, so this stays a
    // nullable provider-specific field rather than a shared abstraction.
    CodexResetCredits? ResetCredits = null)
{
    public IReadOnlyList<ProviderUsageWindow> Windows
    {
        get
        {
            var windows = new List<ProviderUsageWindow> { Primary };
            if (Secondary is not null)
            {
                windows.Add(Secondary);
            }

            if (Tertiary is not null)
            {
                windows.Add(Tertiary);
            }

            if (AdditionalWindows is not null)
            {
                windows.AddRange(AdditionalWindows);
            }

            return windows;
        }
    }
}

public sealed record ProviderUsageWindow(
    string Title,
    double UsedPercent,
    int WindowMinutes,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Max(0, 100 - UsedPercent);
}

public sealed record ProviderUsageCost(
    decimal Used,
    decimal? Limit,
    string CurrencyCode,
    string Period,
    DateTimeOffset? ResetsAt);

public sealed record ProviderUsageLookupResult(ProviderUsageSnapshot? Snapshot, string? Error, bool IsStale = false)
{
    public bool HasSnapshot => Snapshot is not null;

    /// <summary>
    /// Carries the previous snapshot forward when a limits refresh fails, so the popup keeps
    /// showing the last known limits (annotated as stale) instead of an error-only view.
    /// </summary>
    public static ProviderUsageLookupResult KeepLastGood(
        ProviderUsageLookupResult? previous,
        ProviderUsageLookupResult next)
    {
        return next.Snapshot is null && previous?.Snapshot is not null
            ? new ProviderUsageLookupResult(previous.Snapshot, next.Error, IsStale: true)
            : next;
    }
}
