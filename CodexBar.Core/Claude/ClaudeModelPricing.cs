namespace CodexBarWindows;

/// <summary>
/// THE Claude pricing table, and the only place Claude token counts turn into money.
/// </summary>
/// <remarks>
/// <para>
/// The mirror image of <see cref="CodexModelPricing"/>, and it exists for the same reason. This
/// table used to be a private static inside <see cref="ClaudeUsageInsightsReader"/>, so the ledger
/// read path could only reach the network-fetched models.dev catalog: every Claude model the
/// catalog did not carry priced at $0.00 on the ledger path while the scan path priced the very
/// same rows correctly. Because a cost of zero is a NUMBER and not a null, the query had nothing to
/// flag as incomplete and the history simply understated spend. Both paths now consume this type -
/// the scan through the reader, the ledger through <c>LedgerPricing</c> - so a rate correction
/// re-prices every month ever recorded.
/// </para>
/// <para>
/// Claude's tiering differs from Codex's in a way that must not be "harmonised": each token
/// COMPONENT splits independently against <see cref="ModelsDevPricingInfo.ThresholdTokens"/>, so
/// within one request only the tokens past the cutoff bill higher. Codex instead reprices the whole
/// row once its input crosses. Applying either rule to the other provider silently mis-bills every
/// long request, which is why <see cref="Tiered"/> and <see cref="EstimateTier"/> both live here
/// rather than being expressed in terms of the Codex helpers.
/// </para>
/// <para>
/// Precedence is catalog-FIRST, built-in second (<c>models.dev ?? built-in</c>), verbatim from the
/// reader: the fetched catalog is the live source of truth, and this table is the offline floor that
/// keeps a model priceable when the fetch has never succeeded. Reversing it would freeze rates at
/// whatever shipped in the binary.
/// </para>
/// </remarks>
internal static class ClaudeModelPricing
{
    /// <summary>
    /// Rates for a Claude model, or <c>null</c> when neither source can price it.
    /// </summary>
    /// <remarks>
    /// Null is a deliberate answer, not a failure to try: it surfaces as <c>HasIncompleteCost</c>.
    /// Falling back to a neighbouring model's rates would make an unknown model look accounted for.
    /// </remarks>
    public static ModelsDevPricingInfo? For(string model)
        => ModelsDevPricing.Lookup("anthropic", model) ?? BuiltInFor(model);

    /// <summary>The long-context cutoff for a model, or null when it has none / is unknown.</summary>
    /// <remarks>
    /// The ONE resolver. The ledger's batch builder records a row's split against this value and the
    /// query re-resolves it to detect drift; when those two consulted different sources (the builder
    /// catalog-then-built-in, the query catalog-only) a row could be SPLIT at 200k and then priced
    /// as though it had no threshold at all.
    /// </remarks>
    public static int? ThresholdTokensFor(string model) => For(model)?.ThresholdTokens;

    /// <summary>
    /// Cost of one unsplit row: 0 for non-billable pseudo-models, null when the model is unknown.
    /// </summary>
    /// <param name="rawInput">Input EXCLUDING cache reads. TokenTotals.InputTokens is raw + cache read.</param>
    public static decimal? EstimateCost(string model, long rawInput, long cacheRead, long cacheCreate, long output)
    {
        // Synthetic/local entries cost nothing; report zero rather than "unpriced" so they do
        // not trigger the incomplete-cost warning.
        if (IsNonBillableModel(model))
        {
            return 0m;
        }

        if (For(model) is not { } pricing)
        {
            return null;
        }

        return Tiered(rawInput, InputRate(pricing), pricing.InputPerMillionAboveThreshold, pricing.ThresholdTokens) +
               Tiered(cacheRead, CacheReadRate(pricing), pricing.CacheReadPerMillionAboveThreshold, pricing.ThresholdTokens) +
               Tiered(cacheCreate, CacheCreationRate(pricing), pricing.CacheCreationPerMillionAboveThreshold, pricing.ThresholdTokens) +
               Tiered(output, OutputRate(pricing), pricing.OutputPerMillionAboveThreshold, pricing.ThresholdTokens);
    }

    /// <summary>
    /// Prices components that were ALREADY split at the cutoff, at one tier's rate column.
    /// </summary>
    /// <remarks>
    /// This is the ledger's half of the same arithmetic: the batch builder performed the per-component
    /// split at scan time, so pricing the sums equals pricing the rows and summing. Same
    /// divide-then-multiply per component, in the same order, as <see cref="Tiered"/> - the published
    /// figures the tests pin were produced by exactly this expression.
    /// </remarks>
    public static decimal EstimateTier(ModelsDevPricingInfo pricing, long rawInput, long cacheRead, long cacheCreate, long output, bool above)
    {
        return Component(rawInput, InputRate(pricing), pricing.InputPerMillionAboveThreshold, above) +
               Component(cacheRead, CacheReadRate(pricing), pricing.CacheReadPerMillionAboveThreshold, above) +
               Component(cacheCreate, CacheCreationRate(pricing), pricing.CacheCreationPerMillionAboveThreshold, above) +
               Component(output, OutputRate(pricing), pricing.OutputPerMillionAboveThreshold, above);
    }

    /// <summary>
    /// Splits ONE component at the cutoff and prices each part.
    /// </summary>
    /// <remarks>
    /// Applied per component, never to a row total: Anthropic's long-context premium is charged on
    /// the tokens of each kind past the cutoff, so a request with 300k input and 10k output pays the
    /// premium on 100k input tokens and on nothing else. A missing above-rate means the component has
    /// no premium at all, which is why it collapses to the base rate rather than to zero.
    /// </remarks>
    public static decimal Tiered(long tokens, decimal basePerMillion, decimal? abovePerMillion, int? threshold)
    {
        tokens = Math.Max(0, tokens);
        if (threshold is not { } cutoff || abovePerMillion is not { } above)
        {
            return tokens / 1_000_000m * basePerMillion;
        }

        var below = Math.Min(tokens, cutoff);
        var over = Math.Max(tokens - cutoff, 0);
        return below / 1_000_000m * basePerMillion + over / 1_000_000m * above;
    }

    private static decimal Component(long tokens, decimal basePerMillion, decimal? abovePerMillion, bool above)
        => Math.Max(0, tokens) / 1_000_000m * (above ? abovePerMillion ?? basePerMillion : basePerMillion);

    // The base rate column, with the reader's fallbacks. A catalog entry that omits a cache column
    // means "cache bills as input" for that model, NOT "cache is free".
    private static decimal InputRate(ModelsDevPricingInfo pricing) => pricing.InputPerMillion;

    private static decimal CacheReadRate(ModelsDevPricingInfo pricing) => pricing.CacheReadPerMillion ?? pricing.InputPerMillion;

    private static decimal CacheCreationRate(ModelsDevPricingInfo pricing) => pricing.CacheCreationPerMillion ?? pricing.InputPerMillion;

    private static decimal OutputRate(ModelsDevPricingInfo pricing) => pricing.OutputPerMillion;

    private static ModelsDevPricingInfo? BuiltInFor(string model)
    {
        var normalized = NormalizeModelName(model);
        return ClaudePricing.TryGetValue(normalized, out var pricing) ? pricing : null;
    }

    /// <summary>
    /// True when a NORMALIZED model id belongs to Anthropic.
    /// </summary>
    /// <remarks>
    /// Checked after <see cref="NormalizeModelName"/>, which already resolves bare aliases
    /// ("opus" → "claude-opus-5") and strips the Bedrock "anthropic." prefix, so every Anthropic
    /// id — first-party, Bedrock, or Vertex ("claude-opus-4-5@20251101") — begins with "claude"
    /// by that point. Synthetic markers are excluded too: they carry no usage either way.
    /// </remarks>
    public static bool IsAnthropicModel(string normalizedModel)
    {
        return !IsNonBillableModel(normalizedModel) &&
            normalizedModel.TrimStart().StartsWith("claude", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pseudo-models that carry no billable usage. Claude Code writes "&lt;synthetic&gt;" for messages
    /// it generates locally; these must not count as unpriced models or the cost warning fires
    /// on every scan.
    /// </summary>
    public static bool IsNonBillableModel(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return value.Length == 0 ||
            value.Equals("<synthetic>", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith('<');
    }

    /// <summary>
    /// Session logs sometimes record a bare family name instead of a full model id. Map those to
    /// the current model in each family so they price rather than falling through as unknown.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ClaudeModelAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fable"] = "claude-fable-5",
            ["opus"] = "claude-opus-5",
            ["sonnet"] = "claude-sonnet-5",
            ["haiku"] = "claude-haiku-4-5"
        };

    /// <summary>
    /// The canonical display/lookup id for a logged Claude model.
    /// </summary>
    /// <remarks>
    /// Also the LABEL both paths group by. The scan buckets its model breakdown on this, so the
    /// ledger has to as well: labelling ledger rows with the raw logged id split one model into two
    /// rows — and two colour overrides — depending on which source answered the query.
    /// <para/>
    /// The dated-suffix collapse is conditional on the table containing the undated key on purpose:
    /// an unknown dated id keeps its date so it stays distinguishable rather than being folded into
    /// a family it may not share rates with.
    /// </remarks>
    public static string NormalizeModelName(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (ClaudeModelAliases.TryGetValue(value, out var aliased))
        {
            return aliased;
        }

        if (value.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase))
        {
            value = value["anthropic.".Length..];
        }

        var lastDot = value.LastIndexOf('.');
        if (lastDot >= 0 && value.Contains("claude-", StringComparison.OrdinalIgnoreCase))
        {
            var tail = value[(lastDot + 1)..];
            if (tail.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            {
                value = tail;
            }
        }

        value = System.Text.RegularExpressions.Regex.Replace(value, "-v\\d+:\\d+$", string.Empty);
        var compactDate = System.Text.RegularExpressions.Regex.Match(value, "-\\d{8}$");
        if (compactDate.Success)
        {
            var baseName = value[..compactDate.Index];
            if (ClaudePricing.ContainsKey(baseName))
            {
                return baseName;
            }
        }

        return value;
    }

    private static readonly IReadOnlyDictionary<string, ModelsDevPricingInfo> ClaudePricing = new Dictionary<string, ModelsDevPricingInfo>(StringComparer.OrdinalIgnoreCase)
    {
        // Claude 5 family. Cache read is 0.1x input and cache write 1.25x input (5-minute TTL),
        // matching every other row here. None of these carry a long-context premium: the 1M
        // window bills at the standard rate, so no threshold tier.
        ["claude-fable-5"] = new(10.00m, 50.00m, 1.00m, 12.50m, null, null, null, null, null),
        ["claude-opus-5"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-sonnet-5"] = new(3.00m, 15.00m, 0.30m, 3.75m, null, null, null, null, null),
        ["claude-opus-4-8"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),

        ["claude-haiku-4-5-20251001"] = new(1.00m, 5.00m, 0.10m, 1.25m, null, null, null, null, null),
        ["claude-haiku-4-5"] = new(1.00m, 5.00m, 0.10m, 1.25m, null, null, null, null, null),
        ["claude-opus-4-5-20251101"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-opus-4-5"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-opus-4-6-20260205"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-opus-4-6"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-opus-4-7"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        // The only rows with a long-context tier: 200k, and every component splits against it.
        ["claude-sonnet-4-5"] = new(3.00m, 15.00m, 0.30m, 3.75m, 200_000, 6.00m, 22.50m, 0.60m, 7.50m),
        ["claude-sonnet-4-6"] = new(3.00m, 15.00m, 0.30m, 3.75m, 200_000, 6.00m, 22.50m, 0.60m, 7.50m),
        ["claude-sonnet-4-5-20250929"] = new(3.00m, 15.00m, 0.30m, 3.75m, 200_000, 6.00m, 22.50m, 0.60m, 7.50m),
        ["claude-opus-4-20250514"] = new(15.00m, 75.00m, 1.50m, 18.75m, null, null, null, null, null),
        ["claude-opus-4-1"] = new(15.00m, 75.00m, 1.50m, 18.75m, null, null, null, null, null),
        ["claude-sonnet-4-20250514"] = new(3.00m, 15.00m, 0.30m, 3.75m, 200_000, 6.00m, 22.50m, 0.60m, 7.50m),
    };
}
