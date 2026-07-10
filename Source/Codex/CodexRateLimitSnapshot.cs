namespace CodexBarWindows;

public sealed record CodexRateLimitSnapshot(
    DateTimeOffset ObservedAt,
    string? PlanType,
    UsageWindow Primary,
    UsageWindow? Secondary,
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
                    WindowTitle(snapshot.Primary.WindowMinutes),
                    snapshot.Primary.UsedPercent,
                    snapshot.Primary.WindowMinutes,
                    snapshot.Primary.ResetsAt),
                snapshot.Secondary is { } secondary
                    ? new ProviderUsageWindow(
                        WindowTitle(secondary.WindowMinutes),
                        secondary.UsedPercent,
                        secondary.WindowMinutes,
                        secondary.ResetsAt)
                    : null,
                snapshot.Source),
            Error);
    }

    private static string WindowTitle(int windowMinutes)
    {
        return windowMinutes switch
        {
            300 => "5 hour limit",
            10080 => "Weekly limit",
            _ when windowMinutes >= 60 && windowMinutes % 60 == 0 => $"{windowMinutes / 60} hour limit",
            _ => $"{windowMinutes} minute limit"
        };
    }
}
