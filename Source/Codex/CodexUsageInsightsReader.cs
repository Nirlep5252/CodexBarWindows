using System.Globalization;
using System.Text.Json;

namespace CodexBarWindows;

public sealed class CodexUsageInsightsReader
{
    private const int DaysToReport = 30;
    private const int ScanLookbackDays = 32;
    private const int MaxFilesToScan = 1200;
    private readonly string codexHome;

    public CodexUsageInsightsReader()
        : this(ResolveCodexHome())
    {
    }

    public CodexUsageInsightsReader(string codexHome)
    {
        this.codexHome = codexHome;
    }

    public CodexUsageInsightsLookupResult ReadLatest()
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
                return new CodexUsageInsightsLookupResult(
                    EmptyInsights(now, firstReportDay),
                    $"No Codex or pi session logs were found under {codexHome} or {piSessionsRoot}.");
            }

            var daily = new Dictionary<DateOnly, MutableUsage>();
            var models = new Dictionary<string, MutableUsage>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in codexFiles)
            {
                ScanCodexFile(file, firstScanDay, daily, models);
            }

            foreach (var file in piFiles)
            {
                ScanPiFile(file, firstScanDay, daily, models);
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
                ?? new CodexDailyUsage(today, 0, 0, 0, 0);

            var result = new CodexUsageInsights(
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
            return new CodexUsageInsightsLookupResult(result, error);
        }
        catch (Exception exception)
        {
            return new CodexUsageInsightsLookupResult(null, $"Could not read Codex usage history: {exception.Message}");
        }
    }

    private static CodexUsageInsights EmptyInsights(DateTimeOffset observedAt, DateOnly firstReportDay)
    {
        var daily = Enumerable.Range(0, DaysToReport)
            .Select(offset => new CodexDailyUsage(firstReportDay.AddDays(offset), 0, 0, 0, 0))
            .ToArray();

        return new CodexUsageInsights(observedAt, "Local Codex + pi sessions", daily, [], 0, 0, 0, 0);
    }

    private IEnumerable<string> EnumerateCodexJsonlFiles(DateOnly firstScanDay)
    {
        foreach (var root in SessionRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsRelevantFile(file, firstScanDay))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> EnumeratePiJsonlFiles(string piSessionsRoot, DateOnly firstScanDay)
    {
        if (!Directory.Exists(piSessionsRoot))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(piSessionsRoot, "*.jsonl", SearchOption.AllDirectories);
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

    private IEnumerable<string> SessionRoots()
    {
        yield return Path.Combine(codexHome, "sessions");
        yield return Path.Combine(codexHome, "archived_sessions");
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

    private static void ScanCodexFile(
        string file,
        DateOnly firstScanDay,
        IDictionary<DateOnly, MutableUsage> daily,
        IDictionary<string, MutableUsage> models)
    {
        string? currentModel = null;
        TokenTotals previousTotals = default;
        var hasPreviousTotals = false;

        foreach (var line in ReadSharedLines(file))
        {
            if (string.IsNullOrWhiteSpace(line) ||
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
                    continue;
                }

                if (!string.Equals(type, "event_msg", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("type", out var payloadType) ||
                    !string.Equals(payloadType.GetString(), "token_count", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var day = ReadDay(root);
                if (day is null || day < firstScanDay)
                {
                    continue;
                }

                var model = ReadModel(root) ?? ReadModel(payload) ?? currentModel ?? "Codex model";
                var delta = ReadTokenDelta(payload, ref previousTotals, ref hasPreviousTotals);
                if (delta.InputTokens == 0 && delta.CachedInputTokens == 0 && delta.OutputTokens == 0)
                {
                    continue;
                }

                Add(daily, day.Value, model, delta, categoryLabel: ModelBreakdownLabel(model, isFastMode: false));
                Add(models, NormalizeModelName(model), model, delta);
            }
            catch
            {
                // Session logs may contain partial or future-format rows. Ignore only the bad row.
            }
        }
    }

    private static void ScanPiFile(
        string file,
        DateOnly firstScanDay,
        IDictionary<DateOnly, MutableUsage> daily,
        IDictionary<string, MutableUsage> models)
    {
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

                var day = ReadDay(message) ?? ReadDay(root);
                if (day is null || day < firstScanDay)
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
                var isFastMode = IsFastMode(root, message, usage, model, tokens, exactCost);
                var categoryLabel = ModelBreakdownLabel(model, isFastMode);
                Add(daily, day.Value, model, tokens, isFastMode, exactCost, categoryLabel);
                Add(models, ModelBreakdownKey(model, isFastMode), model, tokens, isFastMode, exactCost, categoryLabel);
            }
            catch
            {
                // pi session logs may contain partial or future-format rows. Ignore only the bad row.
            }
        }
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

    private static TokenTotals ReadTokenDelta(JsonElement payload, ref TokenTotals previousTotals, ref bool hasPreviousTotals)
    {
        if (!payload.TryGetProperty("info", out var info))
        {
            return default;
        }

        if (info.TryGetProperty("last_token_usage", out var last))
        {
            var delta = ReadTotals(last);
            if (info.TryGetProperty("total_token_usage", out var total))
            {
                previousTotals = ReadTotals(total);
                hasPreviousTotals = true;
            }
            else if (hasPreviousTotals)
            {
                previousTotals = previousTotals.Add(delta);
            }

            return delta.WithCachedInputClamped();
        }

        if (!info.TryGetProperty("total_token_usage", out var totalUsage))
        {
            return default;
        }

        var current = ReadTotals(totalUsage);
        var totalDelta = hasPreviousTotals ? current.SubtractFloor(previousTotals) : current;
        previousTotals = current;
        hasPreviousTotals = true;
        return totalDelta.WithCachedInputClamped();
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

    private static DateOnly? ReadDay(JsonElement element)
    {
        if (!element.TryGetProperty("timestamp", out var timestampElement))
        {
            return null;
        }

        var timestamp = timestampElement.ValueKind switch
        {
            JsonValueKind.String => timestampElement.GetString(),
            JsonValueKind.Number when timestampElement.TryGetInt64(out var raw) => UnixTimestampToLocalDateText(raw),
            _ => null
        };

        return DayFromText(timestamp);
    }

    private static string UnixTimestampToLocalDateText(long raw)
    {
        var timestamp = raw > 1_000_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
            : DateTimeOffset.FromUnixTimeSeconds(raw);
        return timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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

    private static bool IsFastMode(JsonElement root, JsonElement message, JsonElement usage, string model, TokenTotals tokens, decimal? exactCostUsd)
    {
        if (HasFastModeMarker(message) || HasFastModeMarker(root) || HasFastModeMarker(usage))
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

        foreach (var propertyName in new[] { "mode", "tier", "serviceTier", "service_tier", "priority", "fast" })
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
                if (text is not null &&
                    (text.Contains("fast", StringComparison.OrdinalIgnoreCase) || text.Contains("priority", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(value, "\\d{4}-\\d{2}-\\d{2}");
        return match.Success && DateOnly.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
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

    private static CodexDailyUsage ToDaily(DateOnly day, MutableUsage usage)
    {
        return new CodexDailyUsage(day, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, usage.EstimatedCostUsd, usage.FastEstimatedCostUsd, usage.SpendCategories);
    }

    private static CodexModelUsage ToModel(string model, MutableUsage usage)
    {
        return new CodexModelUsage(usage.DisplayName ?? model, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, usage.EstimatedCostUsd, usage.FastEstimatedCostUsd);
    }

    private static string NormalizeModelName(string model)
    {
        return string.IsNullOrWhiteSpace(model) ? "Codex model" : model.Trim().ToLowerInvariant();
    }

    private static string ModelBreakdownKey(string model, bool isFastMode)
    {
        var normalized = NormalizePricingModelName(model);
        return isFastMode ? normalized + "|fast" : normalized;
    }

    private static string ModelBreakdownLabel(string model, bool isFastMode)
    {
        var label = string.IsNullOrWhiteSpace(model) ? "Codex model" : NormalizePricingModelName(model);
        return isFastMode ? label + " fast" : label;
    }

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

        public IReadOnlyList<CodexSpendCategory> SpendCategories => spendCategories
            .Select(pair => new CodexSpendCategory(pair.Key, pair.Value))
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

    private static decimal EstimateCost(string model, TokenTotals tokens)
    {
        var pricing = PricingFor(model);
        return EstimateCost(pricing, tokens, usePriorityRates: false);
    }

    private static decimal? EstimatePriorityCost(string model, TokenTotals tokens)
    {
        var pricing = PricingFor(model);
        if (tokens.InputTokens > PriorityInputTokenLimit ||
            pricing.PriorityInputPerMillion is null ||
            pricing.PriorityOutputPerMillion is null)
        {
            return null;
        }

        return EstimateCost(pricing, tokens, usePriorityRates: true);
    }

    private static decimal EstimateCost(ModelPricing pricing, TokenTotals tokens, bool usePriorityRates)
    {
        var billableInput = Math.Max(0, tokens.InputTokens - tokens.CachedInputTokens);
        var usesLongContextRates = !usePriorityRates && pricing.ThresholdTokens is { } threshold && tokens.InputTokens > threshold;
        var inputPerMillion = usePriorityRates
            ? pricing.PriorityInputPerMillion ?? pricing.InputPerMillion
            : usesLongContextRates ? pricing.InputPerMillionAboveThreshold ?? pricing.InputPerMillion : pricing.InputPerMillion;
        var cachedInputPerMillion = usePriorityRates
            ? pricing.PriorityCachedInputPerMillion ?? pricing.CachedInputPerMillion
            : usesLongContextRates ? pricing.CachedInputPerMillionAboveThreshold ?? pricing.CachedInputPerMillion : pricing.CachedInputPerMillion;
        var outputPerMillion = usePriorityRates
            ? pricing.PriorityOutputPerMillion ?? pricing.OutputPerMillion
            : usesLongContextRates ? pricing.OutputPerMillionAboveThreshold ?? pricing.OutputPerMillion : pricing.OutputPerMillion;

        return ((decimal)billableInput / 1_000_000m * inputPerMillion) +
               ((decimal)tokens.CachedInputTokens / 1_000_000m * cachedInputPerMillion) +
               ((decimal)tokens.OutputTokens / 1_000_000m * outputPerMillion);
    }

    private const int PriorityInputTokenLimit = 272_000;

    private static ModelPricing PricingFor(string model)
    {
        var normalized = NormalizePricingModelName(model);
        if (CodexPricing.TryGetValue(normalized, out var pricing))
        {
            return pricing;
        }

        if (normalized.Contains("gpt-4.1", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelPricing(2.00m, 0.50m, 8.00m);
        }

        if (normalized.Contains("o4-mini", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelPricing(1.10m, 0.275m, 4.40m);
        }

        if (normalized.Contains("o3", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelPricing(2.00m, 0.50m, 8.00m);
        }

        return CodexPricing["gpt-5"];
    }

    private static string NormalizePricingModelName(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        const string openAiPrefix = "openai/";
        if (normalized.StartsWith(openAiPrefix, StringComparison.Ordinal))
        {
            normalized = normalized[openAiPrefix.Length..];
        }

        if (CodexPricing.ContainsKey(normalized))
        {
            return normalized;
        }

        var datedSuffix = System.Text.RegularExpressions.Regex.Match(normalized, "-\\d{4}-\\d{2}-\\d{2}$");
        if (datedSuffix.Success)
        {
            var withoutDate = normalized[..datedSuffix.Index];
            if (CodexPricing.ContainsKey(withoutDate))
            {
                return withoutDate;
            }
        }

        return normalized;
    }

    private static readonly IReadOnlyDictionary<string, ModelPricing> CodexPricing = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5-codex"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5-mini"] = new(0.25m, 0.025m, 2.00m),
        ["gpt-5-nano"] = new(0.05m, 0.005m, 0.40m),
        ["gpt-5-pro"] = new(15.00m, 15.00m, 120.00m),
        ["gpt-5.1"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5.1-codex"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5.1-codex-max"] = new(1.25m, 0.125m, 10.00m),
        ["gpt-5.1-codex-mini"] = new(0.25m, 0.025m, 2.00m),
        ["gpt-5.2"] = new(1.75m, 0.175m, 14.00m),
        ["gpt-5.2-codex"] = new(1.75m, 0.175m, 14.00m),
        ["gpt-5.2-pro"] = new(21.00m, 21.00m, 168.00m),
        ["gpt-5.3-codex"] = new(1.75m, 0.175m, 14.00m),
        ["gpt-5.3-codex-spark"] = new(0.00m, 0.00m, 0.00m),
        ["gpt-5.4"] = new(2.50m, 0.25m, 15.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 5.00m, CachedInputPerMillionAboveThreshold: 0.50m, OutputPerMillionAboveThreshold: 22.50m, PriorityInputPerMillion: 5.00m, PriorityCachedInputPerMillion: 0.50m, PriorityOutputPerMillion: 30.00m),
        ["gpt-5.4-mini"] = new(0.75m, 0.075m, 4.50m, PriorityInputPerMillion: 1.50m, PriorityCachedInputPerMillion: 0.15m, PriorityOutputPerMillion: 9.00m),
        ["gpt-5.4-nano"] = new(0.20m, 0.020m, 1.25m),
        ["gpt-5.4-pro"] = new(30.00m, 30.00m, 180.00m),
        ["gpt-5.5"] = new(5.00m, 0.50m, 30.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 10.00m, CachedInputPerMillionAboveThreshold: 1.00m, OutputPerMillionAboveThreshold: 45.00m, PriorityInputPerMillion: 12.50m, PriorityCachedInputPerMillion: 1.25m, PriorityOutputPerMillion: 75.00m),
        ["gpt-5.5-pro"] = new(30.00m, 30.00m, 180.00m),
    };

    private readonly record struct ModelPricing(
        decimal InputPerMillion,
        decimal CachedInputPerMillion,
        decimal OutputPerMillion,
        int? ThresholdTokens = null,
        decimal? InputPerMillionAboveThreshold = null,
        decimal? CachedInputPerMillionAboveThreshold = null,
        decimal? OutputPerMillionAboveThreshold = null,
        decimal? PriorityInputPerMillion = null,
        decimal? PriorityCachedInputPerMillion = null,
        decimal? PriorityOutputPerMillion = null);
}
