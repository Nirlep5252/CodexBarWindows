namespace CodexBarWindows;

public sealed record ProviderUsageInsightsLookupResult(ProviderUsageInsights? Insights, string? Error, bool IsStale = false)
{
    public bool HasInsights => Insights is not null;

    /// <summary>
    /// Carries the previous insights forward when a refresh fails, so a transient error shows
    /// last-known-good data annotated as stale instead of blanking the view.
    /// </summary>
    /// <remarks>
    /// Staleness is explicit rather than inferred from "has data and an error": a fresh result
    /// can legitimately carry a warning alongside good data, and that must not be marked stale.
    /// A reader that genuinely found no session logs returns non-null empty insights, so it
    /// replaces the previous value here rather than being masked by it.
    /// </remarks>
    public static ProviderUsageInsightsLookupResult KeepLastGood(
        ProviderUsageInsightsLookupResult? previous,
        ProviderUsageInsightsLookupResult next)
    {
        return next.Insights is null && previous?.Insights is not null
            ? new ProviderUsageInsightsLookupResult(previous.Insights, next.Error, IsStale: true)
            : next;
    }
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
