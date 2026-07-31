using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

public sealed class ClaudeUsageInsightsReader
{
    private const int DaysToReport = 30;
    private const int ScanLookbackDays = 32;
    private const int MaxFilesToScan = 1200;

    /// <summary>Bounded so a refresh leaves cores free for the rest of the machine.</summary>
    private static int ScanParallelism => Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
    private readonly IReadOnlyList<string>? projectsRoots;
    private readonly bool refreshModelsDevPricing;

    public ClaudeUsageInsightsReader()
        : this(null, refreshModelsDevPricing: true)
    {
    }

    public ClaudeUsageInsightsReader(IReadOnlyList<string> projectsRoots)
        : this(projectsRoots, refreshModelsDevPricing: true)
    {
    }

    public ClaudeUsageInsightsReader(IReadOnlyList<string>? projectsRoots, bool refreshModelsDevPricing)
    {
        this.projectsRoots = projectsRoots;
        this.refreshModelsDevPricing = refreshModelsDevPricing;

        // The on-disk cache is a single shared file keyed by absolute path, so only the reader
        // scanning the real projects roots may write it — a custom-rooted reader (tests,
        // fixtures) scans a different corpus and would otherwise clobber the user's cache.
        persistCache = projectsRoots is null;
    }

    private readonly bool persistCache;

    public ProviderUsageInsightsLookupResult ReadLatest()
    {
        if (refreshModelsDevPricing)
        {
            ModelsDevPricing.RefreshInBackgroundIfNeeded();
        }

        try
        {
            var now = DateTimeOffset.Now;
            var today = DateOnly.FromDateTime(now.DateTime);
            var firstReportDay = today.AddDays(-(DaysToReport - 1));
            var firstScanDay = today.AddDays(-ScanLookbackDays);
            var roots = ResolveClaudeProjectsRoots().ToArray();
            var files = roots
                .SelectMany(root => EnumerateJsonlFiles(root, firstScanDay))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFilesToScan)
                .ToArray();

            if (files.Length == 0)
            {
                return new ProviderUsageInsightsLookupResult(
                    EmptyInsights(now, firstReportDay),
                    $"No Claude session logs were found under {string.Join("; ", roots)}.");
            }

            EnsurePersistedCacheLoaded();

            // Scanned in parallel (independent, CPU-bound, ConcurrentDictionary cache). Results
            // are collected by index rather than appended as they complete: DeduplicateAcrossFiles
            // is itself order-independent (RowWins is a total order and the output is re-sorted),
            // but a stable row order keeps runs reproducible and diffable.
            var perFile = new IReadOnlyList<ClaudeUsageRow>[files.Length];
            var options = new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism };
            Parallel.For(0, files.Length, options, index =>
            {
                perFile[index] = GetOrReadRowsFromFile(files[index], firstScanDay);
            });

            var rows = new List<ClaudeUsageRow>(perFile.Sum(list => list.Count));
            foreach (var list in perFile)
            {
                rows.AddRange(list);
            }

            PruneFileRowsCache(files.ToHashSet(StringComparer.OrdinalIgnoreCase));
            SavePersistedCache(firstReportDay);

            var daily = new Dictionary<DateOnly, MutableUsage>();
            var models = new Dictionary<string, MutableUsage>(StringComparer.OrdinalIgnoreCase);

            // Same gate as the scan cache: a reader pointed at custom roots scans a different
            // corpus, and the ledger is durable user data rather than an accelerator.
            var ledger = persistCache ? new ClaudeLedgerSink() : null;
            if (files.Length >= MaxFilesToScan)
            {
                // Enumeration was cut off, so this batch is a lower bound on the days it touched.
                // Claiming otherwise would let replace-by-scope delete real history.
                ledger?.Builder.MarkIncomplete();
            }

            foreach (var row in DeduplicateAcrossFiles(rows))
            {
                // Aggregate over the REPORTED window. Files are selected with a wider lookback
                // (ScanLookbackDays), and cached rows outlive the window they were scanned for,
                // so without this guard the model breakdown grows past the chart beside it —
                // unboundedly, for as long as the cache lives.
                if (row.Day < firstReportDay)
                {
                    continue;
                }

                var label = NormalizeClaudeModel(row.Model);

                // Claude History is Claude usage only. The projects tree also holds Claude Code's
                // own subagent and workflow transcripts, which can record another vendor's models
                // (a Codex subagent logs gpt-*). Those tokens were not spent on Anthropic, so
                // counting them inflated the totals and then surfaced as "no pricing" warnings.
                if (!IsAnthropicModel(label))
                {
                    continue;
                }
                Add(daily, row.Day, row.Model, row.Tokens, row.CostUsd, row.CostPriced, label);
                Add(models, label, row.Model, row.Tokens, row.CostUsd, row.CostPriced, label);
                ledger?.Add(row);
            }

            MergeIntoLedger(ledger?.Builder, firstReportDay, today, now);

            var dailyRows = Enumerable.Range(0, DaysToReport)
                .Select(offset => firstReportDay.AddDays(offset))
                .Select(day =>
                {
                    daily.TryGetValue(day, out var usage);
                    usage ??= new MutableUsage();
                    return ToDaily(day, usage);
                })
                .ToArray();

            var modelRows = models
                .Select(pair => ToModel(pair.Key, pair.Value))
                .Where(model => model.TotalTokens > 0 || model.EstimatedCostUsd > 0)
                .OrderByDescending(model => model.EstimatedCostUsd)
                .ThenByDescending(model => model.TotalTokens)
                .Take(8)
                .ToArray();

            var todayUsage = dailyRows.FirstOrDefault(row => row.Day == today)
                ?? new ProviderDailyUsage(today, 0, 0, 0, 0, 0);
            var hasIncompleteCost = dailyRows.Any(row => row.HasIncompleteCost) || modelRows.Any(row => row.HasIncompleteCost);
            var result = new ProviderUsageInsights(
                now,
                "Local Claude sessions",
                dailyRows,
                modelRows,
                todayUsage.TotalTokens,
                todayUsage.EstimatedCostUsd,
                dailyRows.Sum(row => row.TotalTokens),
                dailyRows.Sum(row => row.EstimatedCostUsd),
                HasIncompleteCost: hasIncompleteCost);

            // Name the unpriced models: "some models" gave no way to tell whether a new model
            // needs a pricing entry or a non-Claude id is leaking into this reader.
            var unpricedModels = modelRows
                .Where(row => row.HasIncompleteCost)
                .Select(row => row.Model)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var warning = hasIncompleteCost
                ? unpricedModels.Length > 0
                    ? $"No pricing for {string.Join(", ", unpricedModels)}; cost excludes these models."
                    : "Some Claude models had no pricing; cost may be incomplete."
                : result.HasUsage ? null : "No token usage entries were found in recent Claude session logs.";
            return new ProviderUsageInsightsLookupResult(result, warning);
        }
        catch (Exception exception)
        {
            return new ProviderUsageInsightsLookupResult(null, $"Could not read Claude usage history: {exception.Message}");
        }
    }

    private static ProviderUsageInsights EmptyInsights(DateTimeOffset observedAt, DateOnly firstReportDay)
    {
        var daily = Enumerable.Range(0, DaysToReport)
            .Select(offset => new ProviderDailyUsage(firstReportDay.AddDays(offset), 0, 0, 0, 0, 0))
            .ToArray();
        return new ProviderUsageInsights(observedAt, "Local Claude sessions", daily, [], 0, 0, 0, 0);
    }

    private IEnumerable<string> ResolveClaudeProjectsRoots()
    {
        if (projectsRoots is { Count: > 0 })
        {
            foreach (var root in projectsRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                yield return root;
            }

            yield break;
        }

        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            foreach (var part in configDir.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return Path.GetFileName(part.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    .Equals("projects", StringComparison.OrdinalIgnoreCase)
                    ? part
                    : Path.Combine(part, "projects");
            }

            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".config", "claude", "projects");
        yield return Path.Combine(home, ".claude", "projects");
    }

    private static IEnumerable<string> EnumerateJsonlFiles(string root, DateOnly firstScanDay)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (IsRelevantFile(file, firstScanDay))
            {
                yield return file;
            }
        }
    }

    private static bool IsRelevantFile(string path, DateOnly firstScanDay)
    {
        var dayFromName = DayFromText(Path.GetFileName(path));
        if (dayFromName is { })
        {
            return dayFromName >= firstScanDay;
        }

        try
        {
            return DateOnly.FromDateTime(File.GetLastWriteTime(path)) >= firstScanDay;
        }
        catch
        {
            return false;
        }
    }

    private sealed record CachedFileRows(
        long Length,
        long LastWriteUtcTicks,
        long CreationUtcTicks,
        DateOnly FirstScanDay,
        IReadOnlyList<ClaudeUsageRow> Rows);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedFileRows> FileRowsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private const string CacheProviderName = "claude";
    private static int persistedCacheLoaded;

    /// <summary>Set whenever a file is actually (re)scanned, so an unchanged refresh skips the write.</summary>
    private static int cacheDirty;

    /// <summary>
    /// Seeds the in-memory cache from disk once per process, on the first history read. Entries
    /// land in the same dictionary the scanner already consults, so the normal freshness check
    /// decides what still needs rescanning and <see cref="RepriceRow"/> still runs on every hit.
    /// </summary>
    private void EnsurePersistedCacheLoaded()
    {
        if (!persistCache || Interlocked.Exchange(ref persistedCacheLoaded, 1) == 1)
        {
            return;
        }

        var entries = UsageScanCache.TryLoad<ClaudeUsageRow>(CacheProviderName);
        if (entries is null)
        {
            return;
        }

        foreach (var (path, entry) in entries)
        {
            // FilePath is rehydrated from the key it was stripped against: DeduplicateAcrossFiles
            // tie-breaks on it via RowWins, so a blank path would change conflict resolution.
            var rows = entry.Rows.Count == 0
                ? entry.Rows
                : entry.Rows.Select(row => row with { FilePath = path }).ToArray();

            FileRowsCache.TryAdd(
                path,
                new CachedFileRows(entry.Length, entry.LastWriteUtcTicks, entry.CreationUtcTicks, entry.FirstScanDay, rows));
        }
    }

    /// <summary>
    /// Writes the current cache to disk, keeping only files that still exist and still carry rows
    /// inside the reported window. Cost fields are dropped: they are recomputed at replay, so
    /// persisting them would both waste space and freeze a stale price into the snapshot.
    /// </summary>
    private void SavePersistedCache(DateOnly firstReportDay)
    {
        if (!persistCache || Interlocked.Exchange(ref cacheDirty, 0) == 0)
        {
            return;
        }

        var entries = new Dictionary<string, UsageScanCacheEntry<ClaudeUsageRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, cached) in FileRowsCache)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            // Entries with no rows are kept (see PruneFileRowsCache). Cost is dropped because
            // RepriceRow recomputes it on every hit, and FilePath because it merely repeats the
            // dictionary key — it dominated the serialized size otherwise.
            var rows = cached.Rows
                .Where(row => row.Day >= firstReportDay)
                .Select(row => row with { CostUsd = null, CostPriced = false, FilePath = string.Empty })
                .ToArray();

            entries[path] = new UsageScanCacheEntry<ClaudeUsageRow>(
                cached.Length,
                cached.LastWriteUtcTicks,
                cached.CreationUtcTicks,
                cached.FirstScanDay > firstReportDay ? cached.FirstScanDay : firstReportDay,
                rows);
        }

        UsageScanCache.TrySave(CacheProviderName, entries);
    }

    private static IReadOnlyList<ClaudeUsageRow> GetOrReadRowsFromFile(string file, DateOnly firstScanDay)
    {
        long length;
        long lastWriteUtcTicks;
        long creationUtcTicks;
        try
        {
            var info = new FileInfo(file);
            length = info.Length;
            lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            creationUtcTicks = info.CreationTimeUtc.Ticks;
        }
        catch
        {
            return ReadRowsFromFile(file, firstScanDay);
        }

        // A cached scan taken with an earlier lookback window is still valid: rows older than the
        // current window were already excluded then, and days only move forward. Filtering out
        // rows that have since aged out happens downstream against the report window.
        if (FileRowsCache.TryGetValue(file, out var cached) &&
            cached.Length == length &&
            cached.LastWriteUtcTicks == lastWriteUtcTicks &&
            cached.CreationUtcTicks == creationUtcTicks &&
            cached.FirstScanDay <= firstScanDay)
        {
            // Cost is recomputed on every replay so a models.dev pricing refresh that landed
            // after the file was cached still prices these rows. This is also why the on-disk
            // cache can store rows with no cost at all.
            return cached.Rows.Select(RepriceRow).ToArray();
        }

        var rows = ReadRowsFromFile(file, firstScanDay);
        FileRowsCache[file] = new CachedFileRows(length, lastWriteUtcTicks, creationUtcTicks, firstScanDay, rows);
        Interlocked.Exchange(ref cacheDirty, 1);
        return rows;
    }

    private static ClaudeUsageRow RepriceRow(ClaudeUsageRow row)
    {
        var rawInput = Math.Max(0, row.Tokens.InputTokens - row.Tokens.CachedInputTokens);
        var cost = EstimateCost(row.Model, rawInput, row.Tokens.CachedInputTokens, row.Tokens.CacheCreationTokens, row.Tokens.OutputTokens);
        return row with { CostUsd = cost, CostPriced = cost is not null };
    }

    private static void PruneFileRowsCache(IReadOnlySet<string> scannedPaths)
    {
        // Retain exactly the files enumerated this pass: enumeration already applies the mtime
        // window, so this is both exact and free. Entries with no rows are kept deliberately —
        // "this file yields nothing" is a result worth caching, not a reason to discard it.
        foreach (var path in FileRowsCache.Keys)
        {
            if (!scannedPaths.Contains(path))
            {
                FileRowsCache.TryRemove(path, out _);
            }
        }
    }

    /// <summary>
    /// Scanner SEMANTICS version stamped on every batch. Bump it when an accounting rule changes,
    /// so days written by older logic are identifiable as suspect rather than silently mixed in.
    /// </summary>
    private const int LedgerAccountingVersion = 3;

    /// <summary>
    /// Collects the ledger's view of a scan alongside the reader's own aggregation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fed POST-dedup and post-<c>IsAnthropicModel</c>, at the exact site that reaches the daily
    /// buckets. <see cref="DeduplicateAcrossFiles"/> is global across the corpus, so feeding the
    /// per-file lists instead would count every fork and subagent transcript twice.
    /// </para>
    /// <para>
    /// Each component splits independently at the cutoff because <see cref="Tiered"/> does the same
    /// per component: within one request only the tokens above the cutoff bill higher. The memo
    /// exists because resolving a threshold costs a models.dev lookup per row otherwise.
    /// </para>
    /// </remarks>
    private sealed class ClaudeLedgerSink
    {
        private readonly Dictionary<string, int?> thresholds = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="builder">
        /// Supplied by the backfill, which owns one builder per worker thread. Null in the normal
        /// scan, where the sink owns the only builder there is.
        /// </param>
        public ClaudeLedgerSink(UsageLedgerBatchBuilder? builder = null)
        {
            Builder = builder ?? new UsageLedgerBatchBuilder(LedgerAccountingVersion);
        }

        public UsageLedgerBatchBuilder Builder { get; }

        public void Add(ClaudeUsageRow row)
        {
            if (!thresholds.TryGetValue(row.Model, out var threshold))
            {
                threshold = ThresholdTokensFor(row.Model);
                thresholds[row.Model] = threshold;
            }

            Builder.AddClaudeRow(
                row.Timestamp,
                row.Model,
                // TokenTotals.InputTokens is raw + cache read; Tiered() is called with the raw part.
                Math.Max(0, row.Tokens.InputTokens - row.Tokens.CachedInputTokens),
                row.Tokens.CachedInputTokens,
                row.Tokens.CacheCreationTokens,
                row.Tokens.OutputTokens,
                threshold);
        }
    }

    /// <summary>The live long-context cutoff, resolved exactly as <see cref="EstimateCost"/> resolves it.</summary>
    /// <remarks>
    /// Same resolver the ledger QUERY uses. When the two disagreed, a row could be split at 200k on
    /// write and priced as threshold-less on read.
    /// </remarks>
    private static int? ThresholdTokensFor(string model) => ClaudeModelPricing.ThresholdTokensFor(model);

    /// <summary>
    /// Hands the scan's records to the ledger and returns immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The covered-day declaration is what gives replace-by-scope its authority: rows are bucketed
    /// on the calendar day the log spelled (Claude Code writes UTC), so the reported window maps
    /// onto UTC day numbers directly. A row outside it still carries its own day into the merge,
    /// which is why a complete batch also asserts authority over the days it emitted records for.
    /// </para>
    /// <para>
    /// Off-thread on purpose. This runs inside the history scan (and therefore only while a window
    /// is open — property #1 is untouched), but the shard write is file I/O that the numbers on
    /// screen should not wait behind. The batch is immutable, so nothing is shared with the scan.
    /// </para>
    /// </remarks>
    private static void MergeIntoLedger(UsageLedgerBatchBuilder? builder, DateOnly firstReportDay, DateOnly today, DateTimeOffset scannedAt)
    {
        if (builder is null)
        {
            return;
        }

        builder.CoverDays(UtcMidnight(firstReportDay), UtcMidnight(today));
        var batch = builder.Build(scannedAt);
        _ = Task.Run(() => UsageLedger.TryMerge(UsageLedgerScope.Claude, batch));
    }

    private static DateTimeOffset UtcMidnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// The whole-corpus source used by <see cref="UsageLedgerBackfill"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reuses <see cref="ReadRowsFromFile"/> and the <see cref="IsAnthropicModel"/> filter verbatim,
    /// so a backfilled month is what the 30-day scan would have written for it. It does not touch
    /// <see cref="FileRowsCache"/>: that cache is sized for a 32-day window and pushing years of
    /// rows through it would blow the working set and hand the next save a file orders of magnitude
    /// larger than it is designed to write.
    /// </para>
    /// <para>
    /// DEDUP IS THE HARD PART. <see cref="DeduplicateAcrossFiles"/> is global — the same assistant
    /// message appears in a fork, a sidechain and a subagent transcript — and the scan can afford it
    /// because 32 days of rows fit in memory. A whole corpus does not. So the backfill keeps only a
    /// 128-bit FINGERPRINT per (messageId, requestId): ~24 B per row instead of a live row object,
    /// which turns the one structure that scales with the log size into tens of MB at worst. Two
    /// distinct messages colliding on 128 bits will not happen; if it somehow did, the cost is one
    /// undercounted row, which is the same direction the dedup itself errs in.
    /// </para>
    /// <para>
    /// The one fidelity difference: <see cref="RowWins"/> picks WHICH copy of a duplicate survives,
    /// and first-seen wins here instead. Duplicates are the same API response written twice, so the
    /// tokens, model and timestamp agree — only the losing copy's identity differs, and the ledger
    /// stores none of it. Counting it exactly once is the property that matters, and that holds.
    /// </para>
    /// </remarks>
    internal static IUsageLedgerBackfillSource CreateBackfillSource() => new ClaudeBackfillSource();

    private sealed class ClaudeBackfillSource : IUsageLedgerBackfillSource
    {
        private readonly ClaudeUsageInsightsReader reader = new(null, refreshModelsDevPricing: false);
        private readonly HashSet<(ulong High, ulong Low)> seen = [];

        public UsageLedgerScope Scope => UsageLedgerScope.Claude;

        public string DisplayName => "Claude";

        public int AccountingVersion => LedgerAccountingVersion;

        public IReadOnlyList<UsageLedgerBackfillFile> EnumerateFiles()
        {
            var files = new List<UsageLedgerBackfillFile>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The two default roots (~/.config/claude and ~/.claude) can be the same tree behind a
            // junction, and reading a file twice would only be absorbed by the dedup set for rows
            // that carry both ids. Deduplicate the PATHS as well.
            foreach (var root in reader.ResolveClaudeProjectsRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                IEnumerable<string> found;
                try
                {
                    found = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (var path in found)
                {
                    if (visited.Add(path))
                    {
                        files.Add(new UsageLedgerBackfillFile(path, CodexUsageInsightsReader.StampFor(path), false));
                    }
                }
            }

            files.Sort((left, right) =>
            {
                var byStamp = left.Stamp.CompareTo(right.Stamp);
                return byStamp != 0 ? byStamp : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            });

            return files;
        }

        public void Scan(UsageLedgerBackfillFile file, UsageLedgerBatchBuilder builder)
        {
            // DateOnly.MinValue: no lower bound at all. The scan's 32-day floor is exactly what puts
            // the older months out of reach, and this is the job that goes and gets them.
            var rows = ReadRowsFromFile(file.Path, DateOnly.MinValue);
            var sink = new ClaudeLedgerSink(builder);

            foreach (var row in rows)
            {
                // Claude History is Claude usage only: the projects tree also holds transcripts that
                // record another vendor's models, and those tokens were never spent on Anthropic.
                if (!IsAnthropicModel(NormalizeClaudeModel(row.Model)))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(row.MessageId) && !string.IsNullOrWhiteSpace(row.RequestId))
                {
                    var fingerprint = Fingerprint(row.MessageId, row.RequestId);
                    lock (seen)
                    {
                        if (!seen.Add(fingerprint))
                        {
                            continue;
                        }
                    }
                }

                sink.Add(row);
            }
        }

        /// <summary>
        /// A 128-bit fingerprint of a row's identity, as two independently seeded FNV-1a passes.
        /// Stored in place of the id strings so the dedup set costs bytes per row rather than the
        /// ~150 B a live key string and its row would.
        /// </summary>
        private static (ulong High, ulong Low) Fingerprint(string messageId, string requestId)
        {
            const ulong prime = 0x0000_0100_0000_01B3;
            var high = 0xCBF2_9CE4_8422_2325UL;
            var low = 0x9E37_79B9_7F4A_7C15UL;

            static void Mix(string value, ref ulong high, ref ulong low)
            {
                foreach (var c in value)
                {
                    high = (high ^ c) * prime;
                    low = ((low + c) * 0x2545_F491_4F6C_DD1DUL) ^ (low >> 29);
                }
            }

            Mix(messageId, ref high, ref low);
            // A separator, so ("ab","c") and ("a","bc") cannot fingerprint alike.
            Mix("\0", ref high, ref low);
            Mix(requestId, ref high, ref low);
            return (high, low);
        }
    }

    private static IReadOnlyList<ClaudeUsageRow> ReadRowsFromFile(string file, DateOnly firstScanDay)
    {
        var keyed = new Dictionary<string, ClaudeUsageRow>(StringComparer.Ordinal);
        var unkeyed = new List<ClaudeUsageRow>();
        var pathRole = file.Replace('\\', '/').Contains("/subagents/", StringComparison.OrdinalIgnoreCase)
            ? ClaudePathRole.Subagent
            : ClaudePathRole.Parent;

        const int maxLineChars = 512 * 1024;
        foreach (var line in ReadSharedLines(file))
        {
            if (string.IsNullOrWhiteSpace(line) ||
                line.Length > maxLineChars ||
                (!line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) &&
                 !line.Contains("\"type\": \"assistant\"", StringComparison.Ordinal)) ||
                !line.Contains("\"usage\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!string.Equals(ReadString(root, "type"), "assistant", StringComparison.OrdinalIgnoreCase) || IsVertexAIUsageEntry(root))
                {
                    continue;
                }

                var at = ReadTimestamp(root);
                if (at is null || DateOnly.FromDateTime(at.Value.DateTime) < firstScanDay)
                {
                    continue;
                }

                if (!root.TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("usage", out var usage))
                {
                    continue;
                }

                var model = ReadString(message, "model");
                if (string.IsNullOrWhiteSpace(model))
                {
                    continue;
                }

                var rawInput = ReadLong(usage, "input_tokens");
                var cacheRead = ReadLong(usage, "cache_read_input_tokens");
                var cacheCreate = ReadLong(usage, "cache_creation_input_tokens");
                var output = ReadLong(usage, "output_tokens");
                if (rawInput == 0 && cacheRead == 0 && cacheCreate == 0 && output == 0)
                {
                    continue;
                }

                var tokens = new TokenTotals(rawInput + cacheRead, cacheRead, cacheCreate, output);
                var cost = EstimateCost(model, rawInput, cacheRead, cacheCreate, output);
                var row = new ClaudeUsageRow(
                    file,
                    at.Value,
                    model,
                    ReadString(message, "id"),
                    ReadString(root, "requestId"),
                    ReadString(root, "sessionId") ?? ReadString(root, "session_id") ?? ReadNestedString(root, "metadata", "sessionId"),
                    ReadBool(root, "isSidechain"),
                    pathRole,
                    tokens,
                    cost,
                    cost is not null);

                if (!string.IsNullOrWhiteSpace(row.MessageId) && !string.IsNullOrWhiteSpace(row.RequestId))
                {
                    keyed[$"{row.MessageId}:{row.RequestId}"] = row;
                }
                else
                {
                    unkeyed.Add(row);
                }
            }
            catch
            {
                // Claude session logs may contain partial/future-format rows. Ignore only the bad row.
            }
        }

        return keyed.Keys.OrderBy(key => key, StringComparer.Ordinal).Select(key => keyed[key]).Concat(unkeyed).ToArray();
    }

    private static IEnumerable<ClaudeUsageRow> DeduplicateAcrossFiles(IEnumerable<ClaudeUsageRow> rows)
    {
        var winners = new Dictionary<string, ClaudeUsageRow>(StringComparer.Ordinal);
        var unkeyed = new List<ClaudeUsageRow>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.MessageId) || string.IsNullOrWhiteSpace(row.RequestId))
            {
                unkeyed.Add(row);
                continue;
            }

            var key = $"{row.MessageId}:{row.RequestId}";
            if (!winners.TryGetValue(key, out var existing) || RowWins(row, existing))
            {
                winners[key] = row;
            }
        }

        return winners.Keys.OrderBy(key => key, StringComparer.Ordinal).Select(key => winners[key]).Concat(unkeyed);
    }

    private static bool RowWins(ClaudeUsageRow candidate, ClaudeUsageRow existing)
    {
        if (candidate.IsSidechain != existing.IsSidechain)
        {
            return existing.IsSidechain;
        }

        if (candidate.PathRole != existing.PathRole)
        {
            return existing.PathRole == ClaudePathRole.Subagent;
        }

        return string.Compare(candidate.FilePath, existing.FilePath, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static void Add(IDictionary<DateOnly, MutableUsage> daily, DateOnly day, string model, TokenTotals tokens, decimal? exactCostUsd, bool costPriced, string categoryLabel)
    {
        if (!daily.TryGetValue(day, out var usage))
        {
            usage = new MutableUsage();
            daily[day] = usage;
        }

        usage.Add(model, tokens, exactCostUsd, costPriced, categoryLabel, categoryLabel);
    }

    private static void Add(IDictionary<string, MutableUsage> models, string key, string model, TokenTotals tokens, decimal? exactCostUsd, bool costPriced, string displayName)
    {
        if (!models.TryGetValue(key, out var usage))
        {
            usage = new MutableUsage();
            models[key] = usage;
        }

        usage.Add(model, tokens, exactCostUsd, costPriced, displayName, displayName);
    }

    private static ProviderDailyUsage ToDaily(DateOnly day, MutableUsage usage)
    {
        return new ProviderDailyUsage(day, usage.InputTokens, usage.CachedInputTokens, usage.CacheCreationTokens, usage.OutputTokens, usage.EstimatedCostUsd, SpendCategories: usage.SpendCategories, HasIncompleteCost: usage.HasIncompleteCost);
    }

    private static ProviderModelUsage ToModel(string model, MutableUsage usage)
    {
        return new ProviderModelUsage(usage.DisplayName ?? model, usage.InputTokens, usage.CachedInputTokens, usage.CacheCreationTokens, usage.OutputTokens, usage.EstimatedCostUsd, HasIncompleteCost: usage.HasIncompleteCost);
    }

    // Every pricing decision below is delegated to ClaudeModelPricing so the ledger read path prices
    // the identical row identically. These wrappers stay only because the call sites in this file
    // read better without the type prefix; they must never grow logic of their own.
    private static decimal? EstimateCost(string model, long rawInput, long cacheRead, long cacheCreate, long output)
        => ClaudeModelPricing.EstimateCost(model, rawInput, cacheRead, cacheCreate, output);

    private static bool IsAnthropicModel(string normalizedModel) => ClaudeModelPricing.IsAnthropicModel(normalizedModel);

    private static string NormalizeClaudeModel(string raw) => ClaudeModelPricing.NormalizeModelName(raw);

    private static bool IsVertexAIUsageEntry(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message))
        {
            var messageId = ReadString(message, "id");
            if (messageId?.Contains("_vrtx_", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            var model = ReadString(message, "model");
            if (model?.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) == true && model.Contains('@'))
            {
                return true;
            }
        }

        var requestId = ReadString(root, "requestId");
        if (requestId?.Contains("_vrtx_", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (ContainsVertexMetadata(root))
        {
            return true;
        }

        foreach (var containerName in new[] { "metadata", "request", "context", "client" })
        {
            if (root.TryGetProperty(containerName, out var container) && ContainsVertexMetadata(container))
            {
                return true;
            }
        }

        if (root.TryGetProperty("message", out var messageWithMetadata))
        {
            foreach (var containerName in new[] { "metadata", "request" })
            {
                if (messageWithMetadata.TryGetProperty(containerName, out var container) && ContainsVertexMetadata(container))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsVertexMetadata(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            var key = property.Name.ToLowerInvariant();
            if (key.Contains("vertex", StringComparison.Ordinal) || key.Contains("gcp", StringComparison.Ordinal))
            {
                return true;
            }

            if (IsProviderHintKey(key) &&
                property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString()?.Contains("vertex", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProviderHintKey(string key)
    {
        return key is "provider" or "platform" or "backend" or "api_provider" or "apiprovider" or
            "api_type" or "apitype" or "source" or "vendor" or "client";
    }

    /// <summary>
    /// The hour a transcript line belongs to, in the frame the line was written in.
    /// </summary>
    /// <remarks>
    /// The DATE is bit-for-bit the one the old day-only path produced — the calendar date as the
    /// log spells it, not a converted one — so no reported figure moves because rows grew an hour
    /// column. The declared offset rides along only so the ledger can recover the true instant.
    /// Truncated to the hour: the ledger's finest bucket is an hour, and minutes would cost bytes
    /// in every cached row for nothing.
    /// </remarks>
    private static DateTimeOffset? ReadTimestamp(JsonElement element)
    {
        return UsageTimestampText.TryParseHour(ReadString(element, "timestamp"), out var timestamp) ? timestamp : null;
    }

    private static DateOnly? DayFromText(string? value)
    {
        return UsageTimestampText.TryFindDate(value, out _, out var year, out var month, out var day) &&
            UsageTimestampText.TryMakeDate(year, month, day, out var parsed)
            ? parsed
            : null;
    }

    private static long ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return Math.Max(0, number);
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Max(0, parsed);
        }

        return 0;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName)
    {
        return element.TryGetProperty(objectName, out var nested) ? ReadString(nested, propertyName) : null;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            _ => false
        };
    }

    private sealed class MutableUsage
    {
        public long InputTokens { get; private set; }
        public long CachedInputTokens { get; private set; }
        public long CacheCreationTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public decimal EstimatedCostUsd { get; private set; }
        public bool HasIncompleteCost { get; private set; }
        public string? DisplayName { get; private set; }
        private readonly Dictionary<string, decimal> spendCategories = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ProviderSpendCategory> SpendCategories => spendCategories
            .Select(pair => new ProviderSpendCategory(pair.Key, pair.Value))
            .OrderByDescending(category => category.EstimatedCostUsd)
            .ToArray();

        public void Add(string model, TokenTotals tokens, decimal? exactCostUsd, bool costPriced, string displayName, string categoryLabel)
        {
            DisplayName ??= displayName;
            InputTokens += tokens.InputTokens;
            CachedInputTokens += tokens.CachedInputTokens;
            CacheCreationTokens += tokens.CacheCreationTokens;
            OutputTokens += tokens.OutputTokens;
            if (!costPriced || exactCostUsd is null)
            {
                HasIncompleteCost = true;
                return;
            }

            EstimatedCostUsd += exactCostUsd.Value;
            if (exactCostUsd.Value > 0)
            {
                spendCategories[categoryLabel] = spendCategories.TryGetValue(categoryLabel, out var existing)
                    ? existing + exactCostUsd.Value
                    : exactCostUsd.Value;
            }
        }
    }

    private readonly record struct TokenTotals(long InputTokens, long CachedInputTokens, long CacheCreationTokens, long OutputTokens);

    private sealed record ClaudeUsageRow(
        string FilePath,
        DateTimeOffset Timestamp,
        string Model,
        string? MessageId,
        string? RequestId,
        string? SessionId,
        bool IsSidechain,
        ClaudePathRole PathRole,
        TokenTotals Tokens,
        decimal? CostUsd,
        bool CostPriced)
    {
        /// <summary>
        /// The calendar day the log wrote, unconverted — identical to the DateOnly this row used to
        /// carry. Ignored by the serializer: it is derivable, and the cache pays for every byte.
        /// </summary>
        [JsonIgnore]
        public DateOnly Day => DateOnly.FromDateTime(Timestamp.DateTime);
    }

    private enum ClaudePathRole
    {
        Parent,
        Subagent
    }

}
