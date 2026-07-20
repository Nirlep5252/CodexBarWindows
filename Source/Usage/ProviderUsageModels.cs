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
    IReadOnlyList<ProviderUsageWindow>? AdditionalWindows = null)
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

public sealed record ProviderUsageLookupResult(ProviderUsageSnapshot? Snapshot, string? Error)
{
    public bool HasSnapshot => Snapshot is not null;
}
