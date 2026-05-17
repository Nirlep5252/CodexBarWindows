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
                dailyRows.Sum(row => row.EstimatedCostUsd));

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

                Add(daily, day.Value, model, delta);
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
                if (input == 0 && cacheRead == 0 && cacheWrite == 0 && output == 0 && directTotal == 0)
                {
                    continue;
                }

                var effectiveInput = Math.Max(input + cacheRead + cacheWrite, Math.Max(0, directTotal - output));
                var tokens = new TokenTotals(effectiveInput, Math.Min(cacheRead, effectiveInput), output);
                Add(daily, day.Value, model, tokens);
                Add(models, NormalizeModelName(model), model, tokens);
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

    private static void Add(IDictionary<DateOnly, MutableUsage> daily, DateOnly day, string model, TokenTotals tokens)
    {
        if (!daily.TryGetValue(day, out var usage))
        {
            usage = new MutableUsage();
            daily[day] = usage;
        }

        usage.Add(model, tokens);
    }

    private static void Add(IDictionary<string, MutableUsage> models, string key, string model, TokenTotals tokens)
    {
        if (!models.TryGetValue(key, out var usage))
        {
            usage = new MutableUsage();
            models[key] = usage;
        }

        usage.Add(model, tokens);
    }

    private static CodexDailyUsage ToDaily(DateOnly day, MutableUsage usage)
    {
        return new CodexDailyUsage(day, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, usage.EstimatedCostUsd);
    }

    private static CodexModelUsage ToModel(string model, MutableUsage usage)
    {
        return new CodexModelUsage(model, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, usage.EstimatedCostUsd);
    }

    private static string NormalizeModelName(string model)
    {
        return string.IsNullOrWhiteSpace(model) ? "Codex model" : model.Trim().ToLowerInvariant();
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

        public void Add(string model, TokenTotals tokens)
        {
            InputTokens += tokens.InputTokens;
            CachedInputTokens += tokens.CachedInputTokens;
            OutputTokens += tokens.OutputTokens;
            EstimatedCostUsd += EstimateCost(model, tokens);
        }
    }

    private static decimal EstimateCost(string model, TokenTotals tokens)
    {
        var pricing = PricingFor(model);
        var billableInput = Math.Max(0, tokens.InputTokens - tokens.CachedInputTokens);
        return ((decimal)billableInput / 1_000_000m * pricing.InputPerMillion) +
               ((decimal)tokens.CachedInputTokens / 1_000_000m * pricing.CachedInputPerMillion) +
               ((decimal)tokens.OutputTokens / 1_000_000m * pricing.OutputPerMillion);
    }

    private static ModelPricing PricingFor(string model)
    {
        var normalized = model.ToLowerInvariant();
        if (normalized.Contains("gpt-5", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelPricing(1.25m, 0.125m, 10.00m);
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

        return new ModelPricing(1.25m, 0.125m, 10.00m);
    }

    private readonly record struct ModelPricing(decimal InputPerMillion, decimal CachedInputPerMillion, decimal OutputPerMillion);
}
