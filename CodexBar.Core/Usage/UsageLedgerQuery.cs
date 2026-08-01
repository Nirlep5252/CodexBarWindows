using System.Globalization;

namespace CodexBarWindows;

/// <summary>
/// The read side of the ledger: dense buckets over a local-time range, with a per-model breakdown
/// for every bucket and for the range as a whole.
/// </summary>
/// <remarks>
/// Records are UTC instants; local bucketing happens here and nowhere else, so changing time zone
/// costs nothing and re-import is never required for it. The one inherent limit is resolution: an
/// offset that is not a whole number of hours (IST is +05:30) puts the local day boundary mid
/// bucket, so at most one hour of usage per day boundary is attributed to the neighbouring local
/// day. That is a property of hourly resolution, not of the UTC choice, and the raw logs stay on
/// disk so a finer re-import is always possible.
/// </remarks>
public static partial class UsageLedger
{
    /// <summary>
    /// Aggregates the range into dense buckets. Never throws: an absent or untrusted shard simply
    /// contributes nothing, which reads as a period with no usage.
    /// </summary>
    public static UsageLedgerSeries Query(
        UsageLedgerScope scope,
        DateTimeOffset fromLocal,
        DateTimeOffset toLocalExclusive,
        UsageLedgerGranularity granularity,
        TimeZoneInfo? zone = null,
        UsageLedgerPricing? pricing = null)
    {
        try
        {
            zone ??= TimeZoneInfo.Local;
            if (toLocalExclusive <= fromLocal)
            {
                return UsageLedgerSeries.Empty(granularity);
            }

            var buckets = BuildBuckets(fromLocal, toLocalExclusive, granularity, zone);
            if (buckets.Count == 0)
            {
                return UsageLedgerSeries.Empty(granularity);
            }

            var accumulators = buckets.Select(bucket => new BucketAccumulator(bucket.StartLocal, bucket.EndLocalExclusive)).ToArray();
            var total = new BucketAccumulator(buckets[0].StartLocal, buckets[^1].EndLocalExclusive);

            // INSTANTS, not wall clock. A record is keyed by a true UTC hour and the buckets are a
            // partition of the real timeline, so the match is an instant comparison; matching on the
            // local wall clock instead is undefined exactly where DST makes it interesting - the
            // repeated hour of a fall-back day maps two distinct instants onto one local time, and
            // the array of local starts is not even strictly increasing there, which a binary search
            // requires.
            var starts = buckets.Select(bucket => bucket.StartLocal.UtcDateTime).ToArray();

            var fromUtc = fromLocal.UtcDateTime;
            var toUtc = toLocalExclusive.UtcDateTime;
            var hasPartialDays = false;
            var hasTruncatedDays = false;
            var thresholdMismatch = false;

            foreach (var (record, day) in EnumerateRecords(scope, fromUtc, toUtc))
            {
                hasPartialDays |= day.Partial;
                hasTruncatedDays |= day.Truncated;

                var instant = FromUtcHour(record.Key.UtcHour).UtcDateTime;
                var index = IndexOf(starts, instant);
                if (index < 0 || instant >= buckets[index].EndLocalExclusive.UtcDateTime)
                {
                    continue;
                }

                if (pricing?.ThresholdTokens is { } currentThreshold &&
                    (currentThreshold(record.Key.Model) ?? 0) != record.Key.ThresholdTokens)
                {
                    // The structural cutoff moved under the stored split, so the tier buckets no
                    // longer mean what the live table means. Readable, but worth a re-import.
                    thresholdMismatch = true;
                }

                var cost = pricing?.CostUsd?.Invoke(record);
                var label = (pricing?.ModelLabel ?? DefaultModelLabel)(record.Key.Model, record.Key.Flags.HasFlag(UsageLedgerFlags.Fast));
                accumulators[index].Add(record, label, cost);
                total.Add(record, label, cost);
            }

            var materialised = accumulators.Select(accumulator => accumulator.Build()).ToArray();
            var summary = total.Build();

            return new UsageLedgerSeries(
                granularity,
                materialised,
                summary.Models,
                summary.InputTokens,
                summary.CachedInputTokens,
                summary.CacheCreationTokens,
                summary.OutputTokens,
                summary.EstimatedCostUsd,
                summary.FastEstimatedCostUsd,
                summary.Requests,
                summary.HasIncompleteCost,
                hasPartialDays,
                hasTruncatedDays,
                thresholdMismatch);
        }
        catch
        {
            return UsageLedgerSeries.Empty(granularity);
        }
    }

    /// <summary>
    /// Everything ever recorded for a scope, as one bucket. Returns an empty series when the ledger
    /// holds nothing, so the caller does not have to special-case a cold install.
    /// </summary>
    /// <summary>
    /// The bucket PARTITION for a range, with every bucket zeroed and no shard read at all.
    /// </summary>
    /// <remarks>
    /// For a provider that has no ledger corpus (Grok: scan-only). The graphs window fills the
    /// scan's daily rows into ledger-shaped buckets, so it needs the bounds even when there is no
    /// ledger to ask - and the bounds are a pure function of the range, granularity and zone.
    ///
    /// The alternative, which this replaces, was to hand <see cref="Query"/> a scope that was
    /// expected to stay empty forever and use its buckets. That worked only for as long as nobody
    /// wrote a row to it, and put a real scope in the enum to represent the absence of one.
    /// </remarks>
    public static UsageLedgerSeries EmptyRange(
        DateTimeOffset fromLocal,
        DateTimeOffset toLocalExclusive,
        UsageLedgerGranularity granularity,
        TimeZoneInfo? zone = null)
    {
        try
        {
            zone ??= TimeZoneInfo.Local;
            if (toLocalExclusive <= fromLocal)
            {
                return UsageLedgerSeries.Empty(granularity);
            }

            var buckets = BuildBuckets(fromLocal, toLocalExclusive, granularity, zone);
            if (buckets.Count == 0)
            {
                return UsageLedgerSeries.Empty(granularity);
            }

            // Built through the same accumulator the real query uses, so an empty bucket here is
            // byte-for-byte the empty bucket a cold ledger would have produced.
            var materialised = buckets
                .Select(bucket => new BucketAccumulator(bucket.StartLocal, bucket.EndLocalExclusive).Build())
                .ToArray();

            return new UsageLedgerSeries(
                granularity,
                materialised,
                [],
                0, 0, 0, 0,
                0m, 0m, 0,
                false, false, false, false);
        }
        catch
        {
            return UsageLedgerSeries.Empty(granularity);
        }
    }

    public static UsageLedgerSeries QueryTotal(
        UsageLedgerScope scope,
        TimeZoneInfo? zone = null,
        UsageLedgerPricing? pricing = null)
    {
        zone ??= TimeZoneInfo.Local;
        var coverage = GetCoverage(scope);
        if (coverage.FirstRecordedDay is not { } first || coverage.LastRecordedDay is not { } last)
        {
            return UsageLedgerSeries.Empty(UsageLedgerGranularity.All);
        }

        // Widen by a day at each end: a UTC day maps onto parts of two local days for any non-zero
        // offset, so the local window that contains a UTC day range is strictly larger than it.
        var fromLocal = ToLocal(first.ToDateTime(TimeOnly.MinValue).AddDays(-1), zone);
        var toLocal = ToLocal(last.ToDateTime(TimeOnly.MinValue).AddDays(2), zone);
        return Query(scope, fromLocal, toLocal, UsageLedgerGranularity.All, zone, pricing);
    }

    /// <summary>
    /// What the ledger holds for a scope, including the earliest instant with real tokens — the
    /// timeline strip clamps its back arrow to this rather than assuming both providers share a
    /// range (the Codex corpus reaches months further back than the Claude one).
    /// </summary>
    public static UsageLedgerCoverage GetCoverage(UsageLedgerScope scope)
    {
        try
        {
            DateOnly? firstDay = null;
            DateOnly? lastDay = null;
            int? firstHour = null;
            int? lastHour = null;
            var dayCount = 0;
            var recordCount = 0;
            long bytes = 0;
            var accounting = 0;
            var partial = false;
            var unreadable = false;

            foreach (var year in ShardYears(scope))
            {
                var shard = TryLoadShard(scope, year, out var shardUnreadable);
                unreadable |= shardUnreadable;
                if (shard is null)
                {
                    continue;
                }

                try
                {
                    bytes += new FileInfo(ShardPath(scope, year)).Length;
                }
                catch
                {
                    // A size we cannot stat is not worth failing coverage over.
                }

                accounting = Math.Max(accounting, shard.A);
                var models = new ReadOnlyModelTable(shard.M);
                foreach (var (key, day) in shard.D)
                {
                    if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var utcDay))
                    {
                        continue;
                    }

                    dayCount++;
                    partial |= day.P;
                    var date = FromUtcDay(utcDay);
                    firstDay = firstDay is { } f && f <= date ? f : date;
                    lastDay = lastDay is { } l && l >= date ? l : date;

                    foreach (var record in DecodeDay(day, models.NameAt, utcDay))
                    {
                        recordCount++;
                        if (record.Combined.Total <= 0)
                        {
                            continue;
                        }

                        firstHour = firstHour is { } fh && fh <= record.Key.UtcHour ? fh : record.Key.UtcHour;
                        lastHour = lastHour is { } lh && lh >= record.Key.UtcHour ? lh : record.Key.UtcHour;
                    }
                }
            }

            if (dayCount == 0)
            {
                return UsageLedgerCoverage.None with { HasUnreadableShards = unreadable };
            }

            return new UsageLedgerCoverage(
                firstDay,
                lastDay,
                firstHour is { } fhv ? FromUtcHour(fhv) : null,
                // The last recorded hour BUCKET ends an hour after it starts; report the instant the
                // data actually reaches so a caller can use it as an exclusive upper bound.
                lastHour is { } lhv ? FromUtcHour(lhv + 1) : null,
                dayCount,
                recordCount,
                bytes,
                accounting,
                partial,
                unreadable);
        }
        catch
        {
            return UsageLedgerCoverage.None;
        }
    }

    private static string DefaultModelLabel(string model, bool isFast)
    {
        var label = string.IsNullOrWhiteSpace(model) ? "model" : model;
        return isFast ? label + " fast" : label;
    }

    private static IEnumerable<int> ShardYears(UsageLedgerScope scope)
    {
        var directory = RootDirectory;
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, $"{ScopeName(scope)}-*-v{SchemaVersion}.json");
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                yield return year;
            }
        }
    }

    private static IEnumerable<(UsageLedgerRecord Record, DayFlags Day)> EnumerateRecords(
        UsageLedgerScope scope,
        DateTime fromUtc,
        DateTime toUtcExclusive)
    {
        var fromHour = ToUtcHour(new DateTimeOffset(fromUtc, TimeSpan.Zero));
        var toHour = ToUtcHour(new DateTimeOffset(toUtcExclusive, TimeSpan.Zero));

        for (var year = fromUtc.Year; year <= toUtcExclusive.Year; year++)
        {
            var shard = TryLoadShard(scope, year, out _);
            if (shard is null)
            {
                continue;
            }

            var models = new ReadOnlyModelTable(shard.M);
            foreach (var (key, day) in shard.D)
            {
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var utcDay))
                {
                    continue;
                }

                var dayStartHour = utcDay * 24;
                if (dayStartHour + 24 <= fromHour || dayStartHour >= toHour)
                {
                    continue;
                }

                var flags = new DayFlags(day.P, day.T);
                foreach (var record in DecodeDay(day, models.NameAt, utcDay))
                {
                    if (record.Key.UtcHour >= fromHour && record.Key.UtcHour < toHour)
                    {
                        yield return (record, flags);
                    }
                }
            }
        }
    }

    private readonly record struct DayFlags(bool Partial, bool Truncated);

    private static int IndexOf(DateTime[] starts, DateTime value)
    {
        var index = Array.BinarySearch(starts, value);
        return index >= 0 ? index : ~index - 1;
    }

    private static DateTimeOffset ToLocal(DateTime unspecified, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(unspecified, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static List<BucketBounds> BuildBuckets(
        DateTimeOffset fromLocal,
        DateTimeOffset toLocalExclusive,
        UsageLedgerGranularity granularity,
        TimeZoneInfo zone)
    {
        var buckets = new List<BucketBounds>();
        if (granularity == UsageLedgerGranularity.All)
        {
            buckets.Add(new BucketBounds(fromLocal, toLocalExclusive));
            return buckets;
        }

        if (granularity == UsageLedgerGranularity.Hour)
        {
            // THE DAY'S OWN TIMELINE, not 24 wall-clock hours. A calendar day is a day however long
            // it is, so Day/Week/Month/Year still step in wall clock below - but an HOUR bucket is a
            // fixed span of real time, and walking the wall clock produced a bucket set that did not
            // partition the day it claimed to cover:
            //
            //   spring forward - 02:00 local never happens, so [02:00, 03:00) is the SAME instant
            //     twice: an empty, zero-width column that GraphsPeriod.Elapsed then counted as a
            //     whole elapsed hour, deflating Average and Projected all day.
            //   fall back - 01:00 local happens twice, so two real hours folded into one column and
            //     the day lost one of them.
            //
            // Advancing by a real hour from the day's first instant gives 23 columns on a short day
            // and 25 on a long one, every column exactly one hour wide - so the count of columns and
            // the count of elapsed hours are the same number by construction, which is the property
            // Elapsed relies on.
            var instant = ToLocal(Floor(fromLocal.DateTime, granularity), zone);
            while (instant < toLocalExclusive && buckets.Count < MaxBuckets)
            {
                var next = instant.AddHours(1);

                // Re-projected through the zone at each end rather than carried: the offset changes
                // mid-day, and the local face of a bucket is what labels and selects it.
                buckets.Add(new BucketBounds(
                    TimeZoneInfo.ConvertTime(instant, zone),
                    TimeZoneInfo.ConvertTime(next, zone)));
                instant = next;
            }

            return buckets;
        }

        var end = toLocalExclusive.DateTime;
        var cursor = Floor(fromLocal.DateTime, granularity);
        while (cursor < end && buckets.Count < MaxBuckets)
        {
            var next = Advance(cursor, granularity);
            buckets.Add(new BucketBounds(ToLocal(cursor, zone), ToLocal(next, zone)));
            cursor = next;
        }

        return buckets;
    }

    private static DateTime Floor(DateTime local, UsageLedgerGranularity granularity) => granularity switch
    {
        UsageLedgerGranularity.Hour => new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0),
        UsageLedgerGranularity.Day => local.Date,
        // ISO weeks (Monday start) rather than the culture's first day: the ledger's buckets have to
        // be stable across machines, and a culture flip must not silently re-slice history.
        UsageLedgerGranularity.Week => local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)),
        UsageLedgerGranularity.Month => new DateTime(local.Year, local.Month, 1),
        UsageLedgerGranularity.Year => new DateTime(local.Year, 1, 1),
        _ => local
    };

    private static DateTime Advance(DateTime local, UsageLedgerGranularity granularity) => granularity switch
    {
        UsageLedgerGranularity.Hour => local.AddHours(1),
        UsageLedgerGranularity.Day => local.AddDays(1),
        UsageLedgerGranularity.Week => local.AddDays(7),
        UsageLedgerGranularity.Month => local.AddMonths(1),
        UsageLedgerGranularity.Year => local.AddYears(1),
        _ => DateTime.MaxValue
    };

    /// <summary>
    /// One column's bounds. Both ends are INSTANTS carrying the zone's offset at that instant, which
    /// is the only representation that survives a DST boundary: the local face is
    /// <c>StartLocal.DateTime</c> and the identity used for matching is <c>StartLocal.UtcDateTime</c>.
    /// </summary>
    private readonly record struct BucketBounds(
        DateTimeOffset StartLocal,
        DateTimeOffset EndLocalExclusive);

    /// <summary>Read-side view of a shard's interned model table; never mutates the shard.</summary>
    private sealed class ReadOnlyModelTable(List<string> ids)
    {
        public string? NameAt(long index) => index >= 0 && index < ids.Count ? ids[(int)index] : null;
    }

    private sealed class BucketAccumulator(DateTimeOffset startLocal, DateTimeOffset endLocalExclusive)
    {
        private readonly Dictionary<string, ModelAccumulator> models = new(StringComparer.Ordinal);

        private long input;
        private long cachedInput;
        private long cacheCreation;
        private long output;
        private decimal cost;
        private decimal fastCost;
        private int requests;
        private bool incompleteCost;

        public void Add(UsageLedgerRecord record, string label, decimal? recordCost)
        {
            var tokens = record.Combined;
            input += tokens.Input;
            cachedInput += tokens.CachedInput;
            cacheCreation += tokens.CacheCreation;
            output += tokens.Output;
            requests += record.Requests;

            var isFast = record.Key.Flags.HasFlag(UsageLedgerFlags.Fast);

            // A vendor-priced row's real cost was money the vendor supplied, and money is exactly
            // what this store refuses to keep. Its tokens still count; its cost is reported as
            // unknown rather than re-derived from rates that never applied to it.
            var underivable = record.Key.Flags.HasFlag(UsageLedgerFlags.VendorPriced) || recordCost is null;
            incompleteCost |= underivable;
            var resolved = underivable ? 0m : recordCost!.Value;
            cost += resolved;
            if (isFast)
            {
                fastCost += resolved;
            }

            if (!models.TryGetValue(label, out var model))
            {
                model = new ModelAccumulator();
                models[label] = model;
            }

            model.Add(tokens, resolved, isFast, underivable);
        }

        public UsageLedgerBucket Build()
        {
            var breakdown = models
                .Select(pair => pair.Value.Build(pair.Key))
                .OrderByDescending(model => model.EstimatedCostUsd)
                .ThenByDescending(model => model.TotalTokens)
                .ThenBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Same shape the readers produce: one category per model label, cost-bearing only, with
            // the fast variants sorted after their base model.
            var categories = breakdown
                .Where(model => model.EstimatedCostUsd > 0)
                .Select(model => new ProviderSpendCategory(model.Model, model.EstimatedCostUsd))
                .OrderBy(category => category.Label.Contains(" fast", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new UsageLedgerBucket(
                startLocal,
                endLocalExclusive,
                input,
                cachedInput,
                cacheCreation,
                output,
                cost,
                fastCost,
                requests,
                breakdown,
                categories,
                incompleteCost);
        }

        private sealed class ModelAccumulator
        {
            private long input;
            private long cachedInput;
            private long cacheCreation;
            private long output;
            private decimal cost;
            private decimal fastCost;
            private bool incomplete;

            public void Add(UsageLedgerTokens tokens, decimal recordCost, bool isFast, bool underivable)
            {
                input += tokens.Input;
                cachedInput += tokens.CachedInput;
                cacheCreation += tokens.CacheCreation;
                output += tokens.Output;
                cost += recordCost;
                if (isFast)
                {
                    fastCost += recordCost;
                }

                incomplete |= underivable;
            }

            public ProviderModelUsage Build(string label)
                => new(label, input, cachedInput, cacheCreation, output, cost, fastCost, incomplete);
        }
    }
}
