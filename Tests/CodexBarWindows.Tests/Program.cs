using System.Globalization;
using System.Text.Json;
using CodexBarWindows;

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
    ("Claude history aggregates tokens and cost", ClaudeHistoryAggregatesTokensAndCost),
    ("Claude history dedupes streaming and subagent rows", ClaudeHistoryDedupesRows),
    ("Claude history reports incomplete cost for unknown models", ClaudeHistoryReportsIncompleteCost),
    ("Claude history is usable without Claude credentials", ClaudeHistoryDoesNotRequireCredentials),
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
