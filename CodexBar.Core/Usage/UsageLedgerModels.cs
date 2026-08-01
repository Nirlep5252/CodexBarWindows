namespace CodexBarWindows;

/// <summary>
/// Which session corpus a ledger shard belongs to.
/// </summary>
/// <remarks>
/// Deliberately the SCAN SCOPE, not <see cref="ProviderKeys"/>. ProviderKeys is per CLI entry in the
/// UI, but there is exactly one ~/.codex corpus and one ~/.claude corpus behind them, so keying the
/// ledger per UI entry would fan the same rows into N files and double count on read.
/// </remarks>
/// <remarks>
/// A PROVIDER IS NOT AUTOMATICALLY A SCOPE. Grok deliberately has no member here: nothing merges
/// Grok rows, so a Grok scope would be a permanently empty ledger that every read path still had to
/// treat as authoritative. It is scan-only instead, which the graphs window models as a null scope.
/// Adding a member without also adding a TryMerge caller and an IUsageLedgerBackfillSource
/// reintroduces exactly that: a code path with no data and no test behind it.
/// </remarks>
public enum UsageLedgerScope
{
    Codex,
    Claude
}

public enum UsageLedgerGranularity
{
    Hour,
    Day,
    Week,
    Month,
    Year,

    /// <summary>The whole requested range collapsed into a single bucket ("total ever" when the range is the coverage).</summary>
    All
}

/// <summary>
/// Raw facts about a row that change how it prices, stored so the RATES stay out of the file.
/// </summary>
/// <remarks>
/// <para>
/// Every bit here is an observation made at scan time, never a pricing decision. Whether priority
/// rates actually apply is resolved at read time from the live pricing table, so adding priority
/// rates to a model retroactively re-prices history — the ledger's whole reason to exist.
/// </para>
/// <para>
/// <see cref="VendorPriced"/> marks a row whose real cost was supplied by the vendor (pi rows carry
/// an exact dollar figure) and therefore cannot be reproduced from tokens. The dollar figure is
/// NOT stored — this file holds tokens only — so a reader must treat these rows as tokens with an
/// underivable cost and report incomplete cost rather than inventing one.
/// </para>
/// </remarks>
[Flags]
public enum UsageLedgerFlags
{
    None = 0,

    /// <summary>Resolved AFTER the replay-time fast-turn lookup, not the raw per-row hint.</summary>
    Fast = 1,

    /// <summary>Row input exceeded the priority input ceiling, which disqualifies priority rates independently of the long-context threshold.</summary>
    OverPriorityInputLimit = 2,

    /// <summary>Cost came from the vendor as money and cannot be re-derived from these tokens.</summary>
    VendorPriced = 4
}

/// <summary>
/// One tier's token components.
/// </summary>
/// <remarks>
/// <see cref="Input"/> INCLUDES <see cref="CachedInput"/> for Codex, exactly as TokenTotals does in
/// the reader; billable raw input is Max(0, Input - CachedInput). Claude stores raw input directly
/// because <c>Tiered()</c> is called with rawInput there. Do not "normalise" this away — the split
/// is what lets each provider's own arithmetic be reproduced verbatim.
/// </remarks>
public readonly record struct UsageLedgerTokens(long Input, long CachedInput, long CacheCreation, long Output)
{
    public static UsageLedgerTokens operator +(UsageLedgerTokens a, UsageLedgerTokens b)
        => new(a.Input + b.Input, a.CachedInput + b.CachedInput, a.CacheCreation + b.CacheCreation, a.Output + b.Output);

    /// <summary>Componentwise max — the monotone merge used when a batch could not assert full coverage.</summary>
    public static UsageLedgerTokens Max(UsageLedgerTokens a, UsageLedgerTokens b)
        => new(
            Math.Max(a.Input, b.Input),
            Math.Max(a.CachedInput, b.CachedInput),
            Math.Max(a.CacheCreation, b.CacheCreation),
            Math.Max(a.Output, b.Output));

    /// <summary>Matches ProviderDailyUsage.TotalTokens: cached input is inside Input and is not counted twice.</summary>
    public long Total => Input + CacheCreation + Output;

    public bool IsEmpty => Input == 0 && CachedInput == 0 && CacheCreation == 0 && Output == 0;
}

/// <summary>
/// Identity of a ledger record. <paramref name="UtcHour"/> is whole hours since the Unix epoch.
/// </summary>
/// <remarks>
/// The bucket is a UTC instant, never a local day. Both readers derive their day by regexing
/// yyyy-MM-dd out of the raw ISO timestamp (UTC, with a Z) while their "today" comes from
/// DateTimeOffset.Now (local) — an inconsistency that only shows up near midnight. Storing the true
/// instant makes local bucketing a pure read-side concern and makes a time zone change free.
/// <para/>
/// <paramref name="ThresholdTokens"/> is the long-context cutoff that was actually applied when the
/// tokens were split, recorded per record so a later pricing-table change is DETECTABLE rather than
/// silently miscomputed.
/// </remarks>
/// <param name="UtcHour">Whole hours since the Unix epoch, UTC.</param>
/// <param name="Model">The RAW model id as logged. Normalisation is a read-side display concern.</param>
/// <param name="Flags">Scan-time observations, never pricing decisions.</param>
/// <param name="ThresholdTokens">The long-context cutoff applied to this row, or 0 when the model had none.</param>
public readonly record struct UsageLedgerKey(int UtcHour, string Model, UsageLedgerFlags Flags, int ThresholdTokens);

/// <summary>
/// Tokens for one key, split into the two pricing tiers at scan time by each provider's own rule.
/// </summary>
/// <remarks>
/// Cost is a per-ROW step function of tokens, so summing rows first and pricing the sum gives the
/// wrong answer. Splitting into Standard/LongContext at scan time makes the sum linear again:
/// read-time cost is Standard*baseRate + LongContext*aboveRate, which reproduces both providers
/// exactly while keeping every rate out of the file.
/// </remarks>
public sealed record UsageLedgerRecord(
    UsageLedgerKey Key,
    UsageLedgerTokens Standard,
    UsageLedgerTokens LongContext,
    int Requests)
{
    public UsageLedgerTokens Combined => Standard + LongContext;

    public bool IsEmpty => Standard.IsEmpty && LongContext.IsEmpty && Requests == 0;
}

/// <summary>
/// One scan's worth of records plus the days it is asserting authority over.
/// </summary>
/// <param name="CoveredUtcDays">
/// UTC day numbers (days since the Unix epoch) the scan claims to have read completely.
/// </param>
/// <param name="IsComplete">
/// True only when the enumeration was not truncated AND no file read threw. When true the merge
/// DELETES every existing record for the covered days and replaces them; when false it merges
/// per-key MAX instead, which is monotone and idempotent because session files only ever grow.
/// Getting this backwards deletes real data, so the default must be false.
/// </param>
public sealed record UsageLedgerBatch(
    IReadOnlyList<UsageLedgerRecord> Records,
    IReadOnlySet<int> CoveredUtcDays,
    bool IsComplete,
    int AccountingVersion,
    DateTimeOffset ScannedAt);

/// <summary>
/// Cost derivation supplied by the caller. The ledger stores tokens and never prices anything.
/// </summary>
/// <remarks>
/// Cost lives entirely in the two readers' pricing tables today. Passing it in as delegates keeps
/// the ledger from growing a second, forkable copy of those tables — the moment two pricing tables
/// exist, a rate fix stops applying retroactively and the ledger's core promise dies. All three
/// delegates are optional: with none supplied a query returns tokens and zero cost.
/// </remarks>
/// <param name="CostUsd">Returns null when the model cannot be priced, which surfaces as HasIncompleteCost.</param>
/// <param name="ThresholdTokens">Current long-context cutoff for a model, compared against the recorded one to detect drift.</param>
/// <param name="ModelLabel">(rawModelId, isFast) => display label. Defaults to the reader convention of a " fast" suffix.</param>
public sealed record UsageLedgerPricing(
    Func<UsageLedgerRecord, decimal?>? CostUsd = null,
    Func<string, int?>? ThresholdTokens = null,
    Func<string, bool, string>? ModelLabel = null);

public sealed record UsageLedgerBucket(
    DateTimeOffset StartLocal,
    DateTimeOffset EndLocalExclusive,
    long InputTokens,
    long CachedInputTokens,
    long CacheCreationTokens,
    long OutputTokens,
    decimal EstimatedCostUsd,
    decimal FastEstimatedCostUsd,
    int Requests,
    IReadOnlyList<ProviderModelUsage> Models,
    IReadOnlyList<ProviderSpendCategory> Categories,
    bool HasIncompleteCost)
{
    public long TotalTokens => InputTokens + CacheCreationTokens + OutputTokens;

    public decimal RegularEstimatedCostUsd => Math.Max(0, EstimatedCostUsd - FastEstimatedCostUsd);
}

/// <param name="Buckets">Dense: empty buckets are materialised, exactly as ProviderDailyUsage is today.</param>
/// <param name="HasPartialDays">A day in range was written by a truncated scan, so its totals are a lower bound.</param>
/// <param name="ThresholdMismatch">A record's recorded cutoff differs from the live pricing table — offer a re-import.</param>
/// <param name="HasTruncatedDays">A day hit the per-day record ceiling and lost its smallest keys.</param>
public sealed record UsageLedgerSeries(
    UsageLedgerGranularity Granularity,
    IReadOnlyList<UsageLedgerBucket> Buckets,
    IReadOnlyList<ProviderModelUsage> Models,
    long InputTokens,
    long CachedInputTokens,
    long CacheCreationTokens,
    long OutputTokens,
    decimal TotalEstimatedCostUsd,
    decimal TotalFastEstimatedCostUsd,
    int Requests,
    bool HasIncompleteCost,
    bool HasPartialDays,
    bool HasTruncatedDays,
    bool ThresholdMismatch)
{
    public long TotalTokens => InputTokens + CacheCreationTokens + OutputTokens;

    public decimal TotalRegularEstimatedCostUsd => Math.Max(0, TotalEstimatedCostUsd - TotalFastEstimatedCostUsd);

    /// <summary>
    /// True when this range's money is an ACCOUNTING of it rather than a coincidence: some cost was
    /// derived, or some model's tokens were priceable and simply cost nothing.
    /// </summary>
    /// <remarks>
    /// This exists to keep "the ledger cannot price this" apart from "the ledger says this was
    /// free". A caller that treats a model it cannot price as $0 of real spend under-reports
    /// silently; a caller that treats free usage (gpt-5.3-codex-spark bills at 0.00) as unpriceable
    /// throws away a correct answer. TOKENS are never involved in the distinction — an unpriceable
    /// model's tokens are as real as any other's and still count everywhere tokens are shown — so
    /// this must not be read as "has data". <see cref="TotalTokens"/> answers that.
    /// </remarks>
    public bool HasPriceableData => TotalEstimatedCostUsd > 0 ||
        Models.Any(model => !model.HasIncompleteCost && (model.TotalTokens > 0 || model.EstimatedCostUsd > 0));

    public static UsageLedgerSeries Empty(UsageLedgerGranularity granularity)
        => new(granularity, [], [], 0, 0, 0, 0, 0m, 0m, 0, false, false, false, false);
}

/// <summary>
/// What the ledger actually holds for one scope. Drives the timeline strip's back arrow.
/// </summary>
/// <param name="FirstRecordedDay">Earliest day a scan claimed, including days it found empty.</param>
/// <param name="FirstUsageUtc">Earliest instant with actual tokens — the real start of history.</param>
public sealed record UsageLedgerCoverage(
    DateOnly? FirstRecordedDay,
    DateOnly? LastRecordedDay,
    DateTimeOffset? FirstUsageUtc,
    DateTimeOffset? LastUsageUtc,
    int RecordedDayCount,
    int RecordCount,
    long TotalBytes,
    int AccountingVersion,
    bool HasPartialDays,
    bool HasUnreadableShards)
{
    public static readonly UsageLedgerCoverage None = new(null, null, null, null, 0, 0, 0, 0, false, false);

    public bool HasHistory => RecordCount > 0;
}
