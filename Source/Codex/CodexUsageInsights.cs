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
    decimal Last30DaysEstimatedCostUsd,
    decimal TodayFastEstimatedCostUsd = 0,
    decimal Last30DaysFastEstimatedCostUsd = 0)
{
    public bool HasUsage => Last30DaysTokens > 0 || Last30DaysEstimatedCostUsd > 0;
}

public sealed record CodexDailyUsage(
    DateOnly Day,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal EstimatedCostUsd,
    decimal FastEstimatedCostUsd = 0,
    IReadOnlyList<CodexSpendCategory>? SpendCategories = null)
{
    public long TotalTokens => InputTokens + OutputTokens;
    public decimal RegularEstimatedCostUsd => Math.Max(0, EstimatedCostUsd - FastEstimatedCostUsd);
    public IReadOnlyList<CodexSpendCategory> Categories => SpendCategories ?? [];
}

public sealed record CodexSpendCategory(string Label, decimal EstimatedCostUsd);

public sealed record CodexModelUsage(
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal EstimatedCostUsd,
    decimal FastEstimatedCostUsd = 0)
{
    public long TotalTokens => InputTokens + OutputTokens;
    public decimal RegularEstimatedCostUsd => Math.Max(0, EstimatedCostUsd - FastEstimatedCostUsd);
}
