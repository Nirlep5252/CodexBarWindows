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
    public void CoverDay(DateTimeOffset instant)
    {
        var day = UsageLedger.ToUtcDay(instant);
        if (UsageLedger.IsRecordableUtcDay(day))
        {
            coveredDays.Add(day);
        }
    }

    /// <summary>
    /// Declares a whole inclusive range of UTC days read in full.
    /// </summary>
    /// <remarks>
    /// CLAMPED, never trusted. The backfill derives <paramref name="fromInclusive"/> from the
    /// earliest row it saw anywhere on disk, so a single corrupt timestamp would otherwise ask for
    /// ~739,000 covered days spread over ~2,000 year shards — each one a read-modify-write under a
    /// cross-process mutex, which is the difference between a merge and a hang.
    /// </remarks>
    public void CoverDays(DateTimeOffset fromInclusive, DateTimeOffset toInclusive)
    {
        var first = Math.Max(UsageLedger.ToUtcDay(fromInclusive), UsageLedger.EarliestRecordableUtcDay);
        var last = Math.Min(UsageLedger.ToUtcDay(toInclusive), UsageLedger.LatestRecordableUtcDay);
        for (var day = first; day <= last; day++)
        {
            coveredDays.Add(day);
        }
    }

    /// <summary>
    /// Declares the UTC days a scan that filtered its rows by WALL-CLOCK DATE genuinely read in
    /// full, from the first day of its REPORT window and the moment it ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both readers derive their window from LOCAL "today" and then keep a row when the calendar
    /// date the row's own log SPELLS is at or after <paramref name="firstReportDay"/>. Records,
    /// however, are keyed by the true UTC instant. One local day straddles two UTC days, so the
    /// naive translation — "the report days ARE the covered UTC days" — claims a UTC day the scan
    /// only clipped, and a complete batch is licensed to DELETE a claimed day.
    /// </para>
    /// <para>
    /// A session line is written in one of two frames on this machine: UTC (both CLIs stamp a Z) or
    /// LOCAL (a stamp carrying no zone, or a numeric epoch, is read in the local frame). A row
    /// written in frame <c>o</c> at UTC instant <c>t</c> survives the filter iff
    /// <c>date(t + o) &gt;= firstReportDay</c>, i.e. iff <c>t &gt;= firstReportDay - o</c>. The scan
    /// can therefore only vouch for instants at or after <c>firstReportDay - min(o)</c>, and a UTC
    /// day may be claimed only when the WHOLE of it sits inside that:
    /// </para>
    /// <list type="bullet">
    /// <item>A non-negative local offset (min is UTC's own 0) leaves the first report day exactly on
    /// the boundary — it is claimable, and the loss is on the day BEFORE it, which shows up as
    /// records rather than as a declaration and is handled by the merge's authority rule.</item>
    /// <item>A NEGATIVE local offset pushes the guaranteed start into the first report day itself
    /// (at -08:00 the scan has read it only from 08:00 UTC), so that day is not claimable and this
    /// shrinks the range by one.</item>
    /// </list>
    /// <para>
    /// There is no upper filter, so the top of the range is bounded only by the clock: every instant
    /// up to <paramref name="scannedAt"/> was read in every frame. Deliberately NOT bounded by the
    /// last report day, which is a LOCAL date and would either claim a UTC day that has not started
    /// or refuse the one holding today's usage, depending on the sign of the offset.
    /// </para>
    /// </remarks>
    public void CoverReportWindow(DateOnly firstReportDay, DateTimeOffset scannedAt)
        => CoverReportWindow(firstReportDay, scannedAt, TimeZoneInfo.Local);

    /// <param name="zone">The frame a zone-less log line is read in. Injected only so the boundary arithmetic is testable.</param>
    /// <inheritdoc cref="CoverReportWindow(DateOnly, DateTimeOffset)"/>
    internal void CoverReportWindow(DateOnly firstReportDay, DateTimeOffset scannedAt, TimeZoneInfo zone)
    {
        var windowStart = new DateTimeOffset(firstReportDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // DST makes the local offset a function of the instant, so both ends of the window are
        // consulted and the smallest wins — a window that spans a fall-back must be judged by the
        // frame that admits the LEAST, not by whichever end happened to be sampled.
        var earliest = TimeSpan.Zero;
        foreach (var candidate in new[] { SafeOffset(zone, windowStart), SafeOffset(zone, scannedAt) })
        {
            if (candidate < earliest)
            {
                earliest = candidate;
            }
        }

        var guaranteedFrom = windowStart - earliest;
        var day = UsageLedger.UtcDayOfHour(UsageLedger.ToUtcHour(guaranteedFrom));

        // Rounded UP: a UTC day the guarantee starts partway into was only half read.
        var first = Math.Max(
            guaranteedFrom > UsageLedger.FromUtcHour(day * 24) ? day + 1 : day,
            UsageLedger.EarliestRecordableUtcDay);
        var last = Math.Min(UsageLedger.ToUtcDay(scannedAt), UsageLedger.LatestRecordableUtcDay);

        for (var utcDay = first; utcDay <= last; utcDay++)
        {
            coveredDays.Add(utcDay);
        }
    }

    /// <summary>A wall clock inside a DST gap has no offset and the framework throws; a scan is not worth that.</summary>
    private static TimeSpan SafeOffset(TimeZoneInfo zone, DateTimeOffset instant)
    {
        try
        {
            return zone.GetUtcOffset(instant);
        }
        catch
        {
            return zone.BaseUtcOffset;
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
        // Dropped at the door. A row whose timestamp cannot be real is not usage the user had, and
        // letting it in costs far more than the row is worth: its day names a year shard, so one
        // year-0001 record adds a whole extra file to every merge for the life of the ledger.
        if (!UsageLedger.IsRecordableInstant(timestampUtc))
        {
            return;
        }

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
