using System.Globalization;
using System.Text.Json;

namespace CodexBarWindows;

/// <summary>
/// Builds local Grok token/cost history from session <c>updates.jsonl</c> ledgers under
/// <c>~/.grok/sessions</c>. Prefer server-stamped <c>costUsdTicks</c> when present; otherwise
/// estimate from models.dev / built-in Grok rates.
/// </summary>
public sealed class GrokUsageInsightsReader
{
    private const int DaysToReport = 30;
    private const int ScanLookbackDays = 32;
    private const int MaxFilesToScan = 1200;
    private const int MaxLineChars = 512 * 1024;
    /// <summary>Server stamp: 1 USD = 10^10 ticks (matches Grok headless spend fields).</summary>
    private const decimal CostTicksPerUsd = 10_000_000_000m;
    private readonly IReadOnlyList<string>? sessionsRoots;
    private readonly bool refreshModelsDevPricing;

    public GrokUsageInsightsReader()
        : this(null, refreshModelsDevPricing: true)
    {
    }

    public GrokUsageInsightsReader(IReadOnlyList<string> sessionsRoots)
        : this(sessionsRoots, refreshModelsDevPricing: true)
    {
    }

    public GrokUsageInsightsReader(IReadOnlyList<string>? sessionsRoots, bool refreshModelsDevPricing)
    {
        this.sessionsRoots = sessionsRoots;
        this.refreshModelsDevPricing = refreshModelsDevPricing;
    }

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
            var roots = ResolveSessionsRoots().ToArray();
            var files = roots
                .SelectMany(root => EnumerateUpdatesFiles(root, firstScanDay))
                .OrderByDescending(path =>
                {
                    try
                    {
                        return File.GetLastWriteTimeUtc(path);
                    }
                    catch
                    {
                        return DateTime.MinValue;
                    }
                })
                .Take(MaxFilesToScan)
                .ToArray();

            if (files.Length == 0)
            {
                return new ProviderUsageInsightsLookupResult(
                    EmptyInsights(now, firstReportDay),
                    $"No Grok session logs were found under {string.Join("; ", roots)}.");
            }

            var rows = new List<GrokUsageRow>();
            foreach (var file in files)
            {
                rows.AddRange(GetOrReadRowsFromFile(file, firstScanDay));
            }

            PruneFileRowsCache();

            var daily = new Dictionary<DateOnly, MutableUsage>();
            var models = new Dictionary<string, MutableUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Deduplicate(rows))
            {
                var label = NormalizeGrokModel(row.Model);
                Add(daily, row.Day, row.Model, row.Tokens, row.CostUsd, row.CostPriced, label);
                Add(models, label, row.Model, row.Tokens, row.CostUsd, row.CostPriced, label);
            }

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
            var hasIncompleteCost = dailyRows.Any(row => row.HasIncompleteCost) ||
                                    modelRows.Any(row => row.HasIncompleteCost);
            var result = new ProviderUsageInsights(
                now,
                "Local Grok sessions",
                dailyRows,
                modelRows,
                todayUsage.TotalTokens,
                todayUsage.EstimatedCostUsd,
                dailyRows.Sum(row => row.TotalTokens),
                dailyRows.Sum(row => row.EstimatedCostUsd),
                HasIncompleteCost: hasIncompleteCost);

            var warning = hasIncompleteCost
                ? "Some Grok models had no pricing; cost may be incomplete."
                : result.HasUsage
                    ? null
                    : "No token usage entries were found in recent Grok session logs.";
            return new ProviderUsageInsightsLookupResult(result, warning);
        }
        catch (Exception exception)
        {
            return new ProviderUsageInsightsLookupResult(
                null,
                $"Could not read Grok usage history: {exception.Message}");
        }
    }

    private static ProviderUsageInsights EmptyInsights(DateTimeOffset observedAt, DateOnly firstReportDay)
    {
        var daily = Enumerable.Range(0, DaysToReport)
            .Select(offset => new ProviderDailyUsage(firstReportDay.AddDays(offset), 0, 0, 0, 0, 0))
            .ToArray();
        return new ProviderUsageInsights(observedAt, "Local Grok sessions", daily, [], 0, 0, 0, 0);
    }

    private IEnumerable<string> ResolveSessionsRoots()
    {
        if (sessionsRoots is { Count: > 0 })
        {
            foreach (var root in sessionsRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                yield return root;
            }

            yield break;
        }

        var home = Environment.GetEnvironmentVariable("GROK_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, "sessions");
            yield break;
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".grok",
            "sessions");
    }

    private static IEnumerable<string> EnumerateUpdatesFiles(string root, DateOnly firstScanDay)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "updates.jsonl", SearchOption.AllDirectories);
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
        DateOnly FirstScanDay,
        IReadOnlyList<GrokUsageRow> Rows);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedFileRows> FileRowsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<GrokUsageRow> GetOrReadRowsFromFile(string file, DateOnly firstScanDay)
    {
        long length;
        long lastWriteUtcTicks;
        try
        {
            var info = new FileInfo(file);
            length = info.Length;
            lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return ReadRowsFromFile(file, firstScanDay);
        }

        if (FileRowsCache.TryGetValue(file, out var cached) &&
            cached.Length == length &&
            cached.LastWriteUtcTicks == lastWriteUtcTicks &&
            cached.FirstScanDay <= firstScanDay)
        {
            return cached.Rows.Select(RepriceRow).ToArray();
        }

        var rows = ReadRowsFromFile(file, firstScanDay);
        FileRowsCache[file] = new CachedFileRows(length, lastWriteUtcTicks, firstScanDay, rows);
        return rows;
    }

    private static GrokUsageRow RepriceRow(GrokUsageRow row)
    {
        if (row.ExactCostUsd is { } exact)
        {
            return row with { CostUsd = exact, CostPriced = true };
        }

        var rawInput = Math.Max(0, row.Tokens.InputTokens - row.Tokens.CachedInputTokens);
        var cost = EstimateCost(
            row.Model,
            rawInput,
            row.Tokens.CachedInputTokens,
            row.Tokens.CacheCreationTokens,
            row.Tokens.OutputTokens);
        return row with { CostUsd = cost, CostPriced = cost is not null };
    }

    private static void PruneFileRowsCache()
    {
        if (FileRowsCache.Count <= 2048)
        {
            return;
        }

        foreach (var path in FileRowsCache.Keys)
        {
            if (!File.Exists(path))
            {
                FileRowsCache.TryRemove(path, out _);
            }
        }
    }

    private static IReadOnlyList<GrokUsageRow> ReadRowsFromFile(string file, DateOnly firstScanDay)
    {
        var rows = new List<GrokUsageRow>();
        foreach (var line in ReadSharedLines(file))
        {
            if (string.IsNullOrWhiteSpace(line) ||
                line.Length > MaxLineChars ||
                !line.Contains("turn_completed", StringComparison.Ordinal) ||
                !line.Contains("usage", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!TryGetTurnUpdate(root, out var update) ||
                    !string.Equals(ReadString(update, "sessionUpdate"), "turn_completed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!update.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var day = ReadDay(root, update);
                if (day is null || day < firstScanDay)
                {
                    continue;
                }

                // prompt_id alone keys the dedup: it is a UUID minted per turn, so it is already
                // unique across sessions, and it is the SAME id when a session is relocated and its
                // log ends up under two paths - which is the case the dedup exists for. Adding the
                // session id would defeat exactly that, so the field is not read.
                var promptId = ReadString(update, "prompt_id") ?? ReadString(update, "promptId");

                if (usage.TryGetProperty("modelUsage", out var modelUsage) &&
                    modelUsage.ValueKind == JsonValueKind.Object &&
                    modelUsage.EnumerateObject().Any())
                {
                    foreach (var modelProperty in modelUsage.EnumerateObject())
                    {
                        if (TryBuildRow(
                                file,
                                day.Value,
                                modelProperty.Name,
                                promptId,
                                modelProperty.Value,
                                out var modelRow))
                        {
                            rows.Add(modelRow);
                        }
                    }

                    continue;
                }

                if (TryBuildRow(file, day.Value, "grok", promptId, usage, out var row))
                {
                    rows.Add(row);
                }
            }
            catch
            {
                // Session logs may contain partial or future-format rows.
            }
        }

        return rows;
    }

    private static bool TryGetTurnUpdate(JsonElement root, out JsonElement update)
    {
        update = default;
        if (root.TryGetProperty("params", out var parameters) &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("update", out var nested) &&
            nested.ValueKind == JsonValueKind.Object)
        {
            update = nested;
            return true;
        }

        if (root.TryGetProperty("update", out var direct) && direct.ValueKind == JsonValueKind.Object)
        {
            update = direct;
            return true;
        }

        return false;
    }

    private static bool TryBuildRow(
        string file,
        DateOnly day,
        string model,
        string? promptId,
        JsonElement usage,
        out GrokUsageRow row)
    {
        row = default!;
        var input = ReadLong(usage, "inputTokens", "input_tokens");
        var cacheRead = ReadLong(usage, "cachedReadTokens", "cache_read_input_tokens", "cacheReadInputTokens");
        var cacheCreate = ReadLong(usage, "cacheCreationTokens", "cache_creation_input_tokens", "cacheCreationInputTokens");
        var output = ReadLong(usage, "outputTokens", "output_tokens");
        // reasoningTokens is DELIBERATELY NOT ADDED. It is a breakdown of outputTokens, not a
        // sibling of it: every observed turn satisfies totalTokens == inputTokens + outputTokens
        // while reasoningTokens is strictly smaller than outputTokens (57/49, 81/73, 1621/467).
        // Folding it in double counted the thinking half of every turn - on a short answer that
        // is most of the output, so the charts and the fallback cost ran up to ~86% high.

        if (input == 0 && cacheRead == 0 && cacheCreate == 0 && output == 0)
        {
            return false;
        }

        // inputTokens on turn_completed is the full prompt (cache-inclusive).
        var totalInput = input > 0 ? input : cacheRead + Math.Max(0, input);
        if (cacheRead > totalInput)
        {
            totalInput = cacheRead;
        }

        var tokens = new TokenTotals(totalInput, cacheRead, cacheCreate, output);
        var exactCost = ReadCostUsd(usage);
        var rawInput = Math.Max(0, totalInput - cacheRead);
        var estimated = exactCost ?? EstimateCost(model, rawInput, cacheRead, cacheCreate, output);
        row = new GrokUsageRow(
            file,
            day,
            model,
            promptId,
            tokens,
            exactCost,
            estimated,
            estimated is not null);
        return true;
    }

    private static IEnumerable<GrokUsageRow> Deduplicate(IEnumerable<GrokUsageRow> rows)
    {
        var winners = new Dictionary<string, GrokUsageRow>(StringComparer.Ordinal);
        var unkeyed = new List<GrokUsageRow>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.PromptId))
            {
                unkeyed.Add(row);
                continue;
            }

            // Model-level rows from one turn share a prompt id; keep each model separately.
            var key = $"{row.PromptId}:{NormalizeGrokModel(row.Model)}";
            if (!winners.TryGetValue(key, out var existing) ||
                string.Compare(row.FilePath, existing.FilePath, StringComparison.OrdinalIgnoreCase) < 0)
            {
                winners[key] = row;
            }
        }

        return winners.Values.Concat(unkeyed);
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

    private static void Add(
        IDictionary<DateOnly, MutableUsage> daily,
        DateOnly day,
        string model,
        TokenTotals tokens,
        decimal? exactCostUsd,
        bool costPriced,
        string categoryLabel)
    {
        if (!daily.TryGetValue(day, out var usage))
        {
            usage = new MutableUsage();
            daily[day] = usage;
        }

        usage.Add(model, tokens, exactCostUsd, costPriced, categoryLabel, categoryLabel);
    }

    private static void Add(
        IDictionary<string, MutableUsage> models,
        string key,
        string model,
        TokenTotals tokens,
        decimal? exactCostUsd,
        bool costPriced,
        string displayName)
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
        return new ProviderDailyUsage(
            day,
            usage.InputTokens,
            usage.CachedInputTokens,
            usage.CacheCreationTokens,
            usage.OutputTokens,
            usage.EstimatedCostUsd,
            SpendCategories: usage.SpendCategories,
            HasIncompleteCost: usage.HasIncompleteCost);
    }

    private static ProviderModelUsage ToModel(string model, MutableUsage usage)
    {
        return new ProviderModelUsage(
            usage.DisplayName ?? model,
            usage.InputTokens,
            usage.CachedInputTokens,
            usage.CacheCreationTokens,
            usage.OutputTokens,
            usage.EstimatedCostUsd,
            HasIncompleteCost: usage.HasIncompleteCost);
    }

    private static decimal? EstimateCost(
        string model,
        long rawInput,
        long cacheRead,
        long cacheCreate,
        long output)
    {
        var pricing = ModelsDevPricing.Lookup("xai", model)
            ?? ModelsDevPricing.Lookup("x-ai", model)
            ?? BuiltInPricingFor(model);
        if (pricing is null)
        {
            return null;
        }

        return (rawInput / 1_000_000m * pricing.InputPerMillion) +
               (cacheRead / 1_000_000m * (pricing.CacheReadPerMillion ?? pricing.InputPerMillion)) +
               (cacheCreate / 1_000_000m * (pricing.CacheCreationPerMillion ?? pricing.InputPerMillion)) +
               (output / 1_000_000m * pricing.OutputPerMillion);
    }

    private static ModelsDevPricingInfo? BuiltInPricingFor(string model)
    {
        var normalized = NormalizeGrokModel(model);
        return GrokPricing.TryGetValue(normalized, out var pricing) ? pricing : null;
    }

    private static string NormalizeGrokModel(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("xai/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["xai/".Length..];
        }

        // Strip build-channel suffixes used in local fingerprints.
        if (value.EndsWith("-build", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"-build".Length];
        }

        return value;
    }

    private static decimal? ReadCostUsd(JsonElement usage)
    {
        if (usage.TryGetProperty("costUsdTicks", out var ticksElement) ||
            usage.TryGetProperty("cost_usd_ticks", out ticksElement) ||
            usage.TryGetProperty("total_cost_usd_ticks", out ticksElement))
        {
            if (ticksElement.ValueKind == JsonValueKind.Number && ticksElement.TryGetDecimal(out var ticks))
            {
                return ticks / CostTicksPerUsd;
            }

            if (ticksElement.ValueKind == JsonValueKind.String &&
                decimal.TryParse(ticksElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out ticks))
            {
                return ticks / CostTicksPerUsd;
            }
        }

        if (usage.TryGetProperty("costUSD", out var costElement) ||
            usage.TryGetProperty("costUsd", out costElement) ||
            usage.TryGetProperty("total_cost_usd", out costElement))
        {
            if (costElement.ValueKind == JsonValueKind.Number && costElement.TryGetDecimal(out var cost))
            {
                return cost;
            }

            if (costElement.ValueKind == JsonValueKind.String &&
                decimal.TryParse(costElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out cost))
            {
                return cost;
            }
        }

        return null;
    }

    private static DateOnly? ReadDay(JsonElement root, JsonElement update)
    {
        if (root.TryGetProperty("timestamp", out var timestamp))
        {
            if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var unix))
            {
                var offset = unix > 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
                return DateOnly.FromDateTime(offset.ToLocalTime().DateTime);
            }

            if (timestamp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(timestamp.GetString(), out var parsed))
            {
                return DateOnly.FromDateTime(parsed.ToLocalTime().DateTime);
            }
        }

        if (update.TryGetProperty("ts", out var ts) &&
            ts.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(ts.GetString(), out var tsParsed))
        {
            return DateOnly.FromDateTime(tsParsed.ToLocalTime().DateTime);
        }

        return null;
    }

    private static long ReadLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return Math.Max(0, number);
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return Math.Max(0, number);
            }
        }

        return 0;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadNestedString(JsonElement element, string parent, string child)
    {
        return element.TryGetProperty(parent, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadString(nested, child)
            : null;
    }

    private sealed record GrokUsageRow(
        string FilePath,
        DateOnly Day,
        string Model,
        string? PromptId,
        TokenTotals Tokens,
        decimal? ExactCostUsd,
        decimal? CostUsd,
        bool CostPriced);

    private sealed record TokenTotals(
        long InputTokens,
        long CachedInputTokens,
        long CacheCreationTokens,
        long OutputTokens);

    private sealed class MutableUsage
    {
        private readonly Dictionary<string, decimal> categories = new(StringComparer.OrdinalIgnoreCase);
        public long InputTokens { get; private set; }
        public long CachedInputTokens { get; private set; }
        public long CacheCreationTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public decimal EstimatedCostUsd { get; private set; }
        public bool HasIncompleteCost { get; private set; }
        public string? DisplayName { get; private set; }

        public IReadOnlyList<ProviderSpendCategory> SpendCategories =>
            categories
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .Select(pair => new ProviderSpendCategory(pair.Key, pair.Value))
                .ToArray();

        public void Add(
            string model,
            TokenTotals tokens,
            decimal? exactCostUsd,
            bool costPriced,
            string displayName,
            string categoryLabel)
        {
            InputTokens += tokens.InputTokens;
            CachedInputTokens += tokens.CachedInputTokens;
            CacheCreationTokens += tokens.CacheCreationTokens;
            OutputTokens += tokens.OutputTokens;
            DisplayName ??= displayName;
            if (exactCostUsd is { } cost)
            {
                EstimatedCostUsd += cost;
                categories[categoryLabel] = categories.GetValueOrDefault(categoryLabel) + cost;
            }
            else if (!costPriced)
            {
                HasIncompleteCost = true;
            }
        }
    }

    /// <summary>
    /// Fallback list prices (USD / 1M tokens), used only when models.dev has nothing to say and the
    /// turn carried no server-stamped <c>costUsdTicks</c>. THE ONLY Grok price table in the app -
    /// there was briefly a second one in LedgerPricing that had already drifted from this one.
    /// </summary>
    /// <remarks>
    /// grok-4.5 is fitted to real stamped turns rather than copied from a card: solving
    /// rawInput·I + cacheRead·C + output·O = costUsdTicks/1e10 over three observed turns gives
    /// I≈2.02, C≈0.17, O≈17.3, so 2.00/0.20/15.00 reproduces them to within ~5%. The previous
    /// 3.00/15.00/0.75 overshot the same turns by 43-55%.
    ///
    /// Cache CREATION is null, not a rate: xAI does not bill a cache write and Grok turn ledgers
    /// carry no cacheCreationTokens field at all. The 3.75 that used to sit here was Anthropic's
    /// 1.25x-input cache-write shape, copied across from the Claude table where it belongs.
    /// The other ids keep their published rates - no stamped turns were available to fit them.
    /// </remarks>
    private static readonly Dictionary<string, ModelsDevPricingInfo> GrokPricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grok-4.5"] = new(2.00m, 15.00m, 0.20m, null, null, null, null, null, null),
        ["grok-4"] = new(3.00m, 15.00m, 0.75m, null, null, null, null, null, null),
        ["grok-3"] = new(3.00m, 15.00m, 0.75m, null, null, null, null, null, null),
        ["grok-2"] = new(2.00m, 10.00m, 0.50m, null, null, null, null, null, null),
    };
}
