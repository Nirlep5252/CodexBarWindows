namespace CodexBarWindows;

public sealed record CodexRateLimitSnapshot(
    DateTimeOffset ObservedAt,
    string? PlanType,
    UsageWindow FiveHour,
    UsageWindow Weekly,
    string Source);

public sealed record UsageWindow(
    double UsedPercent,
    int WindowMinutes,
    DateTimeOffset ResetsAt)
{
    public double RemainingPercent => Math.Max(0, 100 - UsedPercent);
}

public sealed record UsageLookupResult(CodexRateLimitSnapshot? Snapshot, string? Error)
{
    public bool HasSnapshot => Snapshot is not null;
}
