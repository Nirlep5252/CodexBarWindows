namespace CodexBarWindows;

/// <summary>
/// Which rate column a row bills at. The three are mutually exclusive by OpenAI's own rules, which
/// is why this is an enum and not two independent booleans.
/// </summary>
/// <remarks>
/// <see cref="Priority"/> WINS over <see cref="LongContext"/> and is not a multiple of it: a
/// priority turn is capped at <see cref="CodexModelPricing.PriorityInputTokenLimit"/> input tokens,
/// so it can never also be long context, and above that ceiling priority simply does not apply.
/// </remarks>
internal enum CodexRateTier
{
    Base,
    LongContext,
    Priority
}

/// <summary>
/// Per-million rates for one Codex model, in USD.
/// </summary>
/// <remarks>
/// The priority (fast) columns are per MODEL and are NOT a constant multiple of the base column -
/// gpt-5.5 is 2.5x (5.00 -&gt; 12.50 input, 30.00 -&gt; 75.00 output) while every other model with
/// priority rates is 2x. Anything that derives a fast price by multiplying the base price is wrong
/// by 25% on gpt-5.5, which is why nothing in this codebase is allowed to do that.
/// </remarks>
internal readonly record struct CodexModelRates(
    decimal InputPerMillion,
    decimal CachedInputPerMillion,
    decimal OutputPerMillion,
    int? ThresholdTokens = null,
    decimal? InputPerMillionAboveThreshold = null,
    decimal? CachedInputPerMillionAboveThreshold = null,
    decimal? OutputPerMillionAboveThreshold = null,
    decimal? PriorityInputPerMillion = null,
    decimal? PriorityCachedInputPerMillion = null,
    decimal? PriorityOutputPerMillion = null)
{
    /// <summary>
    /// Whether priority rates exist at all for this model. Matches the reader's guard exactly:
    /// input AND output must both be present, because a half-populated entry would silently borrow
    /// the base column for the missing half.
    /// </summary>
    public bool HasPriorityRates => PriorityInputPerMillion is not null && PriorityOutputPerMillion is not null;
}

/// <summary>
/// THE Codex pricing table, and the only place Codex token counts turn into money.
/// </summary>
/// <remarks>
/// <para>
/// This used to be a private static inside <see cref="CodexUsageInsightsReader"/>, unreachable from
/// the ledger read path - which is how ledger-backed history came to price every FAST turn at base
/// rates while reporting the cost as complete. A second table would have re-created the same class
/// of bug from the other side (this codebase has already shipped one pricing-drift bug: "No pricing
/// for gpt-5.5, gpt-5.6-sol"), so the table moved here instead and BOTH paths consume it:
/// the scan path through the reader, the ledger path through <c>LedgerPricing</c>.
/// </para>
/// <para>
/// The arithmetic moved with it deliberately. Cost is a step function of a row's own token counts,
/// so "which rates apply" and "how the components combine" have to be one decision made in one
/// place; splitting them is how the two paths would drift while both still looking correct.
/// </para>
/// </remarks>
internal static class CodexModelPricing
{
    /// <summary>
    /// Priority (fast) rates stop applying above this many INPUT tokens, independently of any
    /// long-context threshold. gpt-5.4-mini has priority rates and no threshold at all, so the two
    /// bits are genuinely separate and neither may be derived from the other.
    /// </summary>
    public const int PriorityInputTokenLimit = 272_000;

    /// <summary>
    /// Resolves per-million rates for a Codex model, or <c>null</c> when the model is unknown.
    /// Unknown models must not borrow another model's rates: a silent fallback is how a whole
    /// generation of models (gpt-5.6) got billed at gpt-5 rates without anything looking wrong.
    /// </summary>
    public static CodexModelRates? For(string model)
    {
        var normalized = NormalizeModelName(model);
        if (CodexPricing.TryGetValue(normalized, out var pricing))
        {
            return pricing;
        }

        if (normalized.Contains("gpt-4.1", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexModelRates(2.00m, 0.50m, 8.00m);
        }

        if (normalized.Contains("o4-mini", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexModelRates(1.10m, 0.275m, 4.40m);
        }

        if (normalized.Contains("o3", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexModelRates(2.00m, 0.50m, 8.00m);
        }

        return ModelsDevRatesFor(normalized);
    }

    /// <summary>The long-context cutoff for a model, or null when it has none / is unknown.</summary>
    public static int? ThresholdTokensFor(string model) => For(model)?.ThresholdTokens;

    /// <summary>
    /// Picks the rate column for a row from its own token counts, exactly as the scan does.
    /// </summary>
    /// <param name="isFast">The resolved fast/priority observation, not the raw per-row hint.</param>
    /// <param name="inputTokens">Total input INCLUDING cached input, the TokenTotals convention.</param>
    public static CodexRateTier TierFor(CodexModelRates rates, bool isFast, long inputTokens)
    {
        // Priority first and unconditionally: when it applies, long context does not, because the
        // ceiling that disqualifies priority sits at or below every published threshold.
        if (isFast && inputTokens <= PriorityInputTokenLimit && rates.HasPriorityRates)
        {
            return CodexRateTier.Priority;
        }

        return rates.ThresholdTokens is { } threshold && inputTokens > threshold
            ? CodexRateTier.LongContext
            : CodexRateTier.Base;
    }

    /// <summary>
    /// Prices one row's components at the given tier.
    /// </summary>
    /// <remarks>
    /// Per-component divide-then-multiply, in this order, because the reader's published figures
    /// (and the tests that pin them) were produced by exactly this expression. Do not "simplify" it
    /// into a single division of the sum: decimal rounding differs and the pinned costs move.
    /// </remarks>
    /// <param name="inputTokens">Total input INCLUDING cached input; billable raw input is the difference.</param>
    public static decimal Estimate(CodexModelRates rates, long inputTokens, long cachedInputTokens, long outputTokens, CodexRateTier tier)
    {
        var billableInput = Math.Max(0, inputTokens - cachedInputTokens);
        var inputPerMillion = tier switch
        {
            CodexRateTier.Priority => rates.PriorityInputPerMillion ?? rates.InputPerMillion,
            CodexRateTier.LongContext => rates.InputPerMillionAboveThreshold ?? rates.InputPerMillion,
            _ => rates.InputPerMillion
        };
        var cachedInputPerMillion = tier switch
        {
            CodexRateTier.Priority => rates.PriorityCachedInputPerMillion ?? rates.CachedInputPerMillion,
            CodexRateTier.LongContext => rates.CachedInputPerMillionAboveThreshold ?? rates.CachedInputPerMillion,
            _ => rates.CachedInputPerMillion
        };
        var outputPerMillion = tier switch
        {
            CodexRateTier.Priority => rates.PriorityOutputPerMillion ?? rates.OutputPerMillion,
            CodexRateTier.LongContext => rates.OutputPerMillionAboveThreshold ?? rates.OutputPerMillion,
            _ => rates.OutputPerMillion
        };

        return ((decimal)billableInput / 1_000_000m * inputPerMillion) +
               ((decimal)cachedInputTokens / 1_000_000m * cachedInputPerMillion) +
               ((decimal)outputTokens / 1_000_000m * outputPerMillion);
    }

    private static CodexModelRates? ModelsDevRatesFor(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized) || ModelsDevPricing.Lookup("openai", normalized) is not { } info)
        {
            return null;
        }

        return new CodexModelRates(
            info.InputPerMillion,
            info.CacheReadPerMillion ?? info.InputPerMillion,
            info.OutputPerMillion,
            info.ThresholdTokens,
            info.InputPerMillionAboveThreshold,
            info.CacheReadPerMillionAboveThreshold,
            info.OutputPerMillionAboveThreshold);
    }

    /// <summary>
    /// The display label a Codex row is grouped under, in both the scan breakdown and the ledger.
    /// </summary>
    /// <remarks>
    /// It lives beside the normalisation rather than in either reader because BOTH paths label the
    /// same rows and the graphs window keys per-model colour overrides off the resulting string: when
    /// the ledger labelled with the raw logged id and the scan with the normalized one, a single
    /// model appeared as two legend entries with two colours depending on which source answered.
    /// </remarks>
    public static string BreakdownLabel(string model, bool isFast)
    {
        var label = string.IsNullOrWhiteSpace(model) ? "Codex model" : NormalizeModelName(model);
        return isFast ? label + " fast" : label;
    }

    public static string NormalizeModelName(string model)
    {
        var normalized = (model ?? string.Empty).Trim().ToLowerInvariant();
        const string openAiPrefix = "openai/";
        if (normalized.StartsWith(openAiPrefix, StringComparison.Ordinal))
        {
            normalized = normalized[openAiPrefix.Length..];
        }

        // OpenAI routes the unsuffixed gpt-5.6 alias to Sol.
        if (string.Equals(normalized, "gpt-5.6", StringComparison.Ordinal))
        {
            return "gpt-5.6-sol";
        }

        if (CodexPricing.ContainsKey(normalized))
        {
            return normalized;
        }

        var datedSuffix = System.Text.RegularExpressions.Regex.Match(normalized, "-\\d{4}-\\d{2}-\\d{2}$");
        if (datedSuffix.Success)
        {
            var withoutDate = normalized[..datedSuffix.Index];
            if (CodexPricing.ContainsKey(withoutDate))
            {
                return withoutDate;
            }
        }

        return normalized;
    }

    private static readonly IReadOnlyDictionary<string, CodexModelRates> CodexPricing = new Dictionary<string, CodexModelRates>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5-codex"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5-mini"] = new(0.25m, 0.025m, 2.00m),
        ["gpt-5-nano"] = new(0.05m, 0.005m, 0.40m),
        ["gpt-5-pro"] = new(15.00m, 15.00m, 120.00m),
        ["gpt-5.1"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5.1-codex"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5.1-codex-max"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5.1-codex-mini"] = new(0.25m, 0.025m, 2.00m),
        ["gpt-5.2"] = new(1.75m, 0.175m, 14.00m),
        ["gpt-5.2-codex"] = new(1.75m, 0.175m, 14.00m),
        ["gpt-5.2-pro"] = new(21.00m, 21.00m, 168.00m),
        ["gpt-5.3-codex"] = new(1.75m, 0.175m, 14.00m),
        ["gpt-5.3-codex-spark"] = new(0.00m, 0.00m, 0.00m),
        ["gpt-5.4"] = new(2.50m, 0.25m, 15.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 5.00m, CachedInputPerMillionAboveThreshold: 0.50m, OutputPerMillionAboveThreshold: 22.50m, PriorityInputPerMillion: 5.00m, PriorityCachedInputPerMillion: 0.50m, PriorityOutputPerMillion: 30.00m),
        ["gpt-5.4-mini"] = new(0.75m, 0.075m, 4.50m, PriorityInputPerMillion: 1.50m, PriorityCachedInputPerMillion: 0.15m, PriorityOutputPerMillion: 9.00m),
        ["gpt-5.4-nano"] = new(0.20m, 0.020m, 1.25m),
        ["gpt-5.4-pro"] = new(30.00m, 30.00m, 180.00m),
        ["gpt-5.5"] = new(5.00m, 0.50m, 30.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 10.00m, CachedInputPerMillionAboveThreshold: 1.00m, OutputPerMillionAboveThreshold: 45.00m, PriorityInputPerMillion: 12.50m, PriorityCachedInputPerMillion: 1.25m, PriorityOutputPerMillion: 75.00m),
        ["gpt-5.5-pro"] = new(30.00m, 30.00m, 180.00m),
        ["gpt-5.6-sol"] = new(5.00m, 0.50m, 30.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 10.00m, CachedInputPerMillionAboveThreshold: 1.00m, OutputPerMillionAboveThreshold: 45.00m, PriorityInputPerMillion: 10.00m, PriorityCachedInputPerMillion: 1.00m, PriorityOutputPerMillion: 60.00m),
        ["gpt-5.6-terra"] = new(2.50m, 0.25m, 15.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 5.00m, CachedInputPerMillionAboveThreshold: 0.50m, OutputPerMillionAboveThreshold: 22.50m, PriorityInputPerMillion: 5.00m, PriorityCachedInputPerMillion: 0.50m, PriorityOutputPerMillion: 30.00m),
        ["gpt-5.6-luna"] = new(1.00m, 0.10m, 6.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 2.00m, CachedInputPerMillionAboveThreshold: 0.20m, OutputPerMillionAboveThreshold: 9.00m, PriorityInputPerMillion: 2.00m, PriorityCachedInputPerMillion: 0.20m, PriorityOutputPerMillion: 12.00m),
    };
}
