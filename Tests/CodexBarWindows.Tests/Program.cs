using System.Globalization;
using System.Text.Json;
using CodexBarWindows;

if (args.Contains("--scan-real-codex", StringComparer.OrdinalIgnoreCase))
{
    var result = new CodexUsageInsightsReader().ReadLatest();
    Console.WriteLine($"error={result.Error ?? "<none>"}");
    Console.WriteLine($"hasInsights={result.HasInsights}");
    if (result.Insights is { } insights)
    {
        Console.WriteLine($"source={insights.Source}");
        Console.WriteLine($"todayTokens={insights.TodayTokens}");
        Console.WriteLine($"todayCost={insights.TodayEstimatedCostUsd}");
        Console.WriteLine($"todayFastCost={insights.TodayFastEstimatedCostUsd}");
        Console.WriteLine($"last30Tokens={insights.Last30DaysTokens}");
        Console.WriteLine($"last30Cost={insights.Last30DaysEstimatedCostUsd}");
        Console.WriteLine($"last30FastCost={insights.Last30DaysFastEstimatedCostUsd}");
        Console.WriteLine($"models={string.Join(", ", insights.Models.Select(m => $"{m.Model}:{m.TotalTokens}:fast={m.FastEstimatedCostUsd}"))}");
        Console.WriteLine($"nonzeroDays={string.Join(", ", insights.Daily.Where(d => d.TotalTokens > 0).Select(d => $"{d.Day}:{d.TotalTokens}:cost={d.EstimatedCostUsd}:fast={d.FastEstimatedCostUsd}"))}");
    }

    return;
}

if (args.Contains("--scan-real-claude", StringComparer.OrdinalIgnoreCase))
{
    var result = new ClaudeUsageInsightsReader(null, refreshModelsDevPricing: false).ReadLatest();
    Console.WriteLine($"error={result.Error ?? "<none>"}");
    Console.WriteLine($"hasInsights={result.HasInsights}");
    if (result.Insights is { } insights)
    {
        Console.WriteLine($"source={insights.Source}");
        Console.WriteLine($"todayTokens={insights.TodayTokens}");
        Console.WriteLine($"last30Tokens={insights.Last30DaysTokens}");
        Console.WriteLine($"last30Cost={insights.Last30DaysEstimatedCostUsd}");
        Console.WriteLine($"models={string.Join(", ", insights.Models.Select(m => $"{m.Model}:{m.TotalTokens}"))}");
        Console.WriteLine($"nonzeroDays={string.Join(", ", insights.Daily.Where(d => d.TotalTokens > 0).Select(d => $"{d.Day}:{d.TotalTokens}"))}");
    }

    return;
}

var tests = new (string Name, Action Run)[]
{
    ("Codex history aggregates token_count rows", CodexHistoryAggregatesTokenCountRows),
    ("Codex history counts premium token_count rows as fast", CodexHistoryCountsPremiumTokenCountRowsAsFast),
    ("Codex history treats prolite token_count rows as regular", CodexHistoryTreatsProliteTokenCountRowsAsRegular),
    ("Codex history counts priority service tier turns as fast", CodexHistoryCountsPriorityServiceTierTurnsAsFast),
    ("Codex history counts priority client metadata turns as fast", CodexHistoryCountsPriorityClientMetadataTurnsAsFast),
    ("Codex history treats primary limit increase as regular", CodexHistoryTreatsPrimaryLimitIncreaseAsRegular),
    ("Codex history ignores stale primary limit for regular turns", CodexHistoryIgnoresStalePrimaryLimitForRegularTurns),
    ("Usage labels preserve fast suffix", UsageLabelsPreserveFastSuffix),
    ("Claude history aggregates tokens and cost", ClaudeHistoryAggregatesTokensAndCost),
    ("Claude history dedupes streaming and subagent rows", ClaudeHistoryDedupesRows),
    ("Claude history reports incomplete cost for unknown models", ClaudeHistoryReportsIncompleteCost),
    ("Claude history is usable without Claude credentials", ClaudeHistoryDoesNotRequireCredentials),
    ("Claude usage maps Fable from scoped limits", ClaudeUsageMapsFableLimit),
    ("Cursor usage keeps fractional percent fields", CursorUsageKeepsFractionalPercents),
    ("Cursor enterprise overall drives headline", CursorEnterpriseOverallDrivesHeadline),
    ("Cursor legacy request usage drives primary", CursorLegacyRequestsDrivePrimary),
    ("Cursor cookie header normalization trims prefix", CursorCookieHeaderNormalizationTrimsPrefix),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

if (failures.Count > 0)
{
    Environment.ExitCode = 1;
}

static void CodexHistoryAggregatesTokenCountRows()
{
    using var fixture = new CodexFixture();
    fixture.WriteSessionLog("session.jsonl", CodexTokenCountLine(
        model: "gpt-5.4",
        input: 1000,
        cacheRead: 100,
        output: 20,
        limitId: "codex"));

    var result = fixture.Read();
    var today = Today(result);

    AssertEqual(1000L, today.InputTokens, "codex input tokens");
    AssertEqual(100L, today.CachedInputTokens, "codex cached input tokens");
    AssertEqual(20L, today.OutputTokens, "codex output tokens");
    AssertEqual(1020L, today.TotalTokens, "codex total tokens");
    AssertClose(0.002575m, today.EstimatedCostUsd, "regular codex estimated cost");
    AssertEqual(0m, today.FastEstimatedCostUsd, "regular codex fast cost");
    Assert(result.Error is null, $"unexpected warning: {result.Error}");
}

static void CodexHistoryCountsPremiumTokenCountRowsAsFast()
{
    using var fixture = new CodexFixture();
    fixture.WriteSessionLog("session.jsonl", CodexTokenCountLine(
        model: "gpt-5.4",
        input: 1000,
        cacheRead: 100,
        output: 20,
        limitId: "premium"));

    var result = fixture.Read();
    var today = Today(result);

    AssertEqual(1020L, today.TotalTokens, "fast codex tokens should be included in history totals");
    AssertClose(0.00515m, today.EstimatedCostUsd, "fast codex total cost should use priority rates");
    AssertClose(0.00515m, today.FastEstimatedCostUsd, "fast codex cost should be tracked separately");
    Assert(
        today.Categories.Any(category => string.Equals(category.Label, "gpt-5.4 fast", StringComparison.OrdinalIgnoreCase)),
        "fast codex spend category should be labeled as fast");
}

static void CodexHistoryTreatsProliteTokenCountRowsAsRegular()
{
    using var fixture = new CodexFixture();
    fixture.WriteSessionLog(
        "session.jsonl",
        CodexTurnContextLine("gpt-5.4"),
        CodexTokenCountLine(
            model: null,
            input: 1000,
            cacheRead: 100,
            output: 20,
            limitId: "codex",
            planType: "prolite"));

    var result = fixture.Read();
    var today = Today(result);

    AssertEqual(1020L, today.TotalTokens, "prolite codex tokens should be included in history totals");
    AssertClose(0.002575m, today.EstimatedCostUsd, "prolite codex total cost should use regular rates");
    AssertEqual(0m, today.FastEstimatedCostUsd, "prolite codex cost should not be tracked as fast");
    Assert(
        result.Insights!.Models.Any(model => string.Equals(model.Model, "gpt-5.4", StringComparison.OrdinalIgnoreCase)),
        "prolite codex model row should keep the regular model label");
    Assert(
        result.Insights!.Models.All(model => !string.Equals(model.Model, "gpt-5.4 fast", StringComparison.OrdinalIgnoreCase)),
        "prolite codex model row should not be labeled as fast");
}

static void CodexHistoryCountsPriorityServiceTierTurnsAsFast()
{
    using var fixture = new CodexFixture();
    var turnId = Guid.NewGuid().ToString();
    fixture.WriteSessionLog(
        "session.jsonl",
        CodexTurnContextLine("gpt-5.4", turnId),
        CodexTokenCountLine(
            model: null,
            input: 1000,
            cacheRead: 100,
            output: 20,
            limitId: "codex",
            planType: "prolite"));
    fixture.WriteCodexLog(
        "logs_2.sqlite",
        $"session_task.turn thread.id={Guid.NewGuid()} turn.id={turnId} model=gpt-5.4 request {{\"service_tier\":\"priority\"}}");

    var result = fixture.Read();
    var today = Today(result);

    AssertClose(0.00515m, today.EstimatedCostUsd, "priority service tier total cost should use fast rates");
    AssertClose(0.00515m, today.FastEstimatedCostUsd, "priority service tier cost should be tracked as fast");
    Assert(
        result.Insights!.Models.Any(model => string.Equals(model.Model, "gpt-5.4 fast", StringComparison.OrdinalIgnoreCase)),
        "priority service tier model row should be labeled as fast");
}

static void CodexHistoryCountsPriorityClientMetadataTurnsAsFast()
{
    using var fixture = new CodexFixture();
    var sessionId = Guid.NewGuid().ToString();
    var turnId = Guid.NewGuid().ToString();
    var turnMetadata = JsonSerializer.Serialize(new
    {
        session_id = sessionId,
        thread_id = sessionId,
        turn_id = turnId
    });

    fixture.WriteSessionLog(
        "session.jsonl",
        CodexTurnContextLine("gpt-5.4", turnId),
        CodexTokenCountLine(
            model: null,
            input: 1000,
            cacheRead: 100,
            output: 20,
            limitId: "codex",
            planType: "prolite"));
    fixture.WriteCodexLog(
        "logs_2.sqlite",
        $"responses_websocket request {{\"service_tier\":\"priority\",\"client_metadata\":{{\"x-codex-turn-metadata\":{JsonSerializer.Serialize(turnMetadata)}}}}}");

    var result = fixture.Read();
    var today = Today(result);

    AssertClose(0.00515m, today.EstimatedCostUsd, "priority client metadata total cost should use fast rates");
    AssertClose(0.00515m, today.FastEstimatedCostUsd, "priority client metadata cost should be tracked as fast");
    Assert(
        result.Insights!.Models.Any(model => string.Equals(model.Model, "gpt-5.4 fast", StringComparison.OrdinalIgnoreCase)),
        "priority client metadata model row should be labeled as fast");
}

static void CodexHistoryTreatsPrimaryLimitIncreaseAsRegular()
{
    using var fixture = new CodexFixture();
    var firstTurnId = Guid.NewGuid().ToString();
    var secondTurnId = Guid.NewGuid().ToString();
    fixture.WriteSessionLog(
        "session.jsonl",
        CodexTurnContextLine("gpt-5.4", firstTurnId),
        CodexTokenCountLine(
            model: null,
            input: 1000,
            cacheRead: 100,
            output: 20,
            limitId: "codex",
            planType: "prolite",
            primaryUsedPercent: 0m),
        CodexTurnContextLine("gpt-5.4", secondTurnId),
        CodexTokenCountLine(
            model: null,
            input: 2000,
            cacheRead: 200,
            output: 40,
            limitId: "codex",
            planType: "prolite",
            primaryUsedPercent: 3m));

    var result = fixture.Read();
    var today = Today(result);

    AssertClose(0.00515m, today.EstimatedCostUsd, "primary limit increase should keep both deltas at regular rates");
    AssertClose(0m, today.FastEstimatedCostUsd, "primary limit increase should not count as fast usage");
}

static void CodexHistoryIgnoresStalePrimaryLimitForRegularTurns()
{
    using var fixture = new CodexFixture();
    var fastTurnId = Guid.NewGuid().ToString();
    var regularTurnId = Guid.NewGuid().ToString();
    fixture.WriteSessionLog(
        "session.jsonl",
        CodexTurnContextLine("gpt-5.4", fastTurnId),
        CodexTokenCountLine(
            model: null,
            input: 1000,
            cacheRead: 100,
            output: 20,
            limitId: "premium",
            primaryUsedPercent: 3m),
        CodexTurnContextLine("gpt-5.4", regularTurnId),
        CodexTokenCountLine(
            model: null,
            input: 2000,
            cacheRead: 200,
            output: 40,
            limitId: "codex",
            planType: "prolite",
            primaryUsedPercent: 3m));

    var result = fixture.Read();
    var today = Today(result);

    AssertClose(0.007725m, today.EstimatedCostUsd, "stale primary limit should keep the second delta regular");
    AssertClose(0.00515m, today.FastEstimatedCostUsd, "stale primary limit should not count the second delta as fast");
}

static void UsageLabelsPreserveFastSuffix()
{
    var method = typeof(UsageGraphsForm).GetMethod("FriendlyModelLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    Assert(method is not null, "FriendlyModelLabel should exist");

    AssertEqual("5.5", (string)method!.Invoke(null, ["gpt-5.5"])!, "regular model label");
    AssertEqual("5.5 fast", (string)method.Invoke(null, ["gpt-5.5 fast"])!, "fast model label");
}

static void ClaudeHistoryAggregatesTokensAndCost()
{
    using var fixture = new ClaudeFixture();
    fixture.WriteProjectLog("project", "session.jsonl", AssistantLine(
        model: "claude-sonnet-4-5",
        messageId: "msg_1",
        requestId: "req_1",
        input: 1000,
        cacheRead: 100,
        cacheCreate: 200,
        output: 300));

    var result = fixture.Read();
    var today = Today(result);

    AssertEqual(1100L, today.InputTokens, "input includes cache-read tokens");
    AssertEqual(100L, today.CachedInputTokens, "cache read tokens");
    AssertEqual(200L, today.CacheCreationTokens, "cache creation tokens");
    AssertEqual(300L, today.OutputTokens, "output tokens");
    AssertEqual(1600L, today.TotalTokens, "total tokens");
    AssertClose(0.00828m, today.EstimatedCostUsd, "estimated cost");
    Assert(result.Error is null, $"unexpected warning: {result.Error}");
}

static void ClaudeHistoryDedupesRows()
{
    using var fixture = new ClaudeFixture();
    fixture.WriteProjectLog("project", "parent.jsonl",
        AssistantLine("claude-sonnet-4-5", "msg_dup", "req_dup", input: 100, cacheRead: 0, cacheCreate: 0, output: 10),
        AssistantLine("claude-sonnet-4-5", "msg_dup", "req_dup", input: 100, cacheRead: 0, cacheCreate: 0, output: 20));
    fixture.WriteProjectLog(Path.Combine("project", "subagents"), "child.jsonl",
        AssistantLine("claude-sonnet-4-5", "msg_dup", "req_dup", input: 999, cacheRead: 0, cacheCreate: 0, output: 999));

    var today = Today(fixture.Read());
    AssertEqual(100L, today.InputTokens, "parent final input should win");
    AssertEqual(20L, today.OutputTokens, "latest parent streaming chunk should win");
}

static void ClaudeHistoryReportsIncompleteCost()
{
    using var fixture = new ClaudeFixture();
    fixture.WriteProjectLog("project", "session.jsonl", AssistantLine(
        model: "claude-mystery-9",
        messageId: "msg_unknown",
        requestId: "req_unknown",
        input: 100,
        cacheRead: 0,
        cacheCreate: 0,
        output: 10));

    var result = fixture.Read();
    var today = Today(result);
    AssertEqual(110L, today.TotalTokens, "unknown model tokens should still render");
    AssertEqual(0m, today.EstimatedCostUsd, "unknown model cost should be omitted");
    Assert(today.HasIncompleteCost, "daily row should mark incomplete cost");
    Assert(result.Insights?.HasIncompleteCost == true, "insights should mark incomplete cost");
    Assert(result.Error?.Contains("no pricing", StringComparison.OrdinalIgnoreCase) == true, "warning should mention pricing");
}

static void ClaudeHistoryDoesNotRequireCredentials()
{
    using var fixture = new ClaudeFixture();
    fixture.WriteProjectLog("project", "session.jsonl", AssistantLine(
        model: "claude-haiku-4-5",
        messageId: "msg_local",
        requestId: "req_local",
        input: 10,
        cacheRead: 0,
        cacheCreate: 0,
        output: 5));

    var result = fixture.Read();
    Assert(result.Insights is not null, "history should be read from files only");
    AssertEqual(15L, Today(result).TotalTokens, "local tokens");
}

static void ClaudeUsageMapsFableLimit()
{
    var usage = JsonSerializer.Deserialize<ClaudeUsageReader.OAuthUsageResponse>("""
        {
          "five_hour": { "utilization": 12.5, "resets_at": "2026-07-10T12:30:00Z" },
          "seven_day": { "utilization": 34.5, "resets_at": "2026-07-13T12:30:00Z" },
          "seven_day_overage_included": null,
          "limits": [
            {
              "kind": "session",
              "group": "session",
              "percent": 12.5,
              "resets_at": "2026-07-10T12:30:00Z",
              "scope": null
            },
            {
              "kind": "weekly_all",
              "group": "weekly",
              "percent": 34.5,
              "resets_at": "2026-07-13T12:30:00Z",
              "scope": null
            },
            {
              "kind": "weekly_scoped",
              "group": "weekly",
              "percent": 0,
              "resets_at": "2026-07-13T12:30:00Z",
              "scope": {
                "model": { "id": null, "display_name": "Fable" },
                "surface": null
              }
            }
          ]
        }
        """);

    Assert(usage is not null, "Claude usage response should deserialize");
    var snapshot = ClaudeUsageReader.MapUsage(
        usage!,
        planLabel: "max");

    Assert(snapshot.Tertiary is not null, "Fable limit should be present");
    AssertEqual("Fable 5 limit", snapshot.Tertiary!.Title, "Fable limit title");
    AssertClose(0m, (decimal)snapshot.Tertiary.UsedPercent, "Fable utilization");
    AssertEqual(10080, snapshot.Tertiary.WindowMinutes, "Fable weekly window minutes");
    Assert(snapshot.Tertiary.ResetsAt is not null, "Fable reset should be parsed");
}

static void CursorUsageKeepsFractionalPercents()
{
    var snapshot = CursorUsageReader.MapUsage(new CursorUsageSummaryResponse(
        BillingCycleStart: "2026-03-18T20:45:42.000Z",
        BillingCycleEnd: "2026-04-18T20:45:42.000Z",
        MembershipType: "pro",
        LimitType: "user",
        IsUnlimited: false,
        AutoModelSelectedDisplayMessage: null,
        NamedModelSelectedDisplayMessage: null,
        IndividualUsage: new CursorIndividualUsageResponse(
            Plan: new CursorPlanUsageResponse(
                Enabled: true,
                Used: 86,
                Limit: 2000,
                Remaining: 1914,
                Breakdown: new CursorPlanBreakdownResponse(86, 0, 86),
                AutoPercentUsed: 0.36,
                ApiPercentUsed: 0.7111111111111111,
                TotalPercentUsed: 0.441025641025641),
            OnDemand: new CursorOnDemandUsageResponse(false, 0, null, null),
            Overall: null),
        TeamUsage: null));

    AssertClose(0.441025641025641m, (decimal)snapshot.Primary.UsedPercent, "cursor total percent");
    AssertClose(0.36m, (decimal)snapshot.Secondary!.UsedPercent, "cursor auto percent");
    AssertClose(0.7111111111111111m, (decimal)snapshot.Tertiary!.UsedPercent, "cursor api percent");
    AssertEqual("Cursor Pro", snapshot.PlanType!, "cursor plan label");
    AssertEqual(44640, snapshot.Primary.WindowMinutes, "cursor billing-cycle minutes");
}

static void CursorEnterpriseOverallDrivesHeadline()
{
    var snapshot = CursorUsageReader.MapUsage(new CursorUsageSummaryResponse(
        BillingCycleStart: "2026-04-01T00:00:00.000Z",
        BillingCycleEnd: "2026-05-01T00:00:00.000Z",
        MembershipType: "enterprise",
        LimitType: "team",
        IsUnlimited: false,
        AutoModelSelectedDisplayMessage: null,
        NamedModelSelectedDisplayMessage: null,
        IndividualUsage: new CursorIndividualUsageResponse(
            Plan: null,
            OnDemand: null,
            Overall: new CursorOverallUsageResponse(true, 7384, 10000, 2616)),
        TeamUsage: new CursorTeamUsageResponse(
            OnDemand: new CursorOnDemandUsageResponse(true, 0, null, null),
            Pooled: new CursorPooledUsageResponse(true, 12_725_135, 28_122_000, 15_396_865))));

    AssertClose(73.84m, (decimal)snapshot.Primary.UsedPercent, "enterprise personal cap percent");
    AssertEqual("Cursor Enterprise", snapshot.PlanType!, "enterprise plan label");
}

static void CursorLegacyRequestsDrivePrimary()
{
    var snapshot = CursorUsageReader.MapUsage(
        new CursorUsageSummaryResponse(
            BillingCycleStart: null,
            BillingCycleEnd: null,
            MembershipType: "enterprise",
            LimitType: null,
            IsUnlimited: null,
            AutoModelSelectedDisplayMessage: null,
            NamedModelSelectedDisplayMessage: null,
            IndividualUsage: null,
            TeamUsage: null),
        requestUsage: new CursorUsageResponse(
            Gpt4: new CursorModelUsageResponse(
                NumRequests: 120,
                NumRequestsTotal: 240,
                NumTokens: null,
                MaxRequestUsage: 500,
                MaxTokenUsage: null),
            StartOfMonth: null));

    AssertEqual("Requests", snapshot.Primary.Title, "legacy primary title");
    AssertClose(48m, (decimal)snapshot.Primary.UsedPercent, "legacy request percent");
}

static void CursorCookieHeaderNormalizationTrimsPrefix()
{
    var normalized = CursorUsageReader.NormalizeCookieHeader("  Cookie: WorkosCursorSessionToken=abc; foo=bar  ");
    AssertEqual("WorkosCursorSessionToken=abc; foo=bar", normalized, "normalized cursor cookie header");

    var bare = CursorUsageReader.NormalizeCookieHeader("abc123");
    AssertEqual("WorkosCursorSessionToken=abc123", bare, "bare cursor token should become a Cookie header");

    var lower = CursorUsageReader.NormalizeCookieHeader("workoscursorsessiontoken=abc; next-auth.session-token=def");
    AssertEqual(
        "WorkosCursorSessionToken=abc; next-auth.session-token=def",
        lower,
        "known cursor cookie names should use canonical casing");
}

static ProviderDailyUsage Today(ProviderUsageInsightsLookupResult result)
{
    Assert(result.Insights is not null, result.Error ?? "missing insights");
    var today = DateOnly.FromDateTime(DateTimeOffset.Now.DateTime);
    return result.Insights!.Daily.Single(day => day.Day == today);
}

static string AssistantLine(string model, string messageId, string requestId, long input, long cacheRead, long cacheCreate, long output)
{
    var payload = new
    {
        type = "assistant",
        timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        requestId,
        message = new
        {
            id = messageId,
            model,
            usage = new
            {
                input_tokens = input,
                cache_read_input_tokens = cacheRead,
                cache_creation_input_tokens = cacheCreate,
                output_tokens = output
            }
        }
    };

    return JsonSerializer.Serialize(payload);
}

static string CodexTurnContextLine(string model, string? turnId = null)
{
    var payload = new
    {
        timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        type = "turn_context",
        payload = new
        {
            turn_id = turnId,
            model
        }
    };

    return JsonSerializer.Serialize(payload);
}

static string CodexTokenCountLine(
    string? model,
    long input,
    long cacheRead,
    long output,
    string limitId,
    string planType = "plus",
    decimal primaryUsedPercent = 0m)
{
    var payload = new
    {
        timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            model,
            info = new
            {
                total_token_usage = new
                {
                    input_tokens = input,
                    cached_input_tokens = cacheRead,
                    output_tokens = output,
                    total_tokens = input + output
                }
            },
            rate_limits = new
            {
                limit_id = limitId,
                primary = new
                {
                    used_percent = primaryUsedPercent,
                    window_minutes = 300,
                    resets_at = DateTimeOffset.Now.AddHours(5).ToUnixTimeSeconds()
                },
                plan_type = planType
            }
        }
    };

    return JsonSerializer.Serialize(payload);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message) where T : IEquatable<T>
{
    if (!expected.Equals(actual))
    {
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}

static void AssertClose(decimal expected, decimal actual, string message)
{
    if (Math.Abs(expected - actual) > 0.000001m)
    {
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}

sealed class CodexFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "codexbar-codex-tests", Guid.NewGuid().ToString("N"));
    private readonly string? originalPiHome;

    public CodexFixture()
    {
        originalPiHome = Environment.GetEnvironmentVariable("PI_HOME");
        Environment.SetEnvironmentVariable("PI_HOME", Path.Combine(root, "pi"));
    }

    public void WriteSessionLog(string fileName, params string[] lines)
    {
        var dir = Path.Combine(root, "codex", "sessions");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(root, "pi", "agent", "sessions"));
        var file = Path.Combine(dir, fileName);
        File.WriteAllLines(file, lines);
        File.SetLastWriteTime(file, DateTime.Now);
    }

    public void WriteCodexLog(string fileName, params string[] lines)
    {
        var file = Path.Combine(root, "codex", fileName);
        File.WriteAllLines(file, lines);
        File.SetLastWriteTime(file, DateTime.Now);
    }

    public ProviderUsageInsightsLookupResult Read()
    {
        return new CodexUsageInsightsReader(Path.Combine(root, "codex")).ReadLatest();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PI_HOME", originalPiHome);
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    }
}

sealed class ClaudeFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "codexbar-claude-tests", Guid.NewGuid().ToString("N"));

    public void WriteProjectLog(string projectPath, string fileName, params string[] lines)
    {
        var dir = Path.Combine(root, projectPath);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, fileName);
        File.WriteAllLines(file, lines);
        File.SetLastWriteTime(file, DateTime.Now);
    }

    public ProviderUsageInsightsLookupResult Read()
    {
        return new ClaudeUsageInsightsReader([root], refreshModelsDevPricing: false).ReadLatest();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
