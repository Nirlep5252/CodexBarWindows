namespace CodexBarWindows;

/// <summary>
/// Accumulates a scan's rows into a <see cref="UsageLedgerBatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the wiring in the readers is three lines at the row site and cannot get the
/// tier split wrong: the split is a per-PROVIDER rule that has to happen per row, before summing,
/// because cost is a step function of a row's own token counts. Aggregate first and the sum prices
/// differently from the rows that made it.
/// </para>
/// <para>
/// Feed this POST-dedup, post-filter rows — the exact rows that reach the readers' own daily
/// aggregation. Feeding the per-file cache layer instead would count forks and subagent
/// transcripts twice.
/// </para>
/// </remarks>
public sealed class UsageLedgerBatchBuilder
{
    private readonly Dictionary<UsageLedgerKey, UsageLedgerRecord> records = [];
    private readonly HashSet<int> coveredDays = [];

    public UsageLedgerBatchBuilder(int accountingVersion)
    {
        AccountingVersion = accountingVersion;
    }

    public int AccountingVersion { get; }

    /// <summary>
    /// False as soon as anything makes the batch a partial view: a truncated enumeration, a file
    /// that threw. A partial batch merges per-key MAX rather than replacing, so a wrong `true` here
    /// deletes real data while a wrong `false` merely delays convergence.
    /// </summary>
    public bool IsComplete { get; private set; } = true;

    public int RecordCount => records.Count;

    public void MarkIncomplete() => IsComplete = false;

    /// <summary>Declares that the scan read this UTC day in full, even if it found nothing in it.</summary>
    public void CoverDay(DateTimeOffset instant) => coveredDays.Add(UsageLedger.ToUtcDay(instant));

    public void CoverDays(DateTimeOffset fromInclusive, DateTimeOffset toInclusive)
    {
        for (var day = UsageLedger.ToUtcDay(fromInclusive); day <= UsageLedger.ToUtcDay(toInclusive); day++)
        {
            coveredDays.Add(day);
        }
    }

    /// <summary>
    /// Adds a Codex row. The WHOLE row moves into the long-context tier when its input exceeds the
    /// threshold, because that is exactly what CodexUsageInsightsReader does: it flips one
    /// usesLongContextRates flag and then prices every component at the above-threshold rate.
    /// </summary>
    /// <param name="input">Total input tokens, INCLUDING cached input, as TokenTotals reports them.</param>
    public void AddCodexRow(
        DateTimeOffset timestampUtc,
        string model,
        long input,
        long cachedInput,
        long output,
        bool isFast,
        int? thresholdTokens,
        bool vendorPriced = false)
    {
        var threshold = thresholdTokens is { } value && value > 0 ? value : 0;
        var tokens = new UsageLedgerTokens(Math.Max(0, input), Math.Max(0, cachedInput), 0, Math.Max(0, output));
        var longContext = threshold > 0 && tokens.Input > threshold;

        var flags = UsageLedgerFlags.None;
        if (isFast)
        {
            flags |= UsageLedgerFlags.Fast;
        }

        // The shared ceiling, never a local copy: this bit is RECORDED into the key and the read path
        // prices from it, so a second literal here would let a stale copy write records that
        // CodexModelPricing.TierFor then interprets under a different rule.
        if (tokens.Input > CodexModelPricing.PriorityInputTokenLimit)
        {
            flags |= UsageLedgerFlags.OverPriorityInputLimit;
        }

        if (vendorPriced)
        {
            flags |= UsageLedgerFlags.VendorPriced;
        }

        Add(
            timestampUtc,
            model,
            flags,
            threshold,
            longContext ? default : tokens,
            longContext ? tokens : default);
    }

    /// <summary>
    /// Adds a Claude row. Each component splits INDEPENDENTLY at the cutoff, because
    /// ClaudeUsageInsightsReader.Tiered() is applied per component: only the tokens above the
    /// cutoff within one request bill at the higher rate.
    /// </summary>
    /// <param name="rawInput">Input EXCLUDING cached input — Tiered() is called with rawInput there, not with Input.</param>
    public void AddClaudeRow(
        DateTimeOffset timestampUtc,
        string model,
        long rawInput,
        long cachedInput,
        long cacheCreation,
        long output,
        int? thresholdTokens)
    {
        var threshold = thresholdTokens is { } value && value > 0 ? value : 0;
        var standard = new UsageLedgerTokens(
            Below(rawInput, threshold),
            Below(cachedInput, threshold),
            Below(cacheCreation, threshold),
            Below(output, threshold));
        var longContext = new UsageLedgerTokens(
            Above(rawInput, threshold),
            Above(cachedInput, threshold),
            Above(cacheCreation, threshold),
            Above(output, threshold));

        Add(timestampUtc, model, UsageLedgerFlags.None, threshold, standard, longContext);
    }

    private static long Below(long component, int threshold)
        => threshold > 0 ? Math.Min(Math.Max(0, component), threshold) : Math.Max(0, component);

    private static long Above(long component, int threshold)
        => threshold > 0 ? Math.Max(Math.Max(0, component) - threshold, 0) : 0;

    public void Add(
        DateTimeOffset timestampUtc,
        string model,
        UsageLedgerFlags flags,
        int thresholdTokens,
        UsageLedgerTokens standard,
        UsageLedgerTokens longContext,
        int requests = 1)
    {
        var key = new UsageLedgerKey(UsageLedger.ToUtcHour(timestampUtc), model ?? string.Empty, flags, Math.Max(0, thresholdTokens));
        records[key] = records.TryGetValue(key, out var existing)
            ? existing with
            {
                Standard = existing.Standard + standard,
                LongContext = existing.LongContext + longContext,
                Requests = existing.Requests + requests
            }
            : new UsageLedgerRecord(key, standard, longContext, requests);
    }

    /// <summary>Earliest UTC hour any record landed in, or null while the builder is empty.</summary>
    /// <remarks>
    /// The backfill uses this to declare coverage from the first row on disk to now. Computed on
    /// demand rather than tracked per Add: it is read once, at the end of a run.
    /// </remarks>
    public int? EarliestUtcHour
    {
        get
        {
            var earliest = int.MaxValue;
            foreach (var key in records.Keys)
            {
                if (key.UtcHour < earliest)
                {
                    earliest = key.UtcHour;
                }
            }

            return earliest == int.MaxValue ? null : earliest;
        }
    }

    /// <summary>
    /// Folds another builder's records into this one, summing per key.
    /// </summary>
    /// <remarks>
    /// Summing is correct here for the same reason <c>Add</c> sums: both builders hold rows the
    /// scan actually read, and a row is never handed to two builders. This exists so the backfill's
    /// workers can each own a private builder and only synchronise once per worker, instead of
    /// taking a lock for every one of the millions of rows in the corpus.
    /// <para/>
    /// Incompleteness is contagious: a partial contribution makes the whole batch partial, because
    /// the merged batch is a lower bound on every day the partial worker touched.
    /// </remarks>
    public void AddFrom(UsageLedgerBatchBuilder other)
    {
        if (other is null || ReferenceEquals(other, this))
        {
            return;
        }

        if (!other.IsComplete)
        {
            IsComplete = false;
        }

        foreach (var day in other.coveredDays)
        {
            coveredDays.Add(day);
        }

        foreach (var (key, record) in other.records)
        {
            records[key] = records.TryGetValue(key, out var existing)
                ? existing with
                {
                    Standard = existing.Standard + record.Standard,
                    LongContext = existing.LongContext + record.LongContext,
                    Requests = existing.Requests + record.Requests
                }
                : record;
        }
    }

    public UsageLedgerBatch Build(DateTimeOffset? scannedAt = null)
        => new(
            records.Values.ToArray(),
            coveredDays,
            IsComplete,
            AccountingVersion,
            scannedAt ?? DateTimeOffset.Now);
}
