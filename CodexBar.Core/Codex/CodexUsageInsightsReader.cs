using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

public sealed class CodexUsageInsightsReader
{
    private const int DaysToReport = 30;
    private const int ScanLookbackDays = 32;
    private const int MaxFilesToScan = 1200;
    private readonly string codexHome;

    /// <summary>
    /// The on-disk cache is a single shared file keyed by absolute path, so only the reader
    /// scanning the real Codex home may write it. A reader pointed at a custom home (tests,
    /// fixtures) scans a different corpus and would otherwise clobber the user's cache.
    /// </summary>
    private readonly bool persistCache;

    /// <summary>
    /// Whether this reader feeds the durable ledger. Follows <see cref="persistCache"/> everywhere
    /// except the test seam below, which needs the ledger interaction without the shared cache file.
    /// </summary>
    private readonly bool writeLedger;

    /// <summary>Tests need the merge to have happened by the time <c>ReadLatest</c> returns.</summary>
    private readonly bool mergeLedgerSynchronously;

    public CodexUsageInsightsReader()
        : this(ResolveCodexHome(), persistCache: true)
    {
    }

    public CodexUsageInsightsReader(string codexHome)
        : this(codexHome, persistCache: false)
    {
    }

    private CodexUsageInsightsReader(
        string codexHome,
        bool persistCache,
        bool? writeLedger = null,
        bool mergeLedgerSynchronously = false)
    {
        this.codexHome = codexHome;
        this.persistCache = persistCache;
        this.writeLedger = writeLedger ?? persistCache;
        this.mergeLedgerSynchronously = mergeLedgerSynchronously;
    }

    /// <summary>
    /// Test seam: a reader over a fixture corpus that DOES feed the ledger.
    /// </summary>
    /// <remarks>
    /// The interaction worth testing is the one that loses data — the 30-day scan's replace-by-scope
    /// against days a manual import recovered — and it is unreachable while the ledger write is
    /// gated on the same flag as the shared scan-cache file. So the two gates are separated here and
    /// only here: the scan cache stays OFF (it is one file keyed by absolute path, and a fixture
    /// corpus would poison it), while the ledger root must ALREADY be redirected, which is asserted
    /// rather than assumed. Both of the user's durable stores are therefore out of reach by
    /// construction, not by care.
    /// </remarks>
    internal static CodexUsageInsightsReader CreateLedgerWritingReaderForTests(string codexHome)
    {
        if (!UsageLedger.IsRootOverridden)
        {
            throw new InvalidOperationException(
                "A ledger-writing test reader requires UsageLedger.OverrideRootForTests; refusing to write the user's real history.");
        }

        return new CodexUsageInsightsReader(codexHome, persistCache: false, writeLedger: true, mergeLedgerSynchronously: true);
    }

    public ProviderUsageInsightsLookupResult ReadLatest()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var today = DateOnly.FromDateTime(now.DateTime);
            var firstReportDay = today.AddDays(-(DaysToReport - 1));
            var firstScanDay = today.AddDays(-ScanLookbackDays);

            var codexFiles = EnumerateCodexJsonlFiles(firstScanDay)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFilesToScan)
                .ToArray();
            var piSessionsRoot = ResolvePiSessionsRoot();
            var piFiles = EnumeratePiJsonlFiles(piSessionsRoot, firstScanDay)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFilesToScan)
                .ToArray();

            if (codexFiles.Length == 0 && piFiles.Length == 0)
            {
                return new ProviderUsageInsightsLookupResult(
                    EmptyInsights(now, firstReportDay),
                    $"No Codex or pi session logs were found under {codexHome} or {piSessionsRoot}.");
            }

            var daily = new Dictionary<DateOnly, MutableUsage>();
            var models = new Dictionary<string, MutableUsage>(StringComparer.OrdinalIgnoreCase);
            var fastTurnIds = ReadFastTurnIdsFromCodexLogs(codexHome);

            // Per-file scanning is independent and CPU-bound (JSON parse dominates), so it runs
            // in parallel; FileRowsCache is a ConcurrentDictionary. Aggregation stays sequential
            // below because MutableUsage accumulators are not thread-safe. DOP is capped so a
            // refresh does not monopolise the machine — this only ever runs while a window is
            // open, so idle CPU is unaffected.
            EnsurePersistedCacheLoaded();
            var scanned = ScanFilesInParallel(codexFiles, piFiles, firstScanDay);

            // Same gate as the scan cache: a reader pointed at a custom home scans a different
            // corpus, and the ledger is durable user data rather than an accelerator.
            var ledger = writeLedger ? new CodexLedgerSink() : null;
            if (codexFiles.Length >= MaxFilesToScan || piFiles.Length >= MaxFilesToScan)
            {
                // Enumeration was cut off, so this batch is a lower bound on the days it touched.
                // Claiming otherwise would let replace-by-scope delete real history.
                ledger?.Builder.MarkIncomplete();
            }

            foreach (var row in scanned)
            {
                ApplyRow(row, firstReportDay, daily, models, fastTurnIds, ledger);
            }

            var piPaths = piFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var scannedPaths = codexFiles.Concat(piFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
            PruneFileRowsCache(scannedPaths);
            SavePersistedCache(firstReportDay, piPaths);
            MergeIntoLedger(ledger?.Builder, firstReportDay, now, mergeLedgerSynchronously);

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

            var result = new ProviderUsageInsights(
                now,
                $"Local Codex + pi sessions ({codexHome}; {piSessionsRoot})",
                dailyRows,
                modelRows,
                todayUsage.TotalTokens,
                todayUsage.EstimatedCostUsd,
                dailyRows.Sum(row => row.TotalTokens),
                dailyRows.Sum(row => row.EstimatedCostUsd),
                todayUsage.FastEstimatedCostUsd,
                dailyRows.Sum(row => row.FastEstimatedCostUsd));

            var error = result.HasUsage ? null : "No token usage entries were found in recent Codex or pi session logs.";
            return new ProviderUsageInsightsLookupResult(result, error);
        }
        catch (Exception exception)
        {
            return new ProviderUsageInsightsLookupResult(null, $"Could not read Codex usage history: {exception.Message}");
        }
    }

    private static ProviderUsageInsights EmptyInsights(DateTimeOffset observedAt, DateOnly firstReportDay)
    {
        var daily = Enumerable.Range(0, DaysToReport)
            .Select(offset => new ProviderDailyUsage(firstReportDay.AddDays(offset), 0, 0, 0, 0, 0))
            .ToArray();

        return new ProviderUsageInsights(observedAt, "Local Codex + pi sessions", daily, [], 0, 0, 0, 0);
    }

    private IEnumerable<string> EnumerateCodexJsonlFiles(DateOnly firstScanDay)
    {
        foreach (var root in SessionRoots())
        {
            foreach (var file in EnumerateRelevantJsonlFiles(root, firstScanDay))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> EnumeratePiJsonlFiles(string piSessionsRoot, DateOnly firstScanDay)
        => EnumerateRelevantJsonlFiles(piSessionsRoot, firstScanDay);

    /// <summary>
    /// Every session file under <paramref name="root"/> that could hold a row inside the scan window.
    /// </summary>
    /// <remarks>
    /// <see cref="DirectoryInfo.EnumerateFiles(string, SearchOption)"/>, not the string overload, and
    /// that is the whole reason the widened relevance test below is free: on Windows the walk already
    /// reads each entry's write time out of the find data, so the <see cref="FileInfo"/> it hands back
    /// answers <c>LastWriteTime</c> without a single extra syscall.
    /// </remarks>
    private static IEnumerable<string> EnumerateRelevantJsonlFiles(string root, DateOnly firstScanDay)
    {
        IEnumerable<FileInfo> files;
        try
        {
            var directory = new DirectoryInfo(root);
            if (!directory.Exists)
            {
                yield break;
            }

            files = directory.EnumerateFiles("*.jsonl", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (IsRelevantFile(file, firstScanDay))
            {
                yield return file.FullName;
            }
        }
    }

    /// <summary>
    /// Whether a session file can contribute a row inside the scan window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BOTH dates, UNIONED, and that is load-bearing rather than defensive. Codex names a rollout
    /// after the day the session STARTED and then keeps appending to that same file for as long as
    /// the session is resumed — so a name-only test drops files that hold today's rows once the
    /// session is older than the lookback.
    /// </para>
    /// <para>
    /// That was not merely a missing number. This scan declares its 30 reported days COMPLETE, and
    /// the ledger honours a complete batch by DELETING every existing record for those days before
    /// writing its own. A file the enumeration skipped therefore took the history the manual import
    /// had recovered from it down with it, on the next graphs-window open. Coverage may only be
    /// claimed over sources the walk actually reached, so the walk now reaches everything either
    /// date puts in the window and the claim in <see cref="MergeIntoLedger"/> holds.
    /// </para>
    /// <para>
    /// It costs nothing: the write time comes from the directory walk (see above), and the extra
    /// files admitted are exactly the long-lived sessions that were being lost — a handful, against
    /// the ~1,800 already in the window. The ~6.3 s cold / ~2.8 s warm scan is unchanged.
    /// </para>
    /// </remarks>
    private static bool IsRelevantFile(FileInfo file, DateOnly firstScanDay)
    {
        if (DayFromText(file.Name) is { } dayFromName && dayFromName >= firstScanDay)
        {
            return true;
        }

        try
        {
            return DateOnly.FromDateTime(file.LastWriteTime) >= firstScanDay;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<string> SessionRoots()
    {
        yield return Path.Combine(codexHome, "sessions");
        yield return Path.Combine(codexHome, "archived_sessions");
    }

    private static IReadOnlyList<CodexScanRow> ScanCodexFile(string file, DateOnly firstScanDay)
    {
        var rows = new List<CodexScanRow>();
        var shape = ClassifyCodexRollout(file);
        if (shape.SuppressWholeFile)
        {
            return rows;
        }

        string? currentModel = null;
        string? currentTurnId = null;
        // Fast-turn-id membership is resolved at apply time so cached rows stay valid when the
        // Codex log databases (and therefore the fast-turn set) change between refreshes.
        var currentBaseFast = false;
        var accountant = new CodexTotalsAccountant(shape.OwnedSuffixBaseline, shape.PrefersTotalsAccounting);
        var lineIndex = -1;

        foreach (var line in ReadSharedLines(file))
        {
            lineIndex++;
            if (lineIndex < shape.OwnedSuffixStartLine ||
                string.IsNullOrWhiteSpace(line) ||
                (!line.Contains("\"token_count\"", StringComparison.Ordinal) &&
                 !line.Contains("\"turn_context\"", StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeElement.GetString();
                if (string.Equals(type, "turn_context", StringComparison.OrdinalIgnoreCase))
                {
                    currentModel = ReadModel(root) ?? currentModel;
                    currentTurnId = ReadTurnId(root);
                    currentBaseFast = IsFastMode(currentModel ?? "Codex model", default, null, root);
                    continue;
                }

                if (!string.Equals(type, "event_msg", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("type", out var payloadType) ||
                    !string.Equals(payloadType.GetString(), "token_count", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The cumulative counters must advance for every row, so out-of-range days are
                // filtered after accounting rather than skipped outright.
                if (!accountant.TryNextDelta(payload, out var delta))
                {
                    continue;
                }

                var at = ReadTimestamp(root);
                if (at is null || DateOnly.FromDateTime(at.Value.DateTime) < firstScanDay)
                {
                    continue;
                }

                var model = ReadModel(root) ?? ReadModel(payload) ?? currentModel ?? "Codex model";
                var rowIsFastMode = payload.TryGetProperty("rate_limits", out var rateLimits)
                    ? IsFastMode(model, delta, null, root, payload, rateLimits)
                    : IsFastMode(model, delta, null, root, payload);
                rows.Add(new CodexScanRow(at.Value, model, delta, currentBaseFast || rowIsFastMode, currentTurnId, null));
            }
            catch
            {
                // Session logs may contain partial or future-format rows. Ignore only the bad row.
            }
        }

        return rows;
    }

    private static IReadOnlyList<CodexScanRow> ScanPiFile(string file, DateOnly firstScanDay)
    {
        var rows = new List<CodexScanRow>();
        string? currentModel = null;
        var currentProviderIsCodex = false;

        foreach (var line in ReadSharedLines(file))
        {
            if (string.IsNullOrWhiteSpace(line) ||
                (!line.Contains("\"model_change\"", StringComparison.Ordinal) &&
                 !line.Contains("\"message\"", StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeElement.GetString();
                if (string.Equals(type, "model_change", StringComparison.OrdinalIgnoreCase))
                {
                    currentProviderIsCodex = IsPiCodexProvider(ReadString(root, "provider"));
                    currentModel = currentProviderIsCodex ? ReadString(root, "modelId") ?? ReadModel(root) : null;
                    continue;
                }

                if (!string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("message", out var message) ||
                    !string.Equals(ReadString(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var providerText = ReadString(message, "provider") ?? ReadString(root, "provider");
                var isCodex = providerText is null ? currentProviderIsCodex : IsPiCodexProvider(providerText);
                if (!isCodex)
                {
                    continue;
                }

                var at = ReadTimestamp(message) ?? ReadTimestamp(root);
                if (at is null || DateOnly.FromDateTime(at.Value.DateTime) < firstScanDay)
                {
                    continue;
                }

                var model = ReadString(message, "model")
                    ?? ReadString(message, "modelId")
                    ?? ReadString(root, "model")
                    ?? ReadString(root, "modelId")
                    ?? currentModel
                    ?? "Codex model";

                if (!message.TryGetProperty("usage", out var usage))
                {
                    continue;
                }

                var input = ReadLong(usage, "input", "inputTokens", "input_tokens", "promptTokens", "prompt_tokens");
                var cacheRead = ReadLong(usage, "cacheRead", "cacheReadTokens", "cache_read", "cache_read_tokens", "cacheReadInputTokens", "cache_read_input_tokens");
                var cacheWrite = ReadLong(usage, "cacheWrite", "cacheWriteTokens", "cache_write", "cache_write_tokens", "cacheCreationTokens", "cache_creation_tokens", "cacheCreationInputTokens", "cache_creation_input_tokens");
                var output = ReadLong(usage, "output", "outputTokens", "output_tokens", "completionTokens", "completion_tokens");
                var directTotal = ReadLong(usage, "totalTokens", "total_tokens", "tokenCount", "token_count", "tokens");
                var exactCost = ReadUsageCostUsd(usage);
                if (input == 0 && cacheRead == 0 && cacheWrite == 0 && output == 0 && directTotal == 0 && exactCost is null)
                {
                    continue;
                }

                var effectiveInput = Math.Max(input + cacheRead + cacheWrite, Math.Max(0, directTotal - output));
                var tokens = new TokenTotals(effectiveInput, Math.Min(cacheRead, effectiveInput), output);
                var isFastMode = IsFastMode(model, tokens, exactCost, root, message, usage);
                rows.Add(new CodexScanRow(at.Value, model, tokens, isFastMode, null, exactCost));
            }
            catch
            {
                // pi session logs may contain partial or future-format rows. Ignore only the bad row.
            }
        }

        return rows;
    }

    /// <summary>
    /// One usage entry extracted from a session file, kept file-shape-agnostic so a file's rows
    /// can be cached and replayed without re-parsing. <see cref="BaseFast"/> excludes fast-turn-id
    /// membership, which is re-evaluated on every apply against the current fast-turn set.
    /// </summary>
    private sealed record CodexScanRow(
        DateTimeOffset Timestamp,
        string Model,
        TokenTotals Tokens,
        bool BaseFast,
        string? TurnId,
        decimal? ExactCostUsd)
    {
        /// <summary>
        /// The calendar day the log wrote, unconverted — identical to the DateOnly this row used to
        /// carry. Ignored by the serializer: it is derivable, and the cache pays for every byte.
        /// </summary>
        [JsonIgnore]
        public DateOnly Day => DateOnly.FromDateTime(Timestamp.DateTime);
    }

    private sealed record CachedFileRows(
        long Length,
        long LastWriteUtcTicks,
        long CreationUtcTicks,
        DateOnly FirstScanDay,
        IReadOnlyList<CodexScanRow> Rows);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedFileRows> FileRowsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private const string CacheProviderName = "codex";
    private static int persistedCacheLoaded;

    /// <summary>Set whenever a file is actually (re)scanned, so an unchanged refresh skips the write.</summary>
    private static int cacheDirty;

    /// <summary>Seeds the in-memory cache from disk once per process, on the first history read.</summary>
    private void EnsurePersistedCacheLoaded()
    {
        if (!persistCache || Interlocked.Exchange(ref persistedCacheLoaded, 1) == 1)
        {
            return;
        }

        var entries = UsageScanCache.TryLoad<CodexScanRow>(CacheProviderName);
        if (entries is null)
        {
            return;
        }

        foreach (var (path, entry) in entries)
        {
            FileRowsCache.TryAdd(
                path,
                new CachedFileRows(entry.Length, entry.LastWriteUtcTicks, entry.CreationUtcTicks, entry.FirstScanDay, entry.Rows));
        }
    }

    /// <summary>
    /// Writes the current cache to disk, keeping only files that still exist and still carry rows
    /// inside the reported window.
    /// </summary>
    /// <remarks>
    /// pi files are deliberately NOT persisted. A pi row's <c>BaseFast</c> is decided by
    /// <see cref="IsFastMode"/>, which falls through to a pricing-dependent cost heuristic, and pi
    /// rows carry no turn id — so unlike Codex rows, replay cannot re-derive it. Persisting one
    /// would freeze a classification made under whatever pricing table happened to be loaded at
    /// scan time. pi corpora are small, so rescanning them each launch costs almost nothing.
    /// </remarks>
    private void SavePersistedCache(DateOnly firstReportDay, IReadOnlySet<string> piPaths)
    {
        // Only the default-rooted reader owns the shared cache file. A reader pointed at a custom
        // root (tests, fixtures) scans a different corpus entirely and must not overwrite it.
        if (!persistCache || Interlocked.Exchange(ref cacheDirty, 0) == 0)
        {
            return;
        }

        var entries = new Dictionary<string, UsageScanCacheEntry<CodexScanRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, cached) in FileRowsCache)
        {
            if (piPaths.Contains(path) || !File.Exists(path))
            {
                continue;
            }

            // Rows are trimmed to the report window, but entries with no rows are KEPT: an empty
            // result is an expensive thing to re-derive (see PruneFileRowsCache).
            var rows = cached.Rows.Where(row => row.Day >= firstReportDay).ToArray();
            entries[path] = new UsageScanCacheEntry<CodexScanRow>(
                cached.Length,
                cached.LastWriteUtcTicks,
                cached.CreationUtcTicks,
                // Clamp so a reloaded entry never claims coverage for days whose rows were just
                // trimmed away. Safe today because aggregation filters to firstReportDay, but the
                // entry should be self-consistent rather than depend on that invariant holding.
                cached.FirstScanDay > firstReportDay ? cached.FirstScanDay : firstReportDay,
                rows);
        }

        UsageScanCache.TrySave(CacheProviderName, entries);
    }

    private static IReadOnlyList<CodexScanRow> GetOrScanFileRows(string file, DateOnly firstScanDay, bool isPiFile)
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
            return isPiFile ? ScanPiFile(file, firstScanDay) : ScanCodexFile(file, firstScanDay);
        }

        // A cached scan taken with an earlier lookback window is still valid: rows older than the
        // current window were already excluded then, and days only move forward.
        // Note Length carries this check for Codex rollouts: Codex holds the rollout handle open,
        // so NTFS leaves LastWriteTimeUtc frozen while the file grows. Never treat an unchanged
        // write time as "unchanged file" here.
        if (FileRowsCache.TryGetValue(file, out var cached) &&
            cached.Length == length &&
            cached.LastWriteUtcTicks == lastWriteUtcTicks &&
            cached.CreationUtcTicks == creationUtcTicks &&
            cached.FirstScanDay <= firstScanDay)
        {
            return cached.Rows;
        }

        var rows = isPiFile ? ScanPiFile(file, firstScanDay) : ScanCodexFile(file, firstScanDay);
        FileRowsCache[file] = new CachedFileRows(length, lastWriteUtcTicks, creationUtcTicks, firstScanDay, rows);
        Interlocked.Exchange(ref cacheDirty, 1);
        return rows;
    }

    private static void PruneFileRowsCache(IReadOnlySet<string> scannedPaths)
    {
        // Evicting only deleted paths did not bound anything: session logs stay on disk long
        // after they age out of the scan window, so their entries were retained for the life of
        // the process. Retaining exactly the files enumerated this pass is both exact and free —
        // enumeration already applies the mtime window — and needs no extra stat calls.
        //
        // Crucially this keeps entries that produced NO rows. Those are the most valuable ones:
        // a rollout classified SuppressWholeFile returns an empty list only after up to three
        // full passes over the largest files in the corpus, so dropping it would re-parse the
        // most expensive file in the set on every refresh.
        foreach (var path in FileRowsCache.Keys)
        {
            if (!scannedPaths.Contains(path))
            {
                FileRowsCache.TryRemove(path, out _);
            }
        }
    }

    /// <summary>
    /// Collects the ledger's view of a scan alongside the reader's own aggregation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fed at the APPLY site, not from the per-file cache: forks and subagent rollouts are
    /// suppressed by the file classifier and the fast-turn set is resolved here, so anything
    /// upstream of this point would record tokens the flyout never counted.
    /// </para>
    /// <para>
    /// The threshold memo matters more than it looks: resolving one costs a model-name normalise
    /// (regex) plus a pricing lookup, and this runs for every in-window row of a 30-day scan.
    /// </para>
    /// </remarks>
    private sealed class CodexLedgerSink
    {
        private readonly Dictionary<string, int?> thresholds = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="builder">
        /// Supplied by the backfill, which owns one builder per worker thread. Null in the normal
        /// scan, where the sink owns the only builder there is.
        /// </param>
        public CodexLedgerSink(UsageLedgerBatchBuilder? builder = null)
        {
            Builder = builder ?? new UsageLedgerBatchBuilder(LedgerAccountingVersion);
        }

        public UsageLedgerBatchBuilder Builder { get; }

        public void Add(CodexScanRow row, bool isFastMode)
        {
            if (!thresholds.TryGetValue(row.Model, out var threshold))
            {
                threshold = CodexModelPricing.ThresholdTokensFor(row.Model);
                thresholds[row.Model] = threshold;
            }

            Builder.AddCodexRow(
                row.Timestamp,
                row.Model,
                row.Tokens.InputTokens,
                row.Tokens.CachedInputTokens,
                row.Tokens.OutputTokens,
                isFastMode,
                threshold,
                // pi rows carry a vendor-supplied dollar figure that no token count reproduces. The
                // ledger records the fact, never the money, so a reader reports the tokens and an
                // underivable cost instead of inventing one.
                vendorPriced: row.ExactCostUsd is not null);
        }
    }

    private static void ApplyRow(
        CodexScanRow row,
        DateOnly firstReportDay,
        IDictionary<DateOnly, MutableUsage> daily,
        IDictionary<string, MutableUsage> models,
        IReadOnlySet<string> fastTurnIds,
        CodexLedgerSink? ledger)
    {
        // Aggregate over the REPORTED window, not the scan window. Files are selected with a
        // wider lookback (ScanLookbackDays) so a file whose mtime is just outside the window can
        // still contribute in-window rows — but rows older than the chart's first day must not
        // reach the model breakdown, or it silently totals more than the chart it sits beside.
        if (row.Day < firstReportDay)
        {
            return;
        }

        var isFastMode = row.BaseFast || (row.TurnId is not null && fastTurnIds.Contains(row.TurnId));
        var categoryLabel = ModelBreakdownLabel(row.Model, isFastMode);
        Add(daily, row.Day, row.Model, row.Tokens, isFastMode, row.ExactCostUsd, categoryLabel);
        Add(models, ModelBreakdownKey(row.Model, isFastMode), row.Model, row.Tokens, isFastMode, row.ExactCostUsd, categoryLabel);
        ledger?.Add(row, isFastMode);
    }

    /// <summary>
    /// Scanner SEMANTICS version stamped on every batch. Bump it when an accounting rule changes,
    /// so days written by older logic are identifiable as suspect rather than silently mixed in.
    /// </summary>
    private const int LedgerAccountingVersion = 3;

    /// <summary>
    /// Hands the scan's records to the ledger and returns immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The covered-day declaration is what gives replace-by-scope its authority, and it is NOT the
    /// report window: rows are admitted on the calendar day the log spelled while records are keyed
    /// by the true UTC instant, so the local window straddles a UTC day at each end.
    /// <see cref="UsageLedgerBatchBuilder.CoverReportWindow"/> owns that arithmetic and claims only
    /// the UTC days this scan genuinely read in full. Rows that land outside it are still merged —
    /// per-key MAX, by the merge's authority rule — rather than licensing a deletion.
    /// </para>
    /// <para>
    /// The claim is only as honest as the ENUMERATION behind it — a day may be declared complete
    /// solely because every file that could contribute to it was walked. That is a property of
    /// <see cref="IsRelevantFile"/>, which is where it is argued; the two must be read together,
    /// because narrowing enumeration without narrowing this deletes the user's imported history.
    /// </para>
    /// <para>
    /// Off-thread on purpose. This runs inside the history scan (and therefore only while a window
    /// is open — property #1 is untouched), but the shard write is file I/O that the numbers on
    /// screen should not wait behind. The batch is immutable, so nothing is shared with the scan.
    /// </para>
    /// </remarks>
    private static void MergeIntoLedger(
        UsageLedgerBatchBuilder? builder,
        DateOnly firstReportDay,
        DateTimeOffset scannedAt,
        bool synchronous)
    {
        if (builder is null)
        {
            return;
        }

        builder.CoverReportWindow(firstReportDay, scannedAt);
        var batch = builder.Build(scannedAt);
        if (synchronous)
        {
            UsageLedger.TryMerge(UsageLedgerScope.Codex, batch);
            return;
        }

        _ = Task.Run(() => UsageLedger.TryMerge(UsageLedgerScope.Codex, batch));
    }

    /// <summary>
    /// The whole-corpus source used by <see cref="UsageLedgerBackfill"/>.
    /// </summary>
    /// <remarks>
    /// It reuses <see cref="ScanCodexFile"/>, <see cref="ScanPiFile"/> and <see cref="ApplyRow"/>'s
    /// fast-turn resolution verbatim, so a backfilled month is arithmetically the same thing the
    /// 30-day scan would have written had it been able to reach that month. What it deliberately
    /// does NOT touch is <see cref="FileRowsCache"/>: that cache is sized for a 32-day window and
    /// exists to make the next refresh cheap, and pushing years of rows through it would both blow
    /// the process's working set and hand the next <see cref="SavePersistedCache"/> a file orders of
    /// magnitude larger than it is designed to write.
    /// </remarks>
    internal static IUsageLedgerBackfillSource CreateBackfillSource() => new CodexBackfillSource();

    private sealed class CodexBackfillSource : IUsageLedgerBackfillSource
    {
        private readonly string codexHome = ResolveCodexHome();
        private readonly string piSessionsRoot = ResolvePiSessionsRoot();

        /// <summary>
        /// Resolved once, up front, so every worker reads one immutable set. The fast-turn set comes
        /// from the Codex log databases, whose retained tail only reaches back days — so old rows
        /// simply are not classifiable as fast, exactly as a scan run at the time would have left
        /// them.
        /// </summary>
        private readonly IReadOnlySet<string> fastTurnIds;

        public CodexBackfillSource()
        {
            fastTurnIds = ReadFastTurnIdsFromCodexLogs(codexHome);
        }

        public UsageLedgerScope Scope => UsageLedgerScope.Codex;

        public string DisplayName => "Codex";

        public int AccountingVersion => LedgerAccountingVersion;

        public IReadOnlyList<UsageLedgerBackfillFile> EnumerateFiles()
        {
            var files = new List<UsageLedgerBackfillFile>();
            foreach (var root in new[] { Path.Combine(codexHome, "sessions"), Path.Combine(codexHome, "archived_sessions") })
            {
                Collect(root, isSecondary: false, files);
            }

            Collect(piSessionsRoot, isSecondary: true, files);

            // Oldest first so the progress label walks forward through the months rather than
            // jumping about the alphabet of session ids.
            files.Sort((left, right) =>
            {
                var byStamp = left.Stamp.CompareTo(right.Stamp);
                return byStamp != 0 ? byStamp : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            });

            return files;
        }

        public void Scan(UsageLedgerBackfillFile file, UsageLedgerBatchBuilder builder)
        {
            // DateOnly.MinValue: no lower bound at all. This is the entire point of the backfill —
            // the scan's 32-day floor is what puts the older months out of reach.
            var rows = file.IsSecondary
                ? ScanPiFile(file.Path, DateOnly.MinValue)
                : ScanCodexFile(file.Path, DateOnly.MinValue);

            var sink = new CodexLedgerSink(builder);
            foreach (var row in rows)
            {
                sink.Add(row, row.BaseFast || (row.TurnId is not null && fastTurnIds.Contains(row.TurnId)));
            }
        }

        private static void Collect(string root, bool isSecondary, List<UsageLedgerBackfillFile> files)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
            }
            catch
            {
                return;
            }

            foreach (var path in found)
            {
                files.Add(new UsageLedgerBackfillFile(path, StampFor(path), isSecondary));
            }
        }
    }

    /// <summary>
    /// The day a log file is ABOUT, for ordering and for the progress label only. Codex names its
    /// rollouts with the date, so the name is preferred; anything else falls back to the write time.
    /// </summary>
    internal static DateOnly StampFor(string path)
    {
        if (DayFromText(Path.GetFileName(path)) is { } fromName)
        {
            return fromName;
        }

        try
        {
            return DateOnly.FromDateTime(File.GetLastWriteTime(path));
        }
        catch
        {
            return DateOnly.MinValue;
        }
    }

    /// <summary>Bounded so a refresh leaves cores free for the rest of the machine.</summary>
    private static int ScanParallelism => Math.Clamp(Environment.ProcessorCount - 2, 1, 8);

    /// <summary>
    /// Scans every file in parallel and returns the rows in a deterministic order: Codex files
    /// first, then pi, each in the caller's order. Order matters because it feeds the accounting
    /// replay, so results are placed by index rather than appended as they complete.
    /// </summary>
    private IReadOnlyList<CodexScanRow> ScanFilesInParallel(
        IReadOnlyList<string> codexFiles,
        IReadOnlyList<string> piFiles,
        DateOnly firstScanDay)
    {
        var perFile = new IReadOnlyList<CodexScanRow>[codexFiles.Count + piFiles.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism };

        Parallel.For(0, perFile.Length, options, index =>
        {
            var isPiFile = index >= codexFiles.Count;
            var path = isPiFile ? piFiles[index - codexFiles.Count] : codexFiles[index];
            perFile[index] = GetOrScanFileRows(path, firstScanDay, isPiFile);
        });

        var total = 0;
        foreach (var rows in perFile)
        {
            total += rows.Count;
        }

        var combined = new List<CodexScanRow>(total);
        foreach (var rows in perFile)
        {
            combined.AddRange(rows);
        }

        return combined;
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

    /// <summary>
    /// Shape of one Codex rollout file, decided before any tokens are counted.
    ///
    /// Subagent and forked rollouts replay their ancestor's entire token_count history into their
    /// own file. Counting those rows again multiplies usage by the size of the agent tree, so the
    /// replayed prefix is skipped and only the suffix this rollout actually owns is accounted.
    /// </summary>
    private readonly record struct CodexRolloutShape(
        bool SuppressWholeFile,
        int OwnedSuffixStartLine,
        TokenTotals? OwnedSuffixBaseline,
        bool PrefersTotalsAccounting)
    {
        public static CodexRolloutShape Plain(bool hasForkParent)
        {
            return new CodexRolloutShape(false, 0, null, hasForkParent);
        }
    }

    private enum RolloutObservationKind
    {
        SessionMeta,
        TurnContext,
        InterAgentCommunication,
        TokenCount
    }

    private readonly record struct RolloutObservation(
        int LineIndex,
        RolloutObservationKind Kind,
        string? SessionId,
        string? ForkParentId,
        bool TriggerTurn,
        TokenTotals? Total,
        TokenTotals? Last);

    private static CodexRolloutShape ClassifyCodexRollout(string file)
    {
        // Only rollouts carrying more than one session_meta can hold a copied prefix, and they are
        // the minority. Everything else skips the full observation pass.
        var (forkParentId, mayHaveCopiedPrefix) = ReadRolloutIdentity(file);
        if (!mayHaveCopiedPrefix)
        {
            return CodexRolloutShape.Plain(forkParentId is not null);
        }

        var observations = new List<RolloutObservation>();
        var lineIndex = -1;

        foreach (var line in ReadSharedLines(file))
        {
            lineIndex++;
            if (string.IsNullOrWhiteSpace(line) ||
                (!line.Contains("\"session_meta\"", StringComparison.Ordinal) &&
                 !line.Contains("\"turn_context\"", StringComparison.Ordinal) &&
                 !line.Contains("inter_agent_communication_metadata", StringComparison.Ordinal) &&
                 !line.Contains("\"token_count\"", StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var hasPayload = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object;
                switch (typeElement.GetString())
                {
                    case "session_meta":
                        observations.Add(new RolloutObservation(
                            lineIndex,
                            RolloutObservationKind.SessionMeta,
                            ReadSessionId(root, hasPayload ? payload : default),
                            hasPayload ? ReadForkParentId(payload) : null,
                            false,
                            null,
                            null));
                        break;

                    case "turn_context":
                        observations.Add(new RolloutObservation(lineIndex, RolloutObservationKind.TurnContext, null, null, false, null, null));
                        break;

                    case "inter_agent_communication_metadata":
                        observations.Add(new RolloutObservation(
                            lineIndex,
                            RolloutObservationKind.InterAgentCommunication,
                            null,
                            null,
                            hasPayload && payload.TryGetProperty("trigger_turn", out var trigger) && trigger.ValueKind == JsonValueKind.True,
                            null,
                            null));
                        break;

                    case "event_msg":
                        if (!hasPayload ||
                            !payload.TryGetProperty("type", out var payloadType) ||
                            !string.Equals(payloadType.GetString(), "token_count", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        var hasInfo = payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object;
                        observations.Add(new RolloutObservation(
                            lineIndex,
                            RolloutObservationKind.TokenCount,
                            null,
                            null,
                            false,
                            hasInfo && info.TryGetProperty("total_token_usage", out var total) ? ReadTotals(total) : null,
                            hasInfo && info.TryGetProperty("last_token_usage", out var last) ? ReadTotals(last) : null));
                        break;
                }
            }
            catch
            {
                // Classification is best effort. A malformed row cannot change file identity.
            }
        }

        return ClassifyObservations(observations);
    }

    private static (string? ForkParentId, bool MayHaveCopiedPrefix) ReadRolloutIdentity(string file)
    {
        string? forkParentId = null;
        var seenSessionMeta = false;

        foreach (var line in ReadSharedLines(file))
        {
            if (!line.Contains("\"session_meta\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (seenSessionMeta)
            {
                return (forkParentId, true);
            }

            seenSessionMeta = true;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var typeElement) &&
                    string.Equals(typeElement.GetString(), "session_meta", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("payload", out var payload) &&
                    payload.ValueKind == JsonValueKind.Object)
                {
                    forkParentId = ReadForkParentId(payload);
                }
                else
                {
                    seenSessionMeta = false;
                }
            }
            catch
            {
                seenSessionMeta = false;
            }
        }

        return (forkParentId, false);
    }

    private static CodexRolloutShape ClassifyObservations(IReadOnlyList<RolloutObservation> observations)
    {
        string? leafSessionId = null;
        string? forkParentId = null;
        var capturedLeaf = false;
        var hasEmbeddedAncestor = false;

        foreach (var observation in observations)
        {
            if (observation.Kind != RolloutObservationKind.SessionMeta)
            {
                continue;
            }

            if (!capturedLeaf)
            {
                capturedLeaf = true;
                leafSessionId = observation.SessionId;
                forkParentId = observation.ForkParentId;
                continue;
            }

            // Embedded ancestor metadata is proof on its own that this file carries a copied prefix.
            if (!SameSessionId(observation.SessionId, leafSessionId))
            {
                hasEmbeddedAncestor = true;
            }
        }

        if (!hasEmbeddedAncestor)
        {
            return CodexRolloutShape.Plain(forkParentId is not null);
        }

        TokenTotals? lastRawTotals = null;
        (int Line, TokenTotals? Baseline)? pendingTurnContext = null;
        var ownedSuffixStartLine = -1;
        TokenTotals? ownedSuffixBaseline = null;
        var inspectedOwnedSuffixFirstTotal = false;
        var observedAuthoritativeMetadata = false;

        foreach (var observation in observations)
        {
            switch (observation.Kind)
            {
                case RolloutObservationKind.SessionMeta:
                    // A later ancestor meta proves any earlier candidate boundary was itself replay.
                    if (observedAuthoritativeMetadata && !SameSessionId(observation.SessionId, leafSessionId))
                    {
                        ownedSuffixStartLine = -1;
                        ownedSuffixBaseline = null;
                        inspectedOwnedSuffixFirstTotal = false;
                    }

                    observedAuthoritativeMetadata = true;
                    pendingTurnContext = null;
                    break;

                case RolloutObservationKind.TurnContext:
                    pendingTurnContext = (observation.LineIndex, lastRawTotals);
                    break;

                case RolloutObservationKind.InterAgentCommunication:
                    // The rollout starts owning its turns at the first turn context that is
                    // immediately followed by an inter-agent trigger turn.
                    if (ownedSuffixStartLine < 0 &&
                        observation.TriggerTurn &&
                        pendingTurnContext is { } pending &&
                        observation.LineIndex == pending.Line + 1)
                    {
                        ownedSuffixStartLine = pending.Line;
                        ownedSuffixBaseline = pending.Baseline;
                        inspectedOwnedSuffixFirstTotal = false;
                    }

                    pendingTurnContext = null;
                    break;

                case RolloutObservationKind.TokenCount:
                    if (!inspectedOwnedSuffixFirstTotal && ownedSuffixStartLine >= 0 && observation.Total is { } firstTotal)
                    {
                        inspectedOwnedSuffixFirstTotal = true;
                        // A rollout that copies history and then restarts its own counter reports
                        // total == last on its first owned row, below the inherited baseline.
                        if (observation.Last is { } firstLast &&
                            firstLast == firstTotal &&
                            !firstTotal.AtLeast(ownedSuffixBaseline ?? default))
                        {
                            ownedSuffixBaseline = default(TokenTotals);
                        }
                    }

                    if (observation.Total is { } observedTotal)
                    {
                        lastRawTotals = observedTotal;
                    }

                    pendingTurnContext = null;
                    break;
            }
        }

        if (ownedSuffixStartLine >= 0)
        {
            return new CodexRolloutShape(false, ownedSuffixStartLine, ownedSuffixBaseline, PrefersTotalsAccounting: true);
        }

        // A copied prefix with no owned suffix and no declared parent is pure replay of another
        // rollout that is scanned in its own right.
        return new CodexRolloutShape(forkParentId is null, 0, null, PrefersTotalsAccounting: forkParentId is not null);
    }

    private static bool SameSessionId(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadSessionId(JsonElement root, JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object)
        {
            var fromPayload = ReadString(payload, "id")
                ?? ReadString(payload, "session_id")
                ?? ReadString(payload, "sessionId");
            if (fromPayload is not null)
            {
                return fromPayload;
            }
        }

        return ReadString(root, "id") ?? ReadString(root, "session_id") ?? ReadString(root, "sessionId");
    }

    private static string? ReadForkParentId(JsonElement payload)
    {
        return ReadString(payload, "forked_from_id")
            ?? ReadString(payload, "forkedFromId")
            ?? ReadString(payload, "parent_thread_id")
            ?? ReadString(payload, "parentThreadId");
    }

    /// <summary>
    /// Turns the cumulative counters in a Codex rollout into per-row usage deltas.
    ///
    /// Codex re-emits cumulative snapshots (resumes, compaction, interleaved subagent lineages),
    /// so a running sum of <c>last_token_usage</c> overcounts. Exact re-emissions are dropped and a
    /// monotonic watermark caps every delta so a lineage flip cannot re-count the same tokens.
    /// </summary>
    private sealed class CodexTotalsAccountant
    {
        private const int SeenRawTotalsLimit = 64;

        private readonly bool prefersTotalsAccounting;
        private readonly List<TokenTotals> seenRawTotals = [];
        private TokenTotals? watermark;
        private TokenTotals? countedTotals;
        private TokenTotals? rawTotalsBaseline;
        private bool sawDivergentTotals;
        private bool sawInterleavedTotals;

        public CodexTotalsAccountant(TokenTotals? baseline, bool prefersTotalsAccounting)
        {
            this.prefersTotalsAccounting = prefersTotalsAccounting;
            watermark = baseline;
            rawTotalsBaseline = baseline;
        }

        public bool TryNextDelta(JsonElement payload, out TokenTotals delta)
        {
            delta = default;
            if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            TokenTotals? total = info.TryGetProperty("total_token_usage", out var totalUsage) ? ReadTotals(totalUsage) : null;
            TokenTotals? last = info.TryGetProperty("last_token_usage", out var lastUsage) ? ReadTotals(lastUsage) : null;
            if (total is null && last is null)
            {
                return false;
            }

            if (total is { } observed)
            {
                if (seenRawTotals.Contains(observed))
                {
                    return false;
                }

                LatchIfBelowWatermark(observed);
            }

            var baseline = watermark ?? rawTotalsBaseline;
            var resolved = default(TokenTotals);

            if (last is { } lastDelta && !(prefersTotalsAccounting && total is not null))
            {
                if (total is { } current)
                {
                    resolved = lastDelta;
                    if (sawInterleavedTotals)
                    {
                        resolved = TokenTotals.Min(lastDelta, ContainedDelta(baseline, countedTotals, current));
                    }
                    else
                    {
                        var totalDelta = current.SubtractFloor(baseline ?? default);
                        if (ShouldPreferTotalDelta(baseline, current, totalDelta, lastDelta))
                        {
                            resolved = totalDelta;
                        }
                    }

                    Commit(resolved, current);
                }
                else
                {
                    resolved = lastDelta;
                    countedTotals = (countedTotals ?? default).Add(resolved);
                    rawTotalsBaseline = countedTotals;
                    watermark = TokenTotals.Max(watermark, countedTotals.Value);
                }
            }
            else if (total is { } currentTotals)
            {
                resolved = TotalsDerivedDelta(baseline, currentTotals);
                Commit(resolved, currentTotals);
            }

            if (total is { } committed)
            {
                CommitObserved(committed);
            }

            delta = resolved.WithCachedInputClamped();
            return delta.InputTokens > 0 || delta.CachedInputTokens > 0 || delta.OutputTokens > 0;
        }

        private void Commit(TokenTotals delta, TokenTotals rawBaseline)
        {
            countedTotals = (countedTotals ?? default).Add(delta);
            rawTotalsBaseline = rawBaseline;
            if (rawBaseline != countedTotals.Value)
            {
                sawDivergentTotals = true;
            }
        }

        private TokenTotals TotalsDerivedDelta(TokenTotals? baseline, TokenTotals current)
        {
            if (sawInterleavedTotals)
            {
                return ContainedDelta(baseline, countedTotals, current);
            }

            if (sawDivergentTotals)
            {
                return DivergentDelta(baseline, countedTotals, current);
            }

            return current.SubtractFloor(baseline ?? default);
        }

        private bool ShouldPreferTotalDelta(TokenTotals? baseline, TokenTotals current, TokenTotals totalDelta, TokenTotals lastDelta)
        {
            return !sawDivergentTotals &&
                baseline is { } raw &&
                current.AtLeast(raw) &&
                totalDelta.AtMost(lastDelta);
        }

        /// <summary>
        /// Advances from the counted baseline when the counter dropped (a resumed lineage), and
        /// from the watermark otherwise, so a lineage flip cannot re-count the gap between them.
        /// </summary>
        private static TokenTotals ContainedDelta(TokenTotals? watermark, TokenTotals? counted, TokenTotals current)
        {
            var water = watermark ?? default;
            var seen = counted ?? default;
            static long Component(long water, long counted, long current)
            {
                return current >= water ? Math.Max(0, current - Math.Max(water, counted)) : Math.Max(0, current - counted);
            }

            return new TokenTotals(
                Component(water.InputTokens, seen.InputTokens, current.InputTokens),
                Component(water.CachedInputTokens, seen.CachedInputTokens, current.CachedInputTokens),
                Component(water.OutputTokens, seen.OutputTokens, current.OutputTokens));
        }

        private static TokenTotals DivergentDelta(TokenTotals? rawBaseline, TokenTotals? counted, TokenTotals current)
        {
            var raw = rawBaseline ?? default;
            var seen = counted ?? default;
            static long Component(long raw, long counted, long current)
            {
                return current >= raw ? Math.Max(0, current - raw) : Math.Max(0, current - counted);
            }

            return new TokenTotals(
                Component(raw.InputTokens, seen.InputTokens, current.InputTokens),
                Component(raw.CachedInputTokens, seen.CachedInputTokens, current.CachedInputTokens),
                Component(raw.OutputTokens, seen.OutputTokens, current.OutputTokens));
        }

        private void LatchIfBelowWatermark(TokenTotals totals)
        {
            if (watermark is not { } water)
            {
                return;
            }

            // A monotonic counter cannot decrease: a drop means a second lineage or a reset, and
            // gap-sized totals deltas can no longer be trusted.
            if (totals.InputTokens < water.InputTokens ||
                totals.CachedInputTokens < water.CachedInputTokens ||
                totals.OutputTokens < water.OutputTokens)
            {
                sawInterleavedTotals = true;
            }
        }

        private void CommitObserved(TokenTotals totals)
        {
            watermark = TokenTotals.Max(watermark, totals);
            if (seenRawTotals.Contains(totals))
            {
                return;
            }

            seenRawTotals.Add(totals);
            if (seenRawTotals.Count > SeenRawTotalsLimit)
            {
                seenRawTotals.RemoveRange(0, seenRawTotals.Count - SeenRawTotalsLimit);
            }
        }
    }

    private static TokenTotals ReadTotals(JsonElement element)
    {
        return new TokenTotals(
            ReadLong(element, "input_tokens"),
            ReadLong(element, "cached_input_tokens", "cache_read_input_tokens"),
            ReadLong(element, "output_tokens"));
    }

    private static long ReadLong(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return Math.Max(0, number);
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            {
                return Math.Max(0, parsed);
            }
        }

        return 0;
    }

    private static decimal? ReadUsageCostUsd(JsonElement usage)
    {
        if (!usage.TryGetProperty("cost", out var cost))
        {
            return null;
        }

        if (cost.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            return ReadDecimal(cost);
        }

        if (cost.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[] { "total", "totalUsd", "totalUSD", "usd", "costUsd", "costUSD" })
        {
            if (cost.TryGetProperty(propertyName, out var value) && ReadDecimal(value) is { } amount)
            {
                return amount;
            }
        }

        var input = ReadCostPart(cost, "input");
        var output = ReadCostPart(cost, "output");
        var cacheRead = ReadCostPart(cost, "cacheRead", "cache_read");
        var cacheWrite = ReadCostPart(cost, "cacheWrite", "cache_write", "cacheCreation", "cache_creation");
        var sum = input + output + cacheRead + cacheWrite;
        return sum > 0 ? sum : null;
    }

    private static decimal ReadCostPart(JsonElement cost, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (cost.TryGetProperty(propertyName, out var value) && ReadDecimal(value) is { } amount)
            {
                return amount;
            }
        }

        return 0;
    }

    private static decimal? ReadDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return Math.Max(0, number);
        }

        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Max(0, parsed);
        }

        return null;
    }

    private static string? ReadModel(JsonElement element)
    {
        if (element.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
        {
            return modelElement.GetString();
        }

        if (element.TryGetProperty("model_name", out var modelNameElement) && modelNameElement.ValueKind == JsonValueKind.String)
        {
            return modelNameElement.GetString();
        }

        if (element.TryGetProperty("payload", out var payload))
        {
            return ReadModel(payload);
        }

        if (element.TryGetProperty("info", out var info))
        {
            return ReadModel(info);
        }

        return null;
    }

    /// <summary>
    /// The hour a session line belongs to, in the frame the line was written in.
    /// </summary>
    /// <remarks>
    /// The DATE this yields is bit-for-bit the one the old day-only path produced — the calendar
    /// date as the log spells it, not a converted one — because the reported day buckets, and every
    /// figure derived from them, must not move just because rows grew an hour column. The offset is
    /// carried alongside purely so the ledger can recover the true instant.
    /// </remarks>
    private static DateTimeOffset? ReadTimestamp(JsonElement element)
    {
        if (!element.TryGetProperty("timestamp", out var timestampElement))
        {
            return null;
        }

        return timestampElement.ValueKind switch
        {
            JsonValueKind.String => TimestampFromText(timestampElement.GetString()),
            JsonValueKind.Number when timestampElement.TryGetInt64(out var raw) => UnixTimestampToLocalHour(raw),
            _ => null
        };
    }

    /// <summary>Numeric timestamps were already interpreted in LOCAL time; keep that exactly.</summary>
    /// <remarks>
    /// The unit guess (seconds vs milliseconds) is exactly the kind of thing a future log format
    /// breaks — microseconds or nanoseconds would land centuries out — so the result goes through
    /// the same plausibility gate the text path uses. A garbage epoch is dropped here rather than
    /// allowed to name a year shard; see <see cref="UsageTimestampText.EarliestPlausibleDay"/>.
    /// </remarks>
    private static DateTimeOffset? UnixTimestampToLocalHour(long raw)
    {
        var timestamp = (raw > 1_000_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
            : DateTimeOffset.FromUnixTimeSeconds(raw)).ToLocalTime();
        var hour = new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, 0, 0, timestamp.Offset);
        return UsageTimestampText.IsPlausibleDay(DateOnly.FromDateTime(hour.DateTime)) ? hour : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsPiCodexProvider(string? provider)
    {
        return string.Equals(provider, "openai-codex", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadTurnId(JsonElement element)
    {
        if (element.TryGetProperty("turn_id", out var turnId) && turnId.ValueKind == JsonValueKind.String)
        {
            return turnId.GetString();
        }

        if (element.TryGetProperty("payload", out var payload))
        {
            return ReadTurnId(payload);
        }

        return null;
    }

    private static IReadOnlySet<string> ReadFastTurnIdsFromCodexLogs(string codexHome)
    {
        var files = EnumerateCodexLogFiles(codexHome).ToArray();
        var signature = string.Join(
            "|",
            files.Select(file =>
            {
                try
                {
                    var info = new FileInfo(file);
                    return string.Create(CultureInfo.InvariantCulture, $"{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
                }
                catch
                {
                    return file;
                }
            }));

        lock (FastTurnIdsCacheLock)
        {
            if (string.Equals(signature, cachedFastTurnIdsSignature, StringComparison.Ordinal))
            {
                return cachedFastTurnIds;
            }
        }

        var turnIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            AddFastTurnIdsFromCodexLog(file, turnIds);
        }

        lock (FastTurnIdsCacheLock)
        {
            cachedFastTurnIdsSignature = signature;
            cachedFastTurnIds = turnIds;
            return cachedFastTurnIds;
        }
    }

    private static IEnumerable<string> EnumerateCodexLogFiles(string codexHome)
    {
        if (!Directory.Exists(codexHome))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(codexHome, "logs_*.sqlite*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (file.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".sqlite-wal", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static void AddFastTurnIdsFromCodexLog(string file, ISet<string> turnIds)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = Math.Max(0, stream.Length - MaxCodexLogScanBytes);

            var buffer = new byte[CodexLogChunkBytes];
            var carry = string.Empty;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var text = carry + Encoding.UTF8.GetString(buffer, 0, read);
                AddFastTurnIdsFromText(text, turnIds);
                carry = text.Length > CodexLogOverlapChars ? text[^CodexLogOverlapChars..] : text;
            }
        }
        catch
        {
            // Codex log databases are best-effort enrichment. Session token rows remain usable without them.
        }
    }

    private static void AddFastTurnIdsFromText(string text, ISet<string> turnIds)
    {
        var searchIndex = 0;
        while (TryFindFastServiceTierMarker(text, searchIndex, out var markerIndex))
        {
            searchIndex = markerIndex + 1;
            AddPreviousFastTurnId(text, markerIndex, turnIds);
            AddMetadataFastTurnIds(text, markerIndex, turnIds);
        }
    }

    private static void AddPreviousFastTurnId(string text, int markerIndex, ISet<string> turnIds)
    {
        var searchStart = Math.Max(0, markerIndex - CodexLogTurnIdBacktrackChars);
        var searchLength = markerIndex - searchStart;
        var turnMarkerIndex = text.LastIndexOf("turn.id=", markerIndex, searchLength, StringComparison.OrdinalIgnoreCase);
        if (turnMarkerIndex < 0)
        {
            return;
        }

        var turnIdStart = turnMarkerIndex + "turn.id=".Length;
        if (TryReadTurnId(text, turnIdStart, out var turnId))
        {
            turnIds.Add(turnId);
        }
    }

    private static void AddMetadataFastTurnIds(string text, int markerIndex, ISet<string> turnIds)
    {
        var searchEnd = Math.Min(text.Length, markerIndex + CodexLogTurnMetadataForwardChars);
        foreach (var marker in TurnIdValueMarkers)
        {
            var searchIndex = markerIndex;
            while (searchIndex < searchEnd)
            {
                var turnMarkerIndex = text.IndexOf(marker, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (turnMarkerIndex < 0 || turnMarkerIndex >= searchEnd)
                {
                    break;
                }

                var turnIdStart = turnMarkerIndex + marker.Length;
                if (TryReadTurnId(text, turnIdStart, out var turnId))
                {
                    turnIds.Add(turnId);
                }

                searchIndex = turnMarkerIndex + marker.Length;
            }
        }
    }

    private static bool TryFindFastServiceTierMarker(string text, int startIndex, out int markerIndex)
    {
        markerIndex = -1;
        foreach (var marker in FastServiceTierMarkers)
        {
            var candidate = text.IndexOf(marker, startIndex, StringComparison.OrdinalIgnoreCase);
            if (candidate >= 0 && (markerIndex < 0 || candidate < markerIndex))
            {
                markerIndex = candidate;
            }
        }

        return markerIndex >= 0;
    }

    private static bool TryReadTurnId(string text, int startIndex, out string turnId)
    {
        turnId = string.Empty;
        const int turnIdLength = 36;
        if (startIndex < 0 || startIndex + turnIdLength > text.Length)
        {
            return false;
        }

        var candidate = text.Substring(startIndex, turnIdLength);
        if (!Guid.TryParse(candidate, out _))
        {
            return false;
        }

        turnId = candidate;
        return true;
    }

    private static bool IsFastMode(string model, TokenTotals tokens, decimal? exactCostUsd, params JsonElement[] elements)
    {
        if (elements.Any(HasFastModeMarker))
        {
            return true;
        }

        if (exactCostUsd is not { } actualCost || actualCost <= 0)
        {
            return false;
        }

        if (EstimatePriorityCost(model, tokens) is not { } priorityCost)
        {
            return false;
        }

        var normalCost = EstimateCost(model, tokens);
        return actualCost > normalCost * 1.2m && CostsAreClose(actualCost, priorityCost);
    }

    private static bool HasFastModeMarker(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in new[] { "mode", "tier", "serviceTier", "service_tier", "speedTier", "speed_tier", "plan_type", "priority", "fast", "limit_id", "limit_name" })
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return propertyName.Contains("priority", StringComparison.OrdinalIgnoreCase) || propertyName.Contains("fast", StringComparison.OrdinalIgnoreCase);
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (text is not null && IsFastMarkerText(propertyName, text))
                {
                    return true;
                }
            }
        }

        foreach (var propertyName in new[] { "payload", "rate_limits", "collaboration_mode", "settings" })
        {
            if (element.TryGetProperty(propertyName, out var value) && HasFastModeMarker(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFastMarkerText(string propertyName, string text)
    {
        if (text.Contains("fast", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("priority", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (propertyName.Contains("limit", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(text, "premium", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool CostsAreClose(decimal left, decimal right)
    {
        var tolerance = Math.Max(0.000001m, Math.Abs(right) * 0.01m);
        return Math.Abs(left - right) <= tolerance;
    }

    private static DateOnly? DayFromText(string? value)
    {
        return UsageTimestampText.TryFindDate(value, out _, out var year, out var month, out var day) &&
            UsageTimestampText.TryMakeDate(year, month, day, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Truncates to the HOUR on purpose: the ledger's finest bucket is an hour, so minutes and
    /// seconds are parsed by nobody and would only inflate every cached row on disk.
    /// </summary>
    private static DateTimeOffset? TimestampFromText(string? value)
    {
        return UsageTimestampText.TryParseHour(value, out var timestamp) ? timestamp : null;
    }

    private static void Add(IDictionary<DateOnly, MutableUsage> daily, DateOnly day, string model, TokenTotals tokens, bool isFastMode = false, decimal? exactCostUsd = null, string? categoryLabel = null)
    {
        if (!daily.TryGetValue(day, out var usage))
        {
            usage = new MutableUsage();
            daily[day] = usage;
        }

        usage.Add(model, tokens, isFastMode, exactCostUsd, categoryLabel: categoryLabel ?? ModelBreakdownLabel(model, isFastMode));
    }

    private static void Add(IDictionary<string, MutableUsage> models, string key, string model, TokenTotals tokens, bool isFastMode = false, decimal? exactCostUsd = null, string? displayName = null)
    {
        if (!models.TryGetValue(key, out var usage))
        {
            usage = new MutableUsage();
            models[key] = usage;
        }

        usage.Add(model, tokens, isFastMode, exactCostUsd, displayName: displayName ?? model);
    }

    private static ProviderDailyUsage ToDaily(DateOnly day, MutableUsage usage)
    {
        return new ProviderDailyUsage(day, usage.InputTokens, usage.CachedInputTokens, 0, usage.OutputTokens, usage.EstimatedCostUsd, usage.FastEstimatedCostUsd, usage.SpendCategories);
    }

    private static ProviderModelUsage ToModel(string model, MutableUsage usage)
    {
        return new ProviderModelUsage(usage.DisplayName ?? model, usage.InputTokens, usage.CachedInputTokens, 0, usage.OutputTokens, usage.EstimatedCostUsd, usage.FastEstimatedCostUsd);
    }

    private static string ModelBreakdownKey(string model, bool isFastMode)
    {
        var normalized = CodexModelPricing.NormalizeModelName(model);
        return isFastMode ? normalized + "|fast" : normalized;
    }

    // Shared with the ledger read path so the same row carries the same label whichever source
    // answered; see CodexModelPricing.BreakdownLabel.
    private static string ModelBreakdownLabel(string model, bool isFastMode) => CodexModelPricing.BreakdownLabel(model, isFastMode);

    private static string ResolveCodexHome()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static string ResolvePiSessionsRoot()
    {
        var piHome = Environment.GetEnvironmentVariable("PI_HOME");
        if (!string.IsNullOrWhiteSpace(piHome))
        {
            return Path.Combine(piHome.Trim(), "agent", "sessions");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent", "sessions");
    }

    private readonly record struct TokenTotals(long InputTokens, long CachedInputTokens, long OutputTokens)
    {
        public TokenTotals Add(TokenTotals other)
        {
            return new TokenTotals(
                InputTokens + other.InputTokens,
                CachedInputTokens + other.CachedInputTokens,
                OutputTokens + other.OutputTokens);
        }

        public TokenTotals SubtractFloor(TokenTotals other)
        {
            return new TokenTotals(
                Math.Max(0, InputTokens - other.InputTokens),
                Math.Max(0, CachedInputTokens - other.CachedInputTokens),
                Math.Max(0, OutputTokens - other.OutputTokens));
        }

        public TokenTotals WithCachedInputClamped()
        {
            return this with { CachedInputTokens = Math.Min(CachedInputTokens, InputTokens) };
        }

        public bool AtLeast(TokenTotals other)
        {
            return InputTokens >= other.InputTokens &&
                CachedInputTokens >= other.CachedInputTokens &&
                OutputTokens >= other.OutputTokens;
        }

        public bool AtMost(TokenTotals other)
        {
            return InputTokens <= other.InputTokens &&
                CachedInputTokens <= other.CachedInputTokens &&
                OutputTokens <= other.OutputTokens;
        }

        public static TokenTotals Min(TokenTotals left, TokenTotals right)
        {
            return new TokenTotals(
                Math.Min(left.InputTokens, right.InputTokens),
                Math.Min(left.CachedInputTokens, right.CachedInputTokens),
                Math.Min(left.OutputTokens, right.OutputTokens));
        }

        public static TokenTotals Max(TokenTotals? left, TokenTotals right)
        {
            return left is not { } value
                ? right
                : new TokenTotals(
                    Math.Max(value.InputTokens, right.InputTokens),
                    Math.Max(value.CachedInputTokens, right.CachedInputTokens),
                    Math.Max(value.OutputTokens, right.OutputTokens));
        }
    }

    private sealed class MutableUsage
    {
        public long InputTokens { get; private set; }
        public long CachedInputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public decimal EstimatedCostUsd { get; private set; }
        public decimal FastEstimatedCostUsd { get; private set; }
        public string? DisplayName { get; private set; }
        private readonly Dictionary<string, decimal> spendCategories = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ProviderSpendCategory> SpendCategories => spendCategories
            .Select(pair => new ProviderSpendCategory(pair.Key, pair.Value))
            .OrderBy(category => category.Label.Contains(" fast", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        public void Add(string model, TokenTotals tokens, bool isFastMode = false, decimal? exactCostUsd = null, string? displayName = null, string? categoryLabel = null)
        {
            DisplayName ??= displayName;
            InputTokens += tokens.InputTokens;
            CachedInputTokens += tokens.CachedInputTokens;
            OutputTokens += tokens.OutputTokens;

            var cost = exactCostUsd ?? (isFastMode ? EstimatePriorityCost(model, tokens) ?? EstimateCost(model, tokens) : EstimateCost(model, tokens));
            EstimatedCostUsd += cost;
            if (isFastMode)
            {
                FastEstimatedCostUsd += cost;
            }

            if (cost > 0)
            {
                var label = categoryLabel ?? ModelBreakdownLabel(model, isFastMode);
                spendCategories[label] = spendCategories.TryGetValue(label, out var existing) ? existing + cost : cost;
            }
        }
    }

    // The rates and the arithmetic now live in CodexModelPricing, which the ledger read path also
    // consumes. These wrappers keep this file's call sites unchanged and, more importantly, keep
    // the tier DECISION in one place: a fast row over the priority ceiling has to fall back to base
    // rates here and in the ledger identically, or the same tokens price two ways.
    private static decimal EstimateCost(string model, TokenTotals tokens)
    {
        return CodexModelPricing.For(model) is { } pricing
            ? CodexModelPricing.Estimate(
                pricing,
                tokens.InputTokens,
                tokens.CachedInputTokens,
                tokens.OutputTokens,
                CodexModelPricing.TierFor(pricing, isFast: false, tokens.InputTokens))
            : 0m;
    }

    private static decimal? EstimatePriorityCost(string model, TokenTotals tokens)
    {
        if (CodexModelPricing.For(model) is not { } pricing ||
            CodexModelPricing.TierFor(pricing, isFast: true, tokens.InputTokens) != CodexRateTier.Priority)
        {
            return null;
        }

        return CodexModelPricing.Estimate(
            pricing,
            tokens.InputTokens,
            tokens.CachedInputTokens,
            tokens.OutputTokens,
            CodexRateTier.Priority);
    }

    private const int MaxCodexLogScanBytes = 64 * 1024 * 1024;
    private const int CodexLogChunkBytes = 1024 * 1024;
    private const int CodexLogTurnIdBacktrackChars = 1_200_000;
    private const int CodexLogTurnMetadataForwardChars = 80_000;
    private const int CodexLogOverlapChars = CodexLogTurnIdBacktrackChars;
    private static readonly string[] FastServiceTierMarkers = ["\"service_tier\":\"priority\"", "\"service_tier\":\"fast\""];
    private static readonly string[] TurnIdValueMarkers = ["\"turn_id\":\"", "\\\"turn_id\\\":\\\"", "\\u0022turn_id\\u0022:\\u0022"];
    private static readonly object FastTurnIdsCacheLock = new();
    private static string? cachedFastTurnIdsSignature;
    private static IReadOnlySet<string> cachedFastTurnIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
