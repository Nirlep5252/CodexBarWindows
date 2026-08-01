using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

/// <summary>
/// One year shard exactly as it sits on disk. Property names are one or two characters on purpose:
/// this file is written once per graphs-window open and read on every open, and the field names are
/// the dominant cost in a naive JSON encoding (~180 B/record named, ~55 B/record like this).
/// </summary>
internal sealed class UsageLedgerShard
{
    /// <summary>Record LAYOUT version. Mirrors the filename so a downgrade cannot read a newer file.</summary>
    [JsonPropertyName("v")] public int V { get; set; }

    /// <summary>Scanner SEMANTICS version. Readable but suspect on mismatch — never a reason to discard.</summary>
    [JsonPropertyName("a")] public int A { get; set; }

    /// <summary>Provenance only. Unlike UsageScanCache this MUST NOT gate loading: an app update
    /// that silently erased a 1-3 minute manual import would be indistinguishable from data loss.</summary>
    [JsonPropertyName("app")] public string? App { get; set; }

    /// <summary>Provenance only. Records are UTC, so the zone is a read-side concern.</summary>
    [JsonPropertyName("tz")] public string? Tz { get; set; }

    /// <summary>Interned raw model ids; records carry an index into this table.</summary>
    [JsonPropertyName("m")] public List<string> M { get; set; } = [];

    /// <summary>Keyed by UTC day number (days since the Unix epoch) as an invariant string.</summary>
    [JsonPropertyName("d")] public Dictionary<string, UsageLedgerShardDay> D { get; set; } = [];
}

internal sealed class UsageLedgerShardDay
{
    /// <summary>Written by a scan that could not assert full coverage; totals are a lower bound.</summary>
    [JsonPropertyName("p")] public bool P { get; set; }

    /// <summary>The day hit the per-day record ceiling and lost its smallest keys.</summary>
    [JsonPropertyName("t")] public bool T { get; set; }

    /// <summary>UTC ticks of the scan that last wrote this day.</summary>
    [JsonPropertyName("s")] public long S { get; set; }

    /// <summary>
    /// Fixed-order rows: [hourOfDay, modelIndex, flags, thresholdTokens,
    /// sIn, sCached, sCacheCreate, sOut, lIn, lCached, lCacheCreate, lOut, requests].
    /// There is no cost column and there never will be — see UsageLedger's remarks.
    /// </summary>
    [JsonPropertyName("r")] public List<long[]> R { get; set; } = [];
}

/// <summary>
/// A durable, append/merge store of TOKENS ONLY, keyed (scope, UTC hour, model, flags, threshold).
/// </summary>
/// <remarks>
/// <para>
/// COST IS NEVER STORED. It is derived at read time by the caller's <see cref="UsageLedgerPricing"/>
/// delegates from the existing pricing tables, so a rate correction retroactively fixes every month
/// ever recorded. This is the entire reason the ledger exists; a "cost" column would freeze history
/// at whatever the table said the day it was scanned.
/// </para>
/// <para>
/// This is DATA, not a cache, and that inverts three UsageScanCache policies: an AppVersion or
/// TimeZoneId mismatch must not discard anything (both are provenance strings), and a merge is a
/// read-modify-write of the affected year shards under a cross-process mutex rather than a whole
/// state overwrite. Everything else follows the cache verbatim — schema version in the filename and
/// the payload, temp file + Flush(flushToDisk) + one File.Move(overwrite), and every failure
/// swallowed so a corrupt file degrades to "no history" instead of throwing on a read path.
/// </para>
/// <para>
/// Zero idle cost is preserved: nothing here is wired to a timer or a startup path. Merge is called
/// only from inside the history scan that the graphs window already triggers, and the backfill only
/// from an explicit button.
/// </para>
/// </remarks>
public static partial class UsageLedger
{
    /// <summary>Record layout. A mismatch means the shard cannot be read; it is ignored, never deleted.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>Columns in one on-disk record row. A row of any other length is discarded.</summary>
    private const int RecordWidth = 13;

    // ---- Size ceiling -------------------------------------------------------------------------
    // The bound has to be structural, not "we expect it to be small": a pathological account (a
    // script hammering many models per hour) must not be able to grow this without limit.
    //
    //   <= MaxRecordsPerDay records/day  x  366 days/shard  =  187,392 records/shard
    //   x ~55 B/record                                      =  ~10 MB/shard
    //   x 2 scopes                                          =  ~21 MB/year, hard-capped below.
    //
    // Realistic load is ~5 (model,flags) combos x 24 h = ~120 records/day, so the per-day cap sits
    // at roughly 4x the worst plausible day and only ever bites a runaway.

    /// <summary>Per UTC day. On overflow the smallest-token keys are dropped and the day is flagged truncated.</summary>
    internal const int MaxRecordsPerDay = 512;

    /// <summary>Distinct model ids interned per shard; further ids collapse into <see cref="OverflowModelId"/>.</summary>
    internal const int MaxModelsPerShard = 512;

    /// <summary>Backstop on the encoded shard. A read refuses a larger file and a write refuses to produce one.</summary>
    internal const int MaxShardBytes = 16 * 1024 * 1024;

    /// <summary>Guards a pathological query (hourly across years) from materialising unbounded dense buckets.</summary>
    internal const int MaxBuckets = 20_000;

    internal const string OverflowModelId = "(other models)";

    private const int MaxModelIdLength = 200;

    // ---- Plausibility bounds ------------------------------------------------------------------
    // A day number is not just a label here, it is FAN-OUT: every distinct year a batch touches is
    // one read-modify-write of a shard under a cross-process mutex, and every covered day is a
    // dictionary entry. One corrupt timestamp (year 0001, or a nanosecond epoch read as seconds)
    // therefore does not record a wrong row, it turns a merge into thousands of shard rebuilds over
    // ~739,000 days and the import stops responding. UsageTimestampText rejects these at parse
    // time; this is the merge's own floor, so a record that reached the ledger by any other route
    // still cannot drive the fan-out.

    /// <summary>Earliest UTC day number the ledger will record. See <see cref="UsageTimestampText.EarliestPlausibleDay"/>.</summary>
    internal static int EarliestRecordableUtcDay { get; } =
        UsageTimestampText.EarliestPlausibleDay.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

    /// <summary>Latest UTC day number the ledger will record, re-evaluated so a long-running app keeps up with the calendar.</summary>
    internal static int LatestRecordableUtcDay => ToUtcDay(DateTimeOffset.UtcNow) + UsageTimestampText.FutureSlackDays;

    internal static bool IsRecordableUtcDay(int utcDay)
        => utcDay >= EarliestRecordableUtcDay && utcDay <= LatestRecordableUtcDay;

    internal static bool IsRecordableInstant(DateTimeOffset instant) => IsRecordableUtcDay(ToUtcDay(instant));

    /// <summary>Single shared instance: System.Text.Json caches reflection metadata per options object.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    private static string? rootOverride;

    /// <summary>
    /// Test seam. Null restores %LOCALAPPDATA%. Mirrors the readers' persistCache gate: a test that
    /// scans a synthetic corpus must never touch the user's real history.
    /// </summary>
    internal static void OverrideRootForTests(string? root)
    {
        rootOverride = root;

        // The cache is keyed (scope, year), not by path, so moving the root would otherwise serve
        // one root's shards under another's key.
        InvalidateShardCache();
    }

    /// <summary>True while a test has redirected the root away from the user's real history.</summary>
    internal static bool IsRootOverridden => rootOverride is not null;

    // ---- Read cache ---------------------------------------------------------------------------
    // Every read path (Query, QueryTotal, GetCoverage) deserializes whole year shards, and the
    // graphs window drives all three on the UI thread for every history update and every period
    // change. With several years of imported history that is tens of MB of JSON re-parsed per
    // interaction. Parsed shards are therefore retained and validated against the file's IDENTITY
    // (length + last write time) rather than a timer, so another process's merge is picked up as
    // soon as it lands and our own merge drops the entry outright.
    //
    // Only READ paths use it. MergeYear loads with useCache:false because it mutates the shard it
    // loads, and a cached instance is shared with every concurrent reader.
    //
    // BOUNDED, AND RELEASED. This process is a tray icon that is supposed to cost nothing when no
    // window is open, so an unbounded cache filled by one graphs-window open and then held for the
    // life of the process is not an accelerator, it is a leak with a nice name. Two limits keep it
    // honest: a ceiling on what may be retained at once (LRU, by BYTES as well as by entry count,
    // because "shards" is not a memory unit), and <see cref="ReleaseReadCache"/> at window teardown,
    // which is the only moment the cache provably has no future reader.

    private sealed record CachedShard(long Length, long LastWriteUtcTicks, long Tick, UsageLedgerShard Shard);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(UsageLedgerScope Scope, int Year), CachedShard> ShardCache = new();

    /// <summary>Monotonic use counter behind the LRU. Ticks, not timestamps: ordering is all that matters.</summary>
    private static long shardCacheTick;

    /// <summary>
    /// Shards retained at once. Sized for the WORKING SET a live window actually has: a period never
    /// spans more than two years, and a coverage read walks them all but does so off the UI thread —
    /// so retaining the newest few is what removes the stutter, and retaining the rest only removes
    /// background work nobody is waiting on.
    /// </summary>
    private const int MaxCachedShards = 6;

    /// <summary>
    /// On-disk bytes of the retained shards. The real bound, because one pathological year may be up
    /// to <see cref="MaxShardBytes"/> on its own and an entry count would say nothing about memory.
    /// </summary>
    private const long MaxCachedShardBytes = 12L * 1024 * 1024;

    /// <summary>Deserializations actually performed. Test seam: what proves the cache is a cache.</summary>
    internal static long ShardParseCount;

    internal static void InvalidateShardCache() => ShardCache.Clear();

    /// <summary>
    /// Drops every parsed shard. Called when the last window that reads the ledger closes.
    /// </summary>
    /// <remarks>
    /// Not merely tidy: with no window open nothing in this process will read a shard again until
    /// the user opens one, and re-parsing at that point is work the user is already waiting through
    /// a scan for. Holding it instead is pure idle cost, which is the property the whole app is
    /// built around. Safe at any moment — the cache is only ever an optimisation, and a merge in
    /// flight does not use it at all (MergeYear loads with useCache:false).
    /// </remarks>
    public static void ReleaseReadCache() => ShardCache.Clear();

    /// <summary>
    /// Parses a scope's shards into the read cache, so a later query finds them warm.
    /// </summary>
    /// <remarks>
    /// Exists purely so a UI caller can pay the disk I/O and the deserialization on a background
    /// thread and leave the render path with dictionary lookups and a stat. Never called from a
    /// timer or a startup path — zero idle cost is untouched.
    /// <para/>
    /// NEWEST FIRST, and that ordering is the whole reason this is not just a loop: the retention
    /// ceiling means warming every year would evict the years a render is about to want in favour of
    /// the oldest ones. Walking down from the newest leaves the working set resident and lets the
    /// tail fall out.
    /// </remarks>
    public static void WarmCache(UsageLedgerScope scope)
    {
        try
        {
            foreach (var year in ShardYears(scope).OrderByDescending(year => year).Take(MaxCachedShards))
            {
                TryLoadShard(scope, year, out _);
            }
        }
        catch
        {
            // Warming is an optimisation; a failure just means the read path parses it itself.
        }
    }

    /// <summary>
    /// Evicts least-recently-used entries until the cache is back inside both ceilings.
    /// </summary>
    /// <remarks>
    /// Approximate on purpose. The dictionary is concurrent and this runs on read paths, so a racing
    /// insert can leave the cache one entry over for an instant; correctness never depends on the
    /// bound, only memory does, and the next read trims it again.
    /// </remarks>
    private static void TrimShardCache()
    {
        while (true)
        {
            var entries = ShardCache.ToArray();
            if (entries.Length <= 1 ||
                (entries.Length <= MaxCachedShards && entries.Sum(entry => entry.Value.Length) <= MaxCachedShardBytes))
            {
                return;
            }

            var oldest = entries[0];
            foreach (var entry in entries)
            {
                if (entry.Value.Tick < oldest.Value.Tick)
                {
                    oldest = entry;
                }
            }

            if (!ShardCache.TryRemove(oldest.Key, out _))
            {
                // Someone else evicted it first; the next pass re-reads the whole set anyway.
                return;
            }
        }
    }

    internal static string RootDirectory => rootOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexBarWindows",
            "usage-ledger");

    internal static string ShardPath(UsageLedgerScope scope, int year)
        => Path.Combine(RootDirectory, $"{ScopeName(scope)}-{year}-v{SchemaVersion}.json");

    // A switch and not a two-way ternary: the ternary mapped every non-Codex scope onto "claude",
    // so a scope added to the enum silently READ AND WROTE CLAUDE'S SHARDS rather than failing.
    private static string ScopeName(UsageLedgerScope scope) => scope switch
    {
        UsageLedgerScope.Codex => "codex",
        UsageLedgerScope.Claude => "claude",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown ledger scope.")
    };

    // ---- Instant helpers ----------------------------------------------------------------------

    /// <summary>
    /// Whole hours since the Unix epoch, SATURATING rather than wrapping.
    /// </summary>
    /// <remarks>
    /// DateTimeOffset spans year 1 to year 9999; an int of hours does not. A plain cast turns
    /// DateTimeOffset.MinValue into a positive hour somewhere in the future, which is the worst
    /// possible failure — a corrupt row that looks legitimate. Clamping keeps the function total and
    /// keeps every out-of-range instant on the far side of <see cref="IsRecordableUtcDay"/>.
    /// </remarks>
    public static int ToUtcHour(DateTimeOffset instant)
        => (int)Math.Clamp(
            (instant.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerHour,
            int.MinValue,
            int.MaxValue);

    public static DateTimeOffset FromUtcHour(int utcHour)
        => new(DateTime.UnixEpoch.AddHours(utcHour), TimeSpan.Zero);

    public static int UtcDayOfHour(int utcHour) => (int)Math.Floor(utcHour / 24.0);

    public static int ToUtcDay(DateTimeOffset instant) => UtcDayOfHour(ToUtcHour(instant));

    public static DateOnly FromUtcDay(int utcDay) => DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(utcDay));

    private static int YearOfUtcDay(int utcDay) => DateTime.UnixEpoch.AddDays(utcDay).Year;

    // ---- Merge --------------------------------------------------------------------------------

    /// <summary>
    /// Merges one scan's records into the affected year shards. Returns false on any failure; a
    /// failed merge must never surface mid-scan, and the next scan re-covers the same days anyway.
    /// </summary>
    /// <remarks>
    /// Idempotent by REPLACE-BY-SCOPE, never additive. A complete batch deletes every existing
    /// record for the days it DECLARED and writes its own, so re-scanning a day converges instead of
    /// doubling — and so an accounting fix that legitimately DECREASES a day (the Codex overcount
    /// fix, or Claude dedup removing a fork) is allowed to land. A partial batch merges per-key MAX
    /// instead, which is monotone and safe because session files only ever grow.
    /// <para/>
    /// A day the batch did not declare is merged per-key MAX even when the batch is complete: a
    /// complete batch is complete over its DECLARED WINDOW, not over every instant its records
    /// happen to touch. See the authority block in <c>MergeYear</c> — that gap is a local/UTC day
    /// boundary, and treating it as authority deleted imported history.
    /// <para/>
    /// Callers MUST NOT call this from a reader whose persistCache is false: such a reader is
    /// scanning a different corpus and would corrupt the user's real history with test data.
    /// </remarks>
    public static bool TryMerge(UsageLedgerScope scope, UsageLedgerBatch batch)
    {
        if (batch is null)
        {
            return false;
        }

        try
        {
            // Every day is filtered through the plausibility bound BEFORE it can name a year. This
            // is the merge's own floor and it is deliberately redundant with the parser's: a batch
            // can be built by anything, and one absurd day here costs a shard rebuild, not a row.
            var years = new SortedSet<int>();
            foreach (var day in batch.CoveredUtcDays)
            {
                if (IsRecordableUtcDay(day))
                {
                    years.Add(YearOfUtcDay(day));
                }
            }

            foreach (var record in batch.Records)
            {
                var day = UtcDayOfHour(record.Key.UtcHour);
                if (IsRecordableUtcDay(day))
                {
                    years.Add(YearOfUtcDay(day));
                }
            }

            var ok = true;
            foreach (var year in years)
            {
                ok &= MergeYear(scope, year, batch);
            }

            return ok;
        }
        catch
        {
            return false;
        }
    }

    private static bool MergeYear(UsageLedgerScope scope, int year, UsageLedgerBatch batch)
    {
        // Cross-process, because Local\ single-instance is per-logon-session and two app instances
        // can legitimately run. A lost write here loses DATA, not merely a cache hit, so unlike the
        // scan cache a read-modify-write race is not survivable.
        using var mutex = new Mutex(false, $"Local\\CodexBarWindows.usage-ledger.{ScopeName(scope)}.{year}");
        var held = false;
        try
        {
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died mid-write. We own the mutex now; the shard on disk is
                // still intact because writes are a single atomic File.Move.
                held = true;
            }

            if (!held)
            {
                return false;
            }

            // useCache: false — this instance is about to be MUTATED and written back, and the read
            // cache hands the same object to every reader. The cache is dropped after the save.
            var shard = TryLoadShard(scope, year, out _, useCache: false) ?? new UsageLedgerShard { V = SchemaVersion };
            shard.V = SchemaVersion;
            shard.A = batch.AccountingVersion;
            shard.App = AppVersion();
            shard.Tz = TimeZoneInfo.Local.Id;

            var models = new ModelTable(shard.M);
            var scannedTicks = batch.ScannedAt.UtcDateTime.Ticks;

            // ---- Authority ----------------------------------------------------------------------
            // A batch may only REPLACE days it explicitly DECLARED. It must never extend that
            // authority to the days its records merely happen to land on, and that one distinction
            // is the difference between a re-scan and destroying a user's imported months.
            //
            // Why the two differ at all: a row is admitted by the scan on the WALL-CLOCK date its
            // own log spells, but a record is keyed by the TRUE UTC INSTANT — and one local day
            // straddles two UTC days. A scan whose window opens on local day F therefore emits
            // records on UTC day F-1 (the tail of that UTC day which fell inside local day F) while
            // having read only a few hours of it. Promoting those records' day into the authority
            // set made a complete batch delete the WHOLE of UTC day F-1 and write back the sliver,
            // so every graphs-window open shaved the oldest boundary day off the backfilled history.
            //
            // Records outside the declared days are still merged — they are real usage — but per-key
            // MAX rather than replace-then-sum. MAX is monotone (it cannot delete) and idempotent (a
            // re-scan of the same sliver converges instead of doubling), which is precisely the two
            // properties the implicit promotion was reaching for.
            var covered = new HashSet<int>(
                batch.CoveredUtcDays.Where(day => IsRecordableUtcDay(day) && YearOfUtcDay(day) == year));

            if (batch.IsComplete)
            {
                foreach (var day in covered)
                {
                    // An explicitly covered day with no rows becomes an empty entry rather than a
                    // missing one: "scanned, found nothing" and "never scanned" are different facts
                    // and coverage has to be able to tell them apart.
                    shard.D[DayKey(day)] = new UsageLedgerShardDay { P = false, S = scannedTicks };
                }
            }

            foreach (var group in batch.Records
                .Where(record => !record.IsEmpty && IsRecordableUtcDay(UtcDayOfHour(record.Key.UtcHour)))
                .GroupBy(record => UtcDayOfHour(record.Key.UtcHour)))
            {
                if (YearOfUtcDay(group.Key) != year)
                {
                    continue;
                }

                var dayKey = DayKey(group.Key);
                var isNewDay = !shard.D.TryGetValue(dayKey, out var stored);
                var day = stored ?? new UsageLedgerShardDay();
                if (isNewDay)
                {
                    shard.D[dayKey] = day;
                }

                // The ONLY place replace-by-scope is decided. Declared + complete, or nothing.
                var replace = batch.IsComplete && covered.Contains(group.Key);

                day.S = scannedTicks;
                if (!batch.IsComplete)
                {
                    day.P = true;
                }
                else if (!replace && isNewDay)
                {
                    // A day this complete batch only CLIPPED is a lower bound of its own reading, so
                    // a day it just invented is partial. An existing one is not demoted: after a MAX
                    // merge its totals can only have risen, and stamping the ledger's boundary day
                    // partial on every single scan would leave a permanent false warning on history
                    // a manual import did read in full.
                    day.P = true;
                }

                var merged = new Dictionary<UsageLedgerKey, UsageLedgerRecord>();
                foreach (var existing in DecodeDay(day, models.NameAt, group.Key))
                {
                    merged[existing.Key] = existing;
                }

                // The batch's own duplicates are folded FIRST. Within a batch the same key can
                // legitimately appear twice (two rows, same hour and model) and those must sum; only
                // the summed value is then compared with what is already stored, so "sum my own
                // rows" can never leak into "add to history" on a day this batch cannot replace.
                var incoming = new Dictionary<UsageLedgerKey, UsageLedgerRecord>();
                foreach (var record in group)
                {
                    var key = Normalize(record.Key);
                    incoming[key] = incoming.TryGetValue(key, out var seen)
                        ? seen with
                        {
                            Standard = seen.Standard + record.Standard,
                            LongContext = seen.LongContext + record.LongContext,
                            Requests = seen.Requests + record.Requests
                        }
                        : record with { Key = key };
                }

                foreach (var (key, record) in incoming)
                {
                    // On a replaced day `merged` is empty (the entry was reset above), so this is
                    // the batch's own value verbatim. Everywhere else the stored value survives
                    // unless this batch genuinely saw more of that key.
                    merged[key] = replace || !merged.TryGetValue(key, out var current)
                        ? record
                        : current with
                        {
                            Standard = UsageLedgerTokens.Max(current.Standard, record.Standard),
                            LongContext = UsageLedgerTokens.Max(current.LongContext, record.LongContext),
                            Requests = Math.Max(current.Requests, record.Requests)
                        };
                }

                var kept = merged.Values.ToList();
                if (kept.Count > MaxRecordsPerDay)
                {
                    // Keep the largest keys: a truncated day should lose noise, not its headline.
                    kept = kept
                        .OrderByDescending(record => record.Combined.Total)
                        .ThenBy(record => record.Key.UtcHour)
                        .ThenBy(record => record.Key.Model, StringComparer.Ordinal)
                        .Take(MaxRecordsPerDay)
                        .ToList();
                    day.T = true;
                }

                day.R = kept
                    .OrderBy(record => record.Key.UtcHour)
                    .ThenBy(record => record.Key.Model, StringComparer.Ordinal)
                    .ThenBy(record => (int)record.Key.Flags)
                    .Select(record => Encode(record, models, group.Key))
                    .ToList();
            }

            shard.M = models.Ids;
            return TrySaveShard(scope, year, shard);
        }
        finally
        {
            if (held)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // Releasing can only fail if we never held it, which the flag already rules out.
                }
            }
        }
    }

    private static UsageLedgerKey Normalize(UsageLedgerKey key)
    {
        var model = key.Model ?? string.Empty;
        if (model.Length > MaxModelIdLength)
        {
            model = model[..MaxModelIdLength];
        }

        return key with
        {
            Model = model,
            ThresholdTokens = Math.Max(0, key.ThresholdTokens)
        };
    }

    private static string DayKey(int utcDay) => utcDay.ToString(CultureInfo.InvariantCulture);

    private static long[] Encode(UsageLedgerRecord record, ModelTable models, int utcDay)
    {
        return
        [
            record.Key.UtcHour - (utcDay * 24),
            models.IndexOf(record.Key.Model),
            (long)record.Key.Flags,
            record.Key.ThresholdTokens,
            record.Standard.Input, record.Standard.CachedInput, record.Standard.CacheCreation, record.Standard.Output,
            record.LongContext.Input, record.LongContext.CachedInput, record.LongContext.CacheCreation, record.LongContext.Output,
            record.Requests
        ];
    }

    /// <summary>
    /// Decodes a day, discarding anything structurally impossible. A hand-edited file must produce
    /// fewer records, never an exception on a read path.
    /// </summary>
    private static IEnumerable<UsageLedgerRecord> DecodeDay(UsageLedgerShardDay day, Func<long, string?> modelAt, int utcDay)
    {
        foreach (var row in day.R)
        {
            if (row is not { Length: RecordWidth })
            {
                continue;
            }

            var hourOfDay = row[0];
            if (hourOfDay is < 0 or > 23)
            {
                continue;
            }

            var model = modelAt(row[1]);
            if (model is null)
            {
                continue;
            }

            var key = new UsageLedgerKey(
                (utcDay * 24) + (int)hourOfDay,
                model,
                (UsageLedgerFlags)Math.Clamp(row[2], 0, 7),
                (int)Math.Clamp(row[3], 0, int.MaxValue));

            yield return new UsageLedgerRecord(
                key,
                new UsageLedgerTokens(NonNegative(row[4]), NonNegative(row[5]), NonNegative(row[6]), NonNegative(row[7])),
                new UsageLedgerTokens(NonNegative(row[8]), NonNegative(row[9]), NonNegative(row[10]), NonNegative(row[11])),
                (int)Math.Clamp(row[12], 0, int.MaxValue));
        }
    }

    private static long NonNegative(long value) => value < 0 ? 0 : value;

    // ---- Persistence --------------------------------------------------------------------------

    /// <summary>
    /// Returns the shard, or null when absent, oversized, unparseable, or written by a schema this
    /// build cannot read. <paramref name="unreadable"/> distinguishes "no such year" from "there is
    /// a file here I refuse to trust", which is what drives the re-import affordance.
    /// </summary>
    /// <param name="useCache">
    /// False for the merge, which mutates what it loads. Everything else reads only, so it shares
    /// one parsed instance per (scope, year) until the file on disk changes underneath it.
    /// </param>
    private static UsageLedgerShard? TryLoadShard(UsageLedgerScope scope, int year, out bool unreadable, bool useCache = true)
    {
        unreadable = false;
        try
        {
            var path = ShardPath(scope, year);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                ShardCache.TryRemove((scope, year), out _);
                return null;
            }

            // Identity, not existence: a shard rewritten by another instance of the app has the
            // same path and must not be served from this process's cache.
            var length = info.Length;
            var lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            if (useCache &&
                ShardCache.TryGetValue((scope, year), out var cached) &&
                cached.Length == length &&
                cached.LastWriteUtcTicks == lastWriteUtcTicks)
            {
                // Re-stamped on every HIT, not only on insert: without this the LRU degenerates into
                // "evict whatever was loaded first", which is exactly the year a live window is
                // sitting on while it walks the older ones for coverage.
                ShardCache[(scope, year)] = cached with { Tick = Interlocked.Increment(ref shardCacheTick) };
                return cached.Shard;
            }

            if (info.Length > MaxShardBytes)
            {
                // Refusing to parse is the point of the ceiling: a 500 MB file must not be turned
                // into 500 MB of managed objects on the UI's scan path.
                unreadable = true;
                ShardCache.TryRemove((scope, year), out _);
                return null;
            }

            Interlocked.Increment(ref ShardParseCount);
            UsageLedgerShard? shard;
            using (var stream = File.OpenRead(path))
            {
                shard = JsonSerializer.Deserialize<UsageLedgerShard>(stream, SerializerOptions);
            }

            if (shard is null || shard.V != SchemaVersion)
            {
                unreadable = shard is not null;
                ShardCache.TryRemove((scope, year), out _);
                return null;
            }

            shard.M ??= [];
            shard.D ??= [];
            if (shard.M.Count > MaxModelsPerShard)
            {
                shard.M = shard.M.Take(MaxModelsPerShard).ToList();
            }

            for (var i = 0; i < shard.M.Count; i++)
            {
                shard.M[i] ??= string.Empty;
            }

            foreach (var day in shard.D.Values)
            {
                day.R ??= [];
                if (day.R.Count > MaxRecordsPerDay)
                {
                    day.R = day.R.Take(MaxRecordsPerDay).ToList();
                    day.T = true;
                }
            }

            if (useCache)
            {
                // Stored AFTER sanitisation, so a cache hit skips that pass too and every reader
                // sees the same normalised object.
                ShardCache[(scope, year)] = new CachedShard(
                    length,
                    lastWriteUtcTicks,
                    Interlocked.Increment(ref shardCacheTick),
                    shard);
                TrimShardCache();
            }

            return shard;
        }
        catch
        {
            // Truncated, hand-edited, mid-write, locked — all the same answer: no history for this
            // year. Nothing on a read path is allowed to throw.
            unreadable = true;
            ShardCache.TryRemove((scope, year), out _);
            return null;
        }
    }

    private static bool TrySaveShard(UsageLedgerScope scope, int year, UsageLedgerShard shard)
    {
        string? temp = null;

        // A MERGE MUST INVALIDATE, and it must do so up front rather than on the way out: the write
        // below can fail at any point, and a reader that raced it must re-stat the file either way.
        // Dropping the entry (rather than replacing it with `shard`) also keeps the mutable instance
        // the merge owns out of the read cache entirely.
        ShardCache.TryRemove((scope, year), out _);

        try
        {
            // Drop days that ended up with nothing to say so a long-idle year does not accumulate
            // empty entries forever.
            foreach (var key in shard.D.Where(pair => pair.Value.R.Count == 0 && pair.Value.S == 0).Select(pair => pair.Key).ToList())
            {
                shard.D.Remove(key);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(shard, SerializerOptions);
            if (bytes.Length > MaxShardBytes)
            {
                // The per-day cap makes this unreachable in practice; if it ever fires, refusing the
                // write keeps the last good shard rather than replacing it with an unbounded one.
                return false;
            }

            var path = ShardPath(scope, year);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temp = Path.Combine(directory, $".tmp-{Guid.NewGuid():N}.json");

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                // Flush before the rename so a power loss cannot leave a renamed-but-empty shard.
                stream.Flush(flushToDisk: true);
            }

            // Single atomic replace: never delete-then-move, which leaves a window with no file.
            File.Move(temp, path, overwrite: true);
            temp = null;

            // Again, and not redundantly: a reader could have re-cached the PREVIOUS file between
            // the removal above and this move. Dropping it here leaves no window in which a stale
            // parse can outlive the write that replaced it.
            ShardCache.TryRemove((scope, year), out _);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // Nothing useful to do if cleanup itself fails.
                }
            }
        }
    }

    private static string AppVersion()
    {
        try
        {
            return AppInfo.CurrentVersion.ToString();
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>Interns raw model ids per shard and collapses overflow rather than growing without bound.</summary>
    private sealed class ModelTable
    {
        private readonly Dictionary<string, int> index = new(StringComparer.Ordinal);

        public ModelTable(List<string> ids)
        {
            Ids = ids;
            for (var i = 0; i < ids.Count; i++)
            {
                index.TryAdd(ids[i], i);
            }
        }

        public List<string> Ids { get; }

        public int IndexOf(string model)
        {
            if (index.TryGetValue(model, out var existing))
            {
                return existing;
            }

            if (Ids.Count >= MaxModelsPerShard)
            {
                model = OverflowModelId;
                if (index.TryGetValue(model, out var overflow))
                {
                    return overflow;
                }

                // One slot over the cap, once, so the overflow bucket itself always has a home.
                Ids.Add(model);
                index[model] = Ids.Count - 1;
                return Ids.Count - 1;
            }

            Ids.Add(model);
            index[model] = Ids.Count - 1;
            return Ids.Count - 1;
        }

        public string? NameAt(long i) => i >= 0 && i < Ids.Count ? Ids[(int)i] : null;
    }
}
