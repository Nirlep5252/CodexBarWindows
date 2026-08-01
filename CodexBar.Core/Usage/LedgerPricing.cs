namespace CodexBarWindows;

/// <summary>
/// Turns ledger TOKENS into money at read time.
/// </summary>
/// <remarks>
/// <para>
/// The ledger stores tokens and refuses to store cost, so every reader has to bring its own pricing.
/// This one holds no rate literals of its own: Codex resolves through <see cref="CodexModelPricing"/>
/// and Claude through <see cref="ClaudeModelPricing"/> - the SAME two tables the 30-day scans price
/// with, each of which consults the shared models.dev catalog first and its built-in table second. A
/// catalog or table correction therefore re-prices every month ever recorded, which is the whole
/// point of the ledger (property #2) and dies the instant a second rate table exists in this codebase.
/// </para>
/// <para>
/// This lives in Core rather than beside the graphs window on purpose: it is the read half of the
/// ledger's accounting and it has to be testable against the scan's own figures, which a WinUI type
/// cannot be.
/// </para>
/// <para>
/// A model NEITHER source can price returns null, which the query surfaces as
/// <c>HasIncompleteCost</c> and contributes $0.00 - never a cheap row, and never a suppressed one.
/// Reaching only the catalog (as the Claude path once did) produced the opposite and far worse
/// failure: a real, priceable model silently valued at zero with nothing marked incomplete.
/// </para>
/// </remarks>
internal static class LedgerPricing
{
    public static UsageLedgerPricing For(UsageLedgerScope scope) => scope switch
    {
        UsageLedgerScope.Codex => new UsageLedgerPricing(
            CostUsd: CodexCost,
            ThresholdTokens: CodexModelPricing.ThresholdTokensFor,
            // The scan groups its breakdown on the NORMALIZED id; without this the ledger grouped
            // on the raw logged id, so "openai/gpt-5.6" and "gpt-5.6-sol" were two rows, two
            // colour overrides and two legend entries for one model, depending on which source
            // answered the query.
            ModelLabel: CodexLabel),
        UsageLedgerScope.Claude => new UsageLedgerPricing(
            CostUsd: ClaudeCost,
            ThresholdTokens: ClaudeModelPricing.ThresholdTokensFor,
            ModelLabel: ClaudeLabel),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown ledger scope.")
    };

    // Both labels reproduce their reader's breakdown label exactly, including the empty-model
    // fallback and the " fast" suffix, because the graphs window keys per-model colours off it.
    private static string CodexLabel(string model, bool isFast) => CodexModelPricing.BreakdownLabel(model, isFast);

    // Claude has no fast tier at all, so the flag is never set on a Claude record and the suffix
    // would be meaningless; the scan labels these rows with the normalized id and nothing else.
    private static string ClaudeLabel(string model, bool isFast) => ClaudeModelPricing.NormalizeModelName(model);

    /// <summary>
    /// Codex cost for one ledger record, or null when the model cannot be priced at all.
    /// </summary>
    /// <remarks>
    /// The tier decision is made by <see cref="CodexModelPricing.TierFor"/> so it cannot disagree
    /// with the scan. Two independent bits feed it, both recorded at scan time and neither derivable
    /// from the other: <see cref="UsageLedgerFlags.Fast"/> (the resolved priority observation) and
    /// <see cref="UsageLedgerFlags.OverPriorityInputLimit"/> (input past the priority ceiling, which
    /// disqualifies priority even for a model that has no long-context threshold - gpt-5.4-mini).
    /// <para/>
    /// A priority row is priced from <see cref="UsageLedgerRecord.Combined"/> rather than per tier
    /// because priority SUPPRESSES long-context rates in the reader, and it can only ever land in
    /// the standard tier anyway: the priority ceiling sits at or below every published threshold.
    /// </remarks>
    private static decimal? CodexCost(UsageLedgerRecord record)
    {
        if (CodexModelPricing.For(record.Key.Model) is not { } rates)
        {
            return null;
        }

        var flags = record.Key.Flags;

        // The ceiling test is the RECORDED flag, not a comparison against these tokens: a record is
        // the sum of every row that shared an hour, a model and a flag set, so its input total says
        // nothing about whether any single row was over the limit. The flag already answered that
        // per row at scan time, which is why it is part of the key.
        if (flags.HasFlag(UsageLedgerFlags.Fast) &&
            !flags.HasFlag(UsageLedgerFlags.OverPriorityInputLimit) &&
            rates.HasPriorityRates)
        {
            var combined = record.Combined;
            return CodexModelPricing.Estimate(rates, combined.Input, combined.CachedInput, combined.Output, CodexRateTier.Priority);
        }

        return CodexTier(rates, record.Standard, CodexRateTier.Base) +
               CodexTier(rates, record.LongContext, CodexRateTier.LongContext);
    }

    private static decimal CodexTier(CodexModelRates rates, UsageLedgerTokens tokens, CodexRateTier tier)
        => tokens.IsEmpty
            ? 0m
            : CodexModelPricing.Estimate(rates, tokens.Input, tokens.CachedInput, tokens.Output, tier);

    /// <summary>
    /// Claude cost for one ledger record, or null when neither pricing source knows the model.
    /// </summary>
    /// <remarks>
    /// <see cref="ClaudeModelPricing.For"/> is the same catalog-then-built-in resolution the scan
    /// uses. Reaching only the catalog priced every built-in-only model - which is most of them, and
    /// the larger share of this user's spend - at exactly $0.00 while returning a non-null cost, so
    /// nothing was ever flagged incomplete.
    /// </remarks>
    private static decimal? ClaudeCost(UsageLedgerRecord record)
    {
        // Mirrors the scan's guard: a synthetic pseudo-model is worth zero, which is an ANSWER and
        // must not be reported as an unpriceable gap.
        if (ClaudeModelPricing.IsNonBillableModel(record.Key.Model))
        {
            return 0m;
        }

        if (ClaudeModelPricing.For(record.Key.Model) is not { } pricing)
        {
            return null;
        }

        return ClaudeTier(pricing, record.Standard, above: false) +
               ClaudeTier(pricing, record.LongContext, above: true);
    }

    /// <summary>
    /// Prices one tier. The tiers were split at SCAN time by each provider's own rule, which is what
    /// makes summing rows and pricing the sum equal to pricing each row and summing.
    /// </summary>
    /// <remarks>
    /// Claude splits EACH COMPONENT independently against the cutoff (unlike Codex, which reprices
    /// the whole row), so a record's Standard bucket can be non-empty at the same time as its
    /// LongContext bucket. The rate columns and the divide-then-multiply order come from
    /// <see cref="ClaudeModelPricing.EstimateTier"/> so they cannot disagree with the scan.
    /// <para/>
    /// Claude stored RAW input (its own tiering is applied to raw input), so nothing is netted off
    /// here. Normalising this against the Codex convention would double-bill every cached token on
    /// one provider or the other.
    /// </remarks>
    private static decimal ClaudeTier(ModelsDevPricingInfo pricing, UsageLedgerTokens tokens, bool above)
        => tokens.IsEmpty
            ? 0m
            : ClaudeModelPricing.EstimateTier(pricing, tokens.Input, tokens.CachedInput, tokens.CacheCreation, tokens.Output, above);

    // NO GROK COST FUNCTION HERE. Grok has no ledger scope (see UsageLedgerScope), so a Grok
    // pricing arm could only ever be called with records that cannot exist. The rates it used to
    // carry were a second, already-drifted copy of the scan's table - the scan's
    // GrokUsageInsightsReader.GrokPricing is now the only one.
}
