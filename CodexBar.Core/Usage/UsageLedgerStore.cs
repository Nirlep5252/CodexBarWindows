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
    internal static void OverrideRootForTests(string? root) => rootOverride = root;

    internal static string RootDirectory => rootOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexBarWindows",
            "usage-ledger");

    internal static string ShardPath(UsageLedgerScope scope, int year)
        => Path.Combine(RootDirectory, $"{ScopeName(scope)}-{year}-v{SchemaVersion}.json");

    private static string ScopeName(UsageLedgerScope scope) => scope == UsageLedgerScope.Codex ? "codex" : "claude";

    // ---- Instant helpers ----------------------------------------------------------------------

    public static int ToUtcHour(DateTimeOffset instant)
        => (int)((instant.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerHour);

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
    /// record for the days it covers and writes its own, so re-scanning a day converges instead of
    /// doubling — and so an accounting fix that legitimately DECREASES a day (the Codex overcount
    /// fix, or Claude dedup removing a fork) is allowed to land. A partial batch merges per-key MAX
    /// instead, which is monotone and safe because session files only ever grow.
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
            var years = new SortedSet<int>();
            foreach (var day in batch.CoveredUtcDays)
            {
                years.Add(YearOfUtcDay(day));
            }

            foreach (var record in batch.Records)
            {
                years.Add(YearOfUtcDay(UtcDayOfHour(record.Key.UtcHour)));
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

            var shard = TryLoadShard(scope, year, out _) ?? new UsageLedgerShard { V = SchemaVersion };
            shard.V = SchemaVersion;
            shard.A = batch.AccountingVersion;
            shard.App = AppVersion();
            shard.Tz = TimeZoneInfo.Local.Id;

            var models = new ModelTable(shard.M);
            var scannedTicks = batch.ScannedAt.UtcDateTime.Ticks;

            if (batch.IsComplete)
            {
                // A complete batch implicitly asserts authority over every day it emitted a record
                // for, even one the caller forgot to declare. Without this a re-scan would SUM an
                // undeclared day into itself, which is exactly the double count replace-by-scope
                // exists to prevent.
                var covered = new HashSet<int>(batch.CoveredUtcDays);
                foreach (var record in batch.Records)
                {
                    covered.Add(UtcDayOfHour(record.Key.UtcHour));
                }

                foreach (var day in covered)
                {
                    if (YearOfUtcDay(day) != year)
                    {
                        continue;
                    }

                    // An explicitly covered day with no rows becomes an empty entry rather than a
                    // missing one: "scanned, found nothing" and "never scanned" are different facts
                    // and coverage has to be able to tell them apart.
                    shard.D[DayKey(day)] = new UsageLedgerShardDay { P = false, S = scannedTicks };
                }
            }

            foreach (var group in batch.Records.Where(record => !record.IsEmpty).GroupBy(record => UtcDayOfHour(record.Key.UtcHour)))
            {
                if (YearOfUtcDay(group.Key) != year)
                {
                    continue;
                }

                var dayKey = DayKey(group.Key);
                if (!shard.D.TryGetValue(dayKey, out var day))
                {
                    day = new UsageLedgerShardDay();
                    shard.D[dayKey] = day;
                }

                day.S = scannedTicks;
                if (!batch.IsComplete)
                {
                    day.P = true;
                }

                var merged = new Dictionary<UsageLedgerKey, UsageLedgerRecord>();
                foreach (var existing in DecodeDay(day, models.NameAt, group.Key))
                {
                    merged[existing.Key] = existing;
                }

                foreach (var record in group)
                {
                    var key = Normalize(record.Key);
                    if (!merged.TryGetValue(key, out var current))
                    {
                        merged[key] = record with { Key = key };
                        continue;
                    }

                    // Within a batch the same key can legitimately appear twice (two rows, same
                    // hour and model), so a complete batch SUMS its own duplicates — the previous
                    // contents were already deleted above, so this cannot double count history.
                    merged[key] = batch.IsComplete
                        ? current with
                        {
                            Standard = current.Standard + record.Standard,
                            LongContext = current.LongContext + record.LongContext,
                            Requests = current.Requests + record.Requests
                        }
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
    private static UsageLedgerShard? TryLoadShard(UsageLedgerScope scope, int year, out bool unreadable)
    {
        unreadable = false;
        try
        {
            var path = ShardPath(scope, year);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxShardBytes)
            {
                // Refusing to parse is the point of the ceiling: a 500 MB file must not be turned
                // into 500 MB of managed objects on the UI's scan path.
                unreadable = true;
                return null;
            }

            using var stream = File.OpenRead(path);
            var shard = JsonSerializer.Deserialize<UsageLedgerShard>(stream, SerializerOptions);
            if (shard is null || shard.V != SchemaVersion)
            {
                unreadable = shard is not null;
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

            return shard;
        }
        catch
        {
            // Truncated, hand-edited, mid-write, locked — all the same answer: no history for this
            // year. Nothing on a read path is allowed to throw.
            unreadable = true;
            return null;
        }
    }

    private static bool TrySaveShard(UsageLedgerScope scope, int year, UsageLedgerShard shard)
    {
        string? temp = null;
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
