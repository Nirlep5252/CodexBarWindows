using System.Globalization;
using System.Text;
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

            foreach (var file in codexFiles)
            {
                ScanCodexFile(file, firstScanDay, daily, models, fastTurnIds);
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
        IDictionary<string, MutableUsage> models,
        IReadOnlySet<string> fastTurnIds)
    {
        var shape = ClassifyCodexRollout(file);
        if (shape.SuppressWholeFile)
        {
            return;
        }

        string? currentModel = null;
        string? currentTurnId = null;
        var currentIsFastMode = false;
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
                    currentIsFastMode = IsFastMode(currentModel ?? "Codex model", default, null, root) ||
                        (currentTurnId is not null && fastTurnIds.Contains(currentTurnId));
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

                var day = ReadDay(root);
                if (day is null || day < firstScanDay)
                {
                    continue;
                }

                var model = ReadModel(root) ?? ReadModel(payload) ?? currentModel ?? "Codex model";
                var rowIsFastMode = payload.TryGetProperty("rate_limits", out var rateLimits)
                    ? IsFastMode(model, delta, null, root, payload, rateLimits)
                    : IsFastMode(model, delta, null, root, payload);
                var isFastMode = currentIsFastMode || rowIsFastMode;
                var categoryLabel = ModelBreakdownLabel(model, isFastMode);
                Add(daily, day.Value, model, delta, isFastMode, categoryLabel: categoryLabel);
                Add(models, ModelBreakdownKey(model, isFastMode), model, delta, isFastMode, displayName: categoryLabel);
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
                var isFastMode = IsFastMode(model, tokens, exactCost, root, message, usage);
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

    private static ProviderDailyUsage ToDaily(DateOnly day, MutableUsage usage)
    {
        return new ProviderDailyUsage(day, usage.InputTokens, usage.CachedInputTokens, 0, usage.OutputTokens, usage.EstimatedCostUsd, usage.FastEstimatedCostUsd, usage.SpendCategories);
    }

    private static ProviderModelUsage ToModel(string model, MutableUsage usage)
    {
        return new ProviderModelUsage(usage.DisplayName ?? model, usage.InputTokens, usage.CachedInputTokens, 0, usage.OutputTokens, usage.EstimatedCostUsd, usage.FastEstimatedCostUsd);
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

    private static decimal EstimateCost(string model, TokenTotals tokens)
    {
        return PricingFor(model) is { } pricing
            ? EstimateCost(pricing, tokens, usePriorityRates: false)
            : 0m;
    }

    private static decimal? EstimatePriorityCost(string model, TokenTotals tokens)
    {
        if (PricingFor(model) is not { } pricing ||
            tokens.InputTokens > PriorityInputTokenLimit ||
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

    /// <summary>
    /// Resolves per-million rates for a Codex model, or <c>null</c> when the model is unknown.
    /// Unknown models must not borrow another model's rates: a silent fallback is how a whole
    /// generation of models (gpt-5.6) got billed at gpt-5 rates without anything looking wrong.
    /// </summary>
    private static ModelPricing? PricingFor(string model)
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

        return ModelsDevPricingFor(normalized);
    }

    private static ModelPricing? ModelsDevPricingFor(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized) || ModelsDevPricing.Lookup("openai", normalized) is not { } info)
        {
            return null;
        }

        return new ModelPricing(
            info.InputPerMillion,
            info.CacheReadPerMillion ?? info.InputPerMillion,
            info.OutputPerMillion,
            info.ThresholdTokens,
            info.InputPerMillionAboveThreshold,
            info.CacheReadPerMillionAboveThreshold,
            info.OutputPerMillionAboveThreshold);
    }

    private static string NormalizePricingModelName(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        const string openAiPrefix = "openai/";
        if (normalized.StartsWith(openAiPrefix, StringComparison.Ordinal))
        {
            normalized = normalized[openAiPrefix.Length..];
        }

        // OpenAI routes the unsuffixed gpt-5.6 alias to Sol.
        if (string.Equals(normalized, "gpt-5.6", StringComparison.Ordinal))
        {
            return "gpt-5.6-sol";
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
        ["gpt-5.6-sol"] = new(5.00m, 0.50m, 30.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 10.00m, CachedInputPerMillionAboveThreshold: 1.00m, OutputPerMillionAboveThreshold: 45.00m, PriorityInputPerMillion: 10.00m, PriorityCachedInputPerMillion: 1.00m, PriorityOutputPerMillion: 60.00m),
        ["gpt-5.6-terra"] = new(2.50m, 0.25m, 15.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 5.00m, CachedInputPerMillionAboveThreshold: 0.50m, OutputPerMillionAboveThreshold: 22.50m, PriorityInputPerMillion: 5.00m, PriorityCachedInputPerMillion: 0.50m, PriorityOutputPerMillion: 30.00m),
        ["gpt-5.6-luna"] = new(1.00m, 0.10m, 6.00m, ThresholdTokens: 272_000, InputPerMillionAboveThreshold: 2.00m, CachedInputPerMillionAboveThreshold: 0.20m, OutputPerMillionAboveThreshold: 9.00m, PriorityInputPerMillion: 2.00m, PriorityCachedInputPerMillion: 0.20m, PriorityOutputPerMillion: 12.00m),
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
