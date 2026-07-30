using System.Globalization;
using System.Text.Json;

namespace CodexBarWindows;

public sealed class ClaudeUsageInsightsReader
{
    private const int DaysToReport = 30;
    private const int ScanLookbackDays = 32;
    private const int MaxFilesToScan = 1200;
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

            var rows = new List<ClaudeUsageRow>();
            foreach (var file in files)
            {
                rows.AddRange(GetOrReadRowsFromFile(file, firstScanDay));
            }

            PruneFileRowsCache();

            var daily = new Dictionary<DateOnly, MutableUsage>();
            var models = new Dictionary<string, MutableUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in DeduplicateAcrossFiles(rows))
            {
                var label = NormalizeClaudeModel(row.Model);
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

            var warning = hasIncompleteCost
                ? "Some Claude models had no pricing; cost may be incomplete."
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
        DateOnly FirstScanDay,
        IReadOnlyList<ClaudeUsageRow> Rows);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedFileRows> FileRowsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<ClaudeUsageRow> GetOrReadRowsFromFile(string file, DateOnly firstScanDay)
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

        // A cached scan taken with an earlier lookback window is still valid: rows older than the
        // current window were already excluded then, and days only move forward. Filtering out
        // rows that have since aged out happens downstream against the report window.
        if (FileRowsCache.TryGetValue(file, out var cached) &&
            cached.Length == length &&
            cached.LastWriteUtcTicks == lastWriteUtcTicks &&
            cached.FirstScanDay <= firstScanDay)
        {
            // Cost is recomputed on every replay so a models.dev pricing refresh that landed
            // after the file was cached still prices these rows.
            return cached.Rows.Select(RepriceRow).ToArray();
        }

        var rows = ReadRowsFromFile(file, firstScanDay);
        FileRowsCache[file] = new CachedFileRows(length, lastWriteUtcTicks, firstScanDay, rows);
        return rows;
    }

    private static ClaudeUsageRow RepriceRow(ClaudeUsageRow row)
    {
        var rawInput = Math.Max(0, row.Tokens.InputTokens - row.Tokens.CachedInputTokens);
        var cost = EstimateCost(row.Model, rawInput, row.Tokens.CachedInputTokens, row.Tokens.CacheCreationTokens, row.Tokens.OutputTokens);
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

                var day = ReadDay(root);
                if (day is null || day < firstScanDay)
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
                    day.Value,
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

    private static decimal? EstimateCost(string model, long rawInput, long cacheRead, long cacheCreate, long output)
    {
        var pricing = ModelsDevPricing.Lookup("anthropic", model) ?? BuiltInPricingFor(model);
        if (pricing is null)
        {
            return null;
        }

        return Tiered(rawInput, pricing.InputPerMillion, pricing.InputPerMillionAboveThreshold, pricing.ThresholdTokens) +
               Tiered(cacheRead, pricing.CacheReadPerMillion ?? pricing.InputPerMillion, pricing.CacheReadPerMillionAboveThreshold, pricing.ThresholdTokens) +
               Tiered(cacheCreate, pricing.CacheCreationPerMillion ?? pricing.InputPerMillion, pricing.CacheCreationPerMillionAboveThreshold, pricing.ThresholdTokens) +
               Tiered(output, pricing.OutputPerMillion, pricing.OutputPerMillionAboveThreshold, pricing.ThresholdTokens);
    }

    private static decimal Tiered(long tokens, decimal basePerMillion, decimal? abovePerMillion, int? threshold)
    {
        tokens = Math.Max(0, tokens);
        if (threshold is not { } cutoff || abovePerMillion is not { } above)
        {
            return tokens / 1_000_000m * basePerMillion;
        }

        var below = Math.Min(tokens, cutoff);
        var over = Math.Max(tokens - cutoff, 0);
        return below / 1_000_000m * basePerMillion + over / 1_000_000m * above;
    }

    private static ModelsDevPricingInfo? BuiltInPricingFor(string model)
    {
        var normalized = NormalizeClaudeModel(model);
        return ClaudePricing.TryGetValue(normalized, out var pricing) ? pricing : null;
    }

    private static string NormalizeClaudeModel(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase))
        {
            value = value["anthropic.".Length..];
        }

        var lastDot = value.LastIndexOf('.');
        if (lastDot >= 0 && value.Contains("claude-", StringComparison.OrdinalIgnoreCase))
        {
            var tail = value[(lastDot + 1)..];
            if (tail.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            {
                value = tail;
            }
        }

        value = System.Text.RegularExpressions.Regex.Replace(value, "-v\\d+:\\d+$", string.Empty);
        var compactDate = System.Text.RegularExpressions.Regex.Match(value, "-\\d{8}$");
        if (compactDate.Success)
        {
            var baseName = value[..compactDate.Index];
            if (ClaudePricing.ContainsKey(baseName))
            {
                return baseName;
            }
        }

        return value;
    }

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

    private static DateOnly? ReadDay(JsonElement element)
    {
        return DayFromText(ReadString(element, "timestamp"));
    }

    private static DateOnly? DayFromText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(value, "\\d{4}-\\d{2}-\\d{2}");
        return match.Success && DateOnly.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
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
        DateOnly Day,
        string Model,
        string? MessageId,
        string? RequestId,
        string? SessionId,
        bool IsSidechain,
        ClaudePathRole PathRole,
        TokenTotals Tokens,
        decimal? CostUsd,
        bool CostPriced);

    private enum ClaudePathRole
    {
        Parent,
        Subagent
    }

    private static readonly IReadOnlyDictionary<string, ModelsDevPricingInfo> ClaudePricing = new Dictionary<string, ModelsDevPricingInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-haiku-4-5-20251001"] = new(1.00m, 5.00m, 0.10m, 1.25m, null, null, null, null, null),
        ["claude-haiku-4-5"] = new(1.00m, 5.00m, 0.10m, 1.25m, null, null, null, null, null),
        ["claude-opus-4-5-20251101"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-opus-4-5"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-opus-4-6-20260205"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-opus-4-6"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-opus-4-7"] = new(5.00m, 25.00m, 0.50m, 6.25m, null, null, null, null, null),
        ["claude-sonnet-4-5"] = new(3.00m, 15.00m, 0.30m, 3.75m, 200_000, 6.00m, 22.50m, 0.60m, 7.50m),
        ["claude-sonnet-4-6"] = new(3.00m, 15.00m, 0.30m, 3.75m, 200_000, 6.00m, 22.50m, 0.60m, 7.50m),
        ["claude-sonnet-4-5-20250929"] = new(3.00m, 15.00m, 0.30m, 3.75m, 200_000, 6.00m, 22.50m, 0.60m, 7.50m),
        ["claude-opus-4-20250514"] = new(15.00m, 75.00m, 1.50m, 18.75m, null, null, null, null, null),
        ["claude-opus-4-1"] = new(15.00m, 75.00m, 1.50m, 18.75m, null, null, null, null, null),
        ["claude-sonnet-4-20250514"] = new(3.00m, 15.00m, 0.30m, 3.75m, 200_000, 6.00m, 22.50m, 0.60m, 7.50m),
    };
}
