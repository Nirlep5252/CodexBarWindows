namespace CodexBarWindows;

public sealed record ProviderUsageInsightsLookupResult(ProviderUsageInsights? Insights, string? Error)
{
    public bool HasInsights => Insights is not null;
}

public sealed record ProviderUsageInsights(
    DateTimeOffset ObservedAt,
    string Source,
    IReadOnlyList<ProviderDailyUsage> Daily,
    IReadOnlyList<ProviderModelUsage> Models,
    long TodayTokens,
    decimal TodayEstimatedCostUsd,
    long Last30DaysTokens,
    decimal Last30DaysEstimatedCostUsd,
    decimal TodayFastEstimatedCostUsd = 0,
    decimal Last30DaysFastEstimatedCostUsd = 0,
    bool HasIncompleteCost = false)
{
    public bool HasUsage => Last30DaysTokens > 0 || Last30DaysEstimatedCostUsd > 0;
}

public sealed record ProviderDailyUsage(
    DateOnly Day,
    long InputTokens,
    long CachedInputTokens,
    long CacheCreationTokens,
    long OutputTokens,
    decimal EstimatedCostUsd,
    decimal FastEstimatedCostUsd = 0,
    IReadOnlyList<ProviderSpendCategory>? SpendCategories = null,
    bool HasIncompleteCost = false)
{
    public long TotalTokens => InputTokens + CacheCreationTokens + OutputTokens;
    public decimal RegularEstimatedCostUsd => Math.Max(0, EstimatedCostUsd - FastEstimatedCostUsd);
    public IReadOnlyList<ProviderSpendCategory> Categories => SpendCategories ?? [];
}

public sealed record ProviderSpendCategory(string Label, decimal EstimatedCostUsd);

public sealed record ProviderModelUsage(
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long CacheCreationTokens,
    long OutputTokens,
    decimal EstimatedCostUsd,
    decimal FastEstimatedCostUsd = 0,
    bool HasIncompleteCost = false)
{
    public long TotalTokens => InputTokens + CacheCreationTokens + OutputTokens;
    public decimal RegularEstimatedCostUsd => Math.Max(0, EstimatedCostUsd - FastEstimatedCostUsd);
}
