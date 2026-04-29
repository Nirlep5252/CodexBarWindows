namespace CodexBarWindows;

public enum UsageProvider
{
    Codex,
    Claude
}

public sealed record ProviderUsageSnapshot(
    UsageProvider Provider,
    DateTimeOffset ObservedAt,
    string? PlanType,
    ProviderUsageWindow Primary,
    ProviderUsageWindow? Secondary,
    string Source);

public sealed record ProviderUsageWindow(
    string Title,
    double UsedPercent,
    int WindowMinutes,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Max(0, 100 - UsedPercent);
}

public sealed record ProviderUsageLookupResult(ProviderUsageSnapshot? Snapshot, string? Error)
{
    public bool HasSnapshot => Snapshot is not null;
}
