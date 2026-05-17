namespace CodexBarWindows;

public sealed record CodexUsageInsightsLookupResult(CodexUsageInsights? Insights, string? Error)
{
    public bool HasInsights => Insights is not null;
}

public sealed record CodexUsageInsights(
    DateTimeOffset ObservedAt,
    string Source,
    IReadOnlyList<CodexDailyUsage> Daily,
    IReadOnlyList<CodexModelUsage> Models,
    long TodayTokens,
    decimal TodayEstimatedCostUsd,
    long Last30DaysTokens,
    decimal Last30DaysEstimatedCostUsd)
{
    public bool HasUsage => Last30DaysTokens > 0 || Last30DaysEstimatedCostUsd > 0;
}

public sealed record CodexDailyUsage(
    DateOnly Day,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal EstimatedCostUsd)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed record CodexModelUsage(
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal EstimatedCostUsd)
{
    public long TotalTokens => InputTokens + OutputTokens;
}
