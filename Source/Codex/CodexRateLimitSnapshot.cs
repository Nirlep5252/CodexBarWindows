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

    public ProviderUsageLookupResult ToProviderResult()
    {
        if (Snapshot is not { } snapshot)
        {
            return new ProviderUsageLookupResult(null, Error);
        }

        return new ProviderUsageLookupResult(
            new ProviderUsageSnapshot(
                UsageProvider.Codex,
                snapshot.ObservedAt,
                snapshot.PlanType,
                new ProviderUsageWindow(
                    "5 hour limit",
                    snapshot.FiveHour.UsedPercent,
                    snapshot.FiveHour.WindowMinutes,
                    snapshot.FiveHour.ResetsAt),
                new ProviderUsageWindow(
                    "Weekly limit",
                    snapshot.Weekly.UsedPercent,
                    snapshot.Weekly.WindowMinutes,
                    snapshot.Weekly.ResetsAt),
                snapshot.Source),
            null);
    }
}
