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

if (args.Contains("--bench-real-history", StringComparer.OrdinalIgnoreCase))
{
    // Times a cold scan against the real session logs, then a warm scan served from the
    // per-file row cache, and verifies both agree.
    foreach (var (name, read) in new (string, Func<ProviderUsageInsightsLookupResult>)[]
    {
        ("codex", () => new CodexUsageInsightsReader().ReadLatest()),
        ("claude", () => new ClaudeUsageInsightsReader(null, refreshModelsDevPricing: false).ReadLatest()),
    })
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var cold = read();
        var coldMs = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();
        var warm = read();
        var warmMs = stopwatch.ElapsedMilliseconds;
        var matches = cold.Insights?.Last30DaysTokens == warm.Insights?.Last30DaysTokens &&
            cold.Insights?.Last30DaysEstimatedCostUsd == warm.Insights?.Last30DaysEstimatedCostUsd;
        Console.WriteLine($"{name}: cold={coldMs}ms warm={warmMs}ms tokens={warm.Insights?.Last30DaysTokens} agree={matches}");
    }

    return;
}

if (args.Contains("--scan-real-reset-credits", StringComparer.OrdinalIgnoreCase))
{
    // Read-only: prints the banked reset inventory the popup renders from.
    var result = new CodexUsageReader().ReadLatest();
    Console.WriteLine($"error={result.Error ?? "<none>"}");
    var credits = result.Snapshot?.ResetCredits;
    Console.WriteLine($"hasResetCredits={credits is not null}");
    if (credits is not null)
    {
        Console.WriteLine($"availableCount={credits.AvailableCount}");
        Console.WriteLine($"redeemable={credits.AvailableByExpiry.Count}");
        Console.WriteLine($"next={credits.NextExpiring?.Id ?? "<none>"} expires={credits.NextExpiring?.ExpiresAt?.ToString("u") ?? "<never>"}");
        foreach (var credit in credits.Credits)
        {
            Console.WriteLine($"  {credit.Id} status={credit.Status} title={credit.DisplayTitle} expires={credit.ExpiresAt?.ToString("u") ?? "<never>"}");
        }
    }

    return;
}

if (args.Contains("--probe-reset-rejection", StringComparer.OrdinalIgnoreCase))
{
    // Exercises the whole redeem path — spawn, handshake, request shape, outcome parsing —
    // against an id that cannot exist, so no real credit can be spent.
    var probe = new CodexResetCreditRedeemer("probe-account", null)
        .Redeem("RateLimitResetCredit_definitely_not_real_probe");
    Console.WriteLine($"outcome={probe.Outcome} error={probe.Error ?? "<none>"} changedUsage={probe.ChangedUsage}");
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
    ("Codex RPC prefers the codex multi-bucket snapshot", CodexRpcPrefersCodexMultiBucketSnapshot),
    ("Codex RPC rejects a non-codex compatibility bucket", CodexRpcRejectsNonCodexCompatibilityBucket),
    ("Codex RPC accepts a weekly-only payload", CodexRpcAcceptsWeeklyOnlyPayload),
    ("Codex RPC discovers dynamically named windows", CodexRpcDiscoversDynamicWindows),
    ("Codex limits establish an initial window consensus", CodexLimitsEstablishInitialConsensus),
    ("Codex limits reject an isolated conflicting window", CodexLimitsRejectIsolatedConflict),
    ("Codex limits accept a repeatedly confirmed replacement", CodexLimitsAcceptConfirmedReplacement),
    ("Codex limits retain the last snapshot on refresh failure", CodexLimitsRetainLastSnapshotOnFailure),
    ("Codex limits accept a post-reset drop once invalidated", CodexLimitsAcceptPostResetDropOnceInvalidated),
    ("Codex RPC parses banked reset credits", CodexRpcParsesBankedResetCredits),
    ("Codex reset credits fall back to the available count", CodexResetCreditsFallBackToAvailableCount),
    ("Codex reset credits drop rows without a redeemable id", CodexResetCreditsDropRowsWithoutId),
    ("Codex reset credits are absent on older CLI builds", CodexResetCreditsAbsentOnOlderCli),
    ("Codex reset outcomes map every documented value", CodexResetOutcomesMapDocumentedValues),
    ("Usage tooltip windows reflect their actual duration", UsageTooltipWindowsReflectDuration),
    ("Codex history aggregates token_count rows", CodexHistoryAggregatesTokenCountRows),
    ("Codex history prices gpt-5.6 at its published rates", CodexHistoryPricesGpt56AtPublishedRates),
    ("Codex history skips a replayed subagent prefix", CodexHistorySkipsReplayedSubagentPrefix),
    ("Codex history ignores repeated cumulative snapshots", CodexHistoryIgnoresRepeatedCumulativeSnapshots),
    ("Codex history suppresses an unowned copied prefix", CodexHistorySuppressesUnownedCopiedPrefix),
    ("Codex history counts premium token_count rows as fast", CodexHistoryCountsPremiumTokenCountRowsAsFast),
    ("Codex history treats prolite token_count rows as regular", CodexHistoryTreatsProliteTokenCountRowsAsRegular),
    ("Codex history counts priority service tier turns as fast", CodexHistoryCountsPriorityServiceTierTurnsAsFast),
    ("Codex history counts priority client metadata turns as fast", CodexHistoryCountsPriorityClientMetadataTurnsAsFast),
    ("Codex history treats primary limit increase as regular", CodexHistoryTreatsPrimaryLimitIncreaseAsRegular),
    ("Codex history ignores stale primary limit for regular turns", CodexHistoryIgnoresStalePrimaryLimitForRegularTurns),
    ("Codex history rescans a session file after it changes", CodexHistoryRescansChangedSessionFiles),
    ("Usage labels preserve fast suffix", UsageLabelsPreserveFastSuffix),
    ("Claude history aggregates tokens and cost", ClaudeHistoryAggregatesTokensAndCost),
    ("Claude history dedupes streaming and subagent rows", ClaudeHistoryDedupesRows),
    ("Claude history reports incomplete cost for unknown models", ClaudeHistoryReportsIncompleteCost),
    ("Claude history is usable without Claude credentials", ClaudeHistoryDoesNotRequireCredentials),
    ("Claude history rescans a session file after it changes", ClaudeHistoryRescansChangedSessionFiles),
    ("Claude usage maps Fable from scoped limits", ClaudeUsageMapsScopedFableLimit),
    ("Claude usage maps Fable from the legacy window", ClaudeUsageMapsLegacyFableLimit),
    ("Claude usage omits Fable when Anthropic omits it", ClaudeUsageOmitsMissingFableLimit),
    ("Claude plan includes the dynamic Max multiplier", ClaudePlanIncludesMultiplier),
    ("Provider plan labels map Codex Pro tiers", ProviderPlanLabelsMapCodexTiers),
    ("Cursor usage keeps fractional percent fields", CursorUsageKeepsFractionalPercents),
    ("Cursor enterprise overall drives headline", CursorEnterpriseOverallDrivesHeadline),
    ("Cursor legacy request usage drives primary", CursorLegacyRequestsDrivePrimary),
    ("Cursor cookie header normalization trims prefix", CursorCookieHeaderNormalizationTrimsPrefix),
};

static void CodexRpcPrefersCodexMultiBucketSnapshot()
{
    const string response = """
        {
          "id": 2,
          "result": {
            "rateLimits": {
              "limitId": "codex_bengalfox",
              "primary": { "usedPercent": 0, "windowDurationMins": 300, "resetsAt": 1783647000 },
              "secondary": { "usedPercent": 0, "windowDurationMins": 10080, "resetsAt": 1784234000 }
            },
            "rateLimitsByLimitId": {
              "codex_bengalfox": {
                "limitId": "codex_bengalfox",
                "primary": { "usedPercent": 0, "windowDurationMins": 300, "resetsAt": 1783647000 },
                "secondary": { "usedPercent": 0, "windowDurationMins": 10080, "resetsAt": 1784234000 }
              },
              "codex": {
                "limitId": "codex",
                "primary": { "usedPercent": 42, "windowDurationMins": 300, "resetsAt": 1783637100 },
                "secondary": { "usedPercent": 9, "windowDurationMins": 10080, "resetsAt": 1784223900 },
                "planType": "plus"
              }
            }
          }
        }
        """;

    var snapshot = CodexUsageReader.ParseRpcSnapshot(response, "test")
        ?? throw new InvalidOperationException("expected a parsed Codex snapshot");

    AssertEqual(42d, snapshot.Primary.UsedPercent, "preferred Codex used percent");
    AssertEqual(300, snapshot.Primary.WindowMinutes, "preferred Codex window length");
    AssertEqual("plus", snapshot.PlanType ?? string.Empty, "preferred Codex plan");
}

static void CodexRpcRejectsNonCodexCompatibilityBucket()
{
    const string response = """
        {
          "id": 2,
          "result": {
            "rateLimits": {
              "limitId": "codex_bengalfox",
              "primary": { "usedPercent": 0, "windowDurationMins": 300, "resetsAt": 1783647000 },
              "secondary": null
            }
          }
        }
        """;

    Assert(
        CodexUsageReader.ParseRpcSnapshot(response, "test") is null,
        "a non-codex compatibility bucket must not drive the Codex headline");
}

static void CodexRpcAcceptsWeeklyOnlyPayload()
{
    const string response = """
        {
          "id": 2,
          "result": {
            "rateLimitsByLimitId": {
              "codex": {
                "limitId": "codex",
                "weekly": {
                  "usedPercent": 23,
                  "windowDurationMins": 10080,
                  "resetsAt": 1784234000
                },
                "planType": "prolite"
              }
            }
          }
        }
        """;

    var snapshot = CodexUsageReader.ParseRpcSnapshot(response, "test")
        ?? throw new InvalidOperationException("expected a weekly-only Codex snapshot");
    var providerSnapshot = new UsageLookupResult(snapshot, null).ToProviderResult().Snapshot
        ?? throw new InvalidOperationException("expected a provider snapshot");

    AssertEqual(1, snapshot.Windows.Count, "weekly-only window count");
    AssertEqual(10080, snapshot.Primary.WindowMinutes, "weekly-only primary duration");
    AssertEqual(1, providerSnapshot.Windows.Count, "weekly-only provider row count");
    AssertEqual("Weekly limit", providerSnapshot.Windows[0].Title, "weekly-only row title");
}

static void CodexRpcDiscoversDynamicWindows()
{
    const string response = """
        {
          "id": 2,
          "result": {
            "rateLimitsByLimitId": {
              "codex": {
                "limitId": "codex",
                "primary": {
                  "usedPercent": 8,
                  "windowDurationMins": 300,
                  "resetsAt": 1783647000
                },
                "windows": [
                  {
                    "used_percent": "23",
                    "window_minutes": "10080",
                    "resets_at": "1784234000"
                  },
                  {
                    "utilization": 4.5,
                    "windowDurationMins": 43200
                  }
                ],
                "planType": "pro"
              }
            }
          }
        }
        """;

    var snapshot = CodexUsageReader.ParseRpcSnapshot(response, "test")
        ?? throw new InvalidOperationException("expected a dynamic Codex snapshot");
    var providerSnapshot = new UsageLookupResult(snapshot, null).ToProviderResult().Snapshot
        ?? throw new InvalidOperationException("expected a provider snapshot");

    AssertEqual(3, snapshot.Windows.Count, "dynamic window count");
    AssertEqual(300, snapshot.Windows[0].WindowMinutes, "first dynamic window");
    AssertEqual(10080, snapshot.Windows[1].WindowMinutes, "second dynamic window");
    AssertEqual(43200, snapshot.Windows[2].WindowMinutes, "third dynamic window");
    AssertEqual(3, providerSnapshot.Windows.Count, "dynamic provider row count");
    AssertEqual("30d", TrayApplicationContext.ShortWindow(providerSnapshot.Windows[2].WindowMinutes), "dynamic monthly duration");
    Assert(providerSnapshot.Windows[2].ResetsAt is null, "missing reset should remain optional");
}

static void CodexLimitsEstablishInitialConsensus()
{
    var now = DateTimeOffset.Now;
    var stabilizer = new CodexRateLimitStabilizer();
    var reset = now.AddHours(2);
    var weeklyReset = now.AddDays(5);
    var result = stabilizer.Stabilize(
        [
            Success(CodexSnapshot(now, 80, reset, 12, weeklyReset)),
            Success(CodexSnapshot(now.AddSeconds(1), 13, reset, 2, weeklyReset)),
            Success(CodexSnapshot(now.AddSeconds(2), 81, reset.AddSeconds(1), 12, weeklyReset.AddSeconds(1)))
        ],
        now);

    AssertEqual(81d, result.Snapshot?.Primary.UsedPercent ?? -1, "consensus used percent");
    Assert(result.Error is null, $"unexpected consensus warning: {result.Error}");
}

static void CodexLimitsRejectIsolatedConflict()
{
    var now = DateTimeOffset.Now;
    var stabilizer = InitializedStabilizer(now, usedPercent: 80);
    var conflict = Success(CodexSnapshot(now.AddMinutes(1), 13, now.AddHours(3), 2, now.AddDays(6)));

    var result = stabilizer.Stabilize([conflict], now.AddMinutes(1));

    AssertEqual(80d, result.Snapshot?.Primary.UsedPercent ?? -1, "last confirmed percent");
    Assert(result.Error?.Contains("conflicting", StringComparison.OrdinalIgnoreCase) == true, "conflict warning");
    AssertEqual(result.Error ?? string.Empty, result.ToProviderResult().Error ?? string.Empty, "provider warning propagation");
}

static void CodexLimitsAcceptConfirmedReplacement()
{
    var now = DateTimeOffset.Now;
    var stabilizer = InitializedStabilizer(now, usedPercent: 80);
    var replacementReset = now.AddHours(3);
    UsageLookupResult? result = null;

    for (var attempt = 1; attempt <= 3; attempt++)
    {
        result = stabilizer.Stabilize(
            [Success(CodexSnapshot(now.AddMinutes(attempt), 13 + attempt, replacementReset, 2, now.AddDays(6)))],
            now.AddMinutes(attempt));
    }

    AssertEqual(16d, result?.Snapshot?.Primary.UsedPercent ?? -1, "confirmed replacement percent");
    Assert(result?.Error is null, $"unexpected replacement warning: {result?.Error}");
}

static void CodexLimitsRetainLastSnapshotOnFailure()
{
    var now = DateTimeOffset.Now;
    var stabilizer = InitializedStabilizer(now, usedPercent: 80);

    var result = stabilizer.Stabilize([new UsageLookupResult(null, "RPC timeout")], now.AddMinutes(1));

    AssertEqual(80d, result.Snapshot?.Primary.UsedPercent ?? -1, "retained percent");
    Assert(result.Error?.Contains("RPC timeout", StringComparison.Ordinal) == true, "refresh failure warning");
}

static void CodexLimitsAcceptPostResetDropOnceInvalidated()
{
    var now = DateTimeOffset.Now;
    var stabilizer = InitializedStabilizer(now, usedPercent: 98);

    // A redeemed reset drops usage and moves the reset time, which is exactly the shape the
    // stabilizer suppresses as a conflicting sample.
    var later = now.AddMinutes(1);
    var postReset = Success(CodexSnapshot(later, 0, later.AddHours(5), 0, later.AddDays(7)));
    var suppressed = stabilizer.Stabilize([postReset], later);
    AssertEqual(98d, suppressed.Snapshot?.Primary.UsedPercent ?? -1, "pre-invalidation percent");

    stabilizer.InvalidateAcceptedSnapshot();
    Assert(stabilizer.NeedsInitialConsensus, "invalidated stabilizer re-samples for consensus");

    var accepted = stabilizer.Stabilize(
        [
            Success(CodexSnapshot(later, 0, later.AddHours(5), 0, later.AddDays(7))),
            Success(CodexSnapshot(later.AddSeconds(1), 0, later.AddHours(5), 0, later.AddDays(7))),
            Success(CodexSnapshot(later.AddSeconds(2), 0, later.AddHours(5), 0, later.AddDays(7)))
        ],
        later);

    AssertEqual(0d, accepted.Snapshot?.Primary.UsedPercent ?? -1, "post-reset percent");
    Assert(accepted.Error is null, $"unexpected post-reset warning: {accepted.Error}");
}

static void CodexRpcParsesBankedResetCredits()
{
    const string response = """
        {
          "id": 2,
          "result": {
            "rateLimitsByLimitId": {
              "codex": {
                "limitId": "codex",
                "primary": { "usedPercent": 97, "windowDurationMins": 10080, "resetsAt": 1785675498 },
                "planType": "prolite"
              }
            },
            "rateLimitResetCredits": {
              "availableCount": 3,
              "credits": [
                {
                  "id": "RateLimitResetCredit_later",
                  "resetType": "codexRateLimits",
                  "status": "available",
                  "grantedAt": 1783965917,
                  "expiresAt": 1786557917,
                  "title": "Full reset",
                  "description": "You've been granted one free rate limit reset."
                },
                {
                  "id": "RateLimitResetCredit_soonest",
                  "resetType": "codexRateLimits",
                  "status": "available",
                  "grantedAt": 1782938127,
                  "expiresAt": 1785530127,
                  "title": "Full reset",
                  "description": "You've been granted one free rate limit reset."
                },
                {
                  "id": "RateLimitResetCredit_spent",
                  "resetType": "codexRateLimits",
                  "status": "redeemed",
                  "grantedAt": 1781000000,
                  "expiresAt": 1783000000,
                  "title": "Full reset",
                  "description": null
                }
              ]
            }
          }
        }
        """;

    var snapshot = CodexUsageReader.ParseRpcSnapshot(response, "test")
        ?? throw new InvalidOperationException("expected a parsed Codex snapshot");
    var credits = snapshot.ResetCredits
        ?? throw new InvalidOperationException("expected banked reset credits");

    AssertEqual(3, credits.AvailableCount, "available reset count");
    AssertEqual(3, credits.Credits.Count, "parsed reset detail rows");
    AssertEqual(2, credits.AvailableByExpiry.Count, "redeemed credits are not redeemable");
    AssertEqual("RateLimitResetCredit_soonest", credits.NextExpiring?.Id ?? "", "soonest-expiring credit wins");
    AssertEqual("Full reset", credits.NextExpiring?.DisplayTitle ?? "", "credit display title");
    Assert(credits.NextExpiring?.ExpiresAt is not null, "credit expiry is parsed");
    Assert(credits.Find("RateLimitResetCredit_later") is not null, "credits are addressable by id");
    Assert(credits.Find("RateLimitResetCredit_missing") is null, "unknown credit ids do not resolve");

    var providerSnapshot = new UsageLookupResult(snapshot, null).ToProviderResult().Snapshot
        ?? throw new InvalidOperationException("expected a provider snapshot");
    AssertEqual(3, providerSnapshot.ResetCredits?.AvailableCount ?? -1, "credits reach the provider snapshot");
}

static void CodexResetCreditsFallBackToAvailableCount()
{
    // The backend may report only a count; there is then no id to redeem explicitly.
    const string response = """
        {
          "id": 2,
          "result": {
            "rateLimitsByLimitId": {
              "codex": {
                "limitId": "codex",
                "primary": { "usedPercent": 12, "windowDurationMins": 300, "resetsAt": 1785675498 }
              }
            },
            "rateLimitResetCredits": { "availableCount": 2, "credits": null }
          }
        }
        """;

    var credits = CodexUsageReader.ParseRpcSnapshot(response, "test")?.ResetCredits
        ?? throw new InvalidOperationException("expected banked reset credits");

    AssertEqual(2, credits.AvailableCount, "count-only available resets");
    AssertEqual(0, credits.Credits.Count, "count-only detail rows");
    Assert(credits.HasAny, "count-only inventory still reports availability");
    Assert(credits.NextExpiring is null, "count-only inventory offers no redeemable id");
}

static void CodexResetCreditsDropRowsWithoutId()
{
    const string response = """
        {
          "id": 2,
          "result": {
            "rateLimitsByLimitId": {
              "codex": {
                "limitId": "codex",
                "primary": { "usedPercent": 99, "windowDurationMins": 300, "resetsAt": 1785675498 }
              }
            },
            "rateLimitResetCredits": {
              "availableCount": 2,
              "credits": [
                { "status": "available", "expiresAt": 1785530127, "title": "Full reset" },
                { "id": "RateLimitResetCredit_ok", "status": "available", "expiresAt": 1786557917 }
              ]
            }
          }
        }
        """;

    var credits = CodexUsageReader.ParseRpcSnapshot(response, "test")?.ResetCredits
        ?? throw new InvalidOperationException("expected banked reset credits");

    AssertEqual(1, credits.Credits.Count, "rows without an id are unusable");
    AssertEqual("RateLimitResetCredit_ok", credits.NextExpiring?.Id ?? "", "only addressable credits are offered");
    AssertEqual(2, credits.AvailableCount, "backend count is preserved when rows are capped");
}

static void CodexResetCreditsAbsentOnOlderCli()
{
    const string response = """
        {
          "id": 2,
          "result": {
            "rateLimitsByLimitId": {
              "codex": {
                "limitId": "codex",
                "primary": { "usedPercent": 42, "windowDurationMins": 300, "resetsAt": 1785675498 }
              }
            }
          }
        }
        """;

    var snapshot = CodexUsageReader.ParseRpcSnapshot(response, "test")
        ?? throw new InvalidOperationException("expected a parsed Codex snapshot");

    Assert(snapshot.ResetCredits is null, "a CLI without reset credits must not fabricate an inventory");
}

static void CodexResetOutcomesMapDocumentedValues()
{
    Assert(
        CodexResetCreditRedeemer.ParseOutcome("""{"id":2,"result":{"outcome":"reset"}}""") == CodexResetOutcome.Reset,
        "reset outcome");
    Assert(
        CodexResetCreditRedeemer.ParseOutcome("""{"id":2,"result":{"outcome":"nothingToReset"}}""") == CodexResetOutcome.NothingToReset,
        "nothingToReset outcome");
    Assert(
        CodexResetCreditRedeemer.ParseOutcome("""{"id":2,"result":{"outcome":"noCredit"}}""") == CodexResetOutcome.NoCredit,
        "noCredit outcome");
    Assert(
        CodexResetCreditRedeemer.ParseOutcome("""{"id":2,"result":{"outcome":"alreadyRedeemed"}}""") == CodexResetOutcome.AlreadyRedeemed,
        "alreadyRedeemed outcome");

    // Only a Reset actually moved the usage windows, so only it should trigger a re-read.
    Assert(new CodexResetRedeemResult(CodexResetOutcome.Reset).ChangedUsage, "a reset changes usage");
    Assert(!new CodexResetRedeemResult(CodexResetOutcome.NothingToReset).ChangedUsage, "nothingToReset leaves usage alone");
    Assert(!new CodexResetRedeemResult(CodexResetOutcome.Failed, "boom").ChangedUsage, "a failure leaves usage alone");

    // An unreadable reply must stay indefinite so the idempotency key is retained.
    Assert(
        CodexResetCreditRedeemer.ParseOutcome("""{"id":2,"result":{"outcome":"somethingNew"}}""") is null,
        "unknown outcomes are not treated as definitive");
    Assert(
        CodexResetCreditRedeemer.ParseOutcome("""{"id":2,"result":{}}""") is null,
        "a missing outcome is not treated as definitive");
}

static void UsageTooltipWindowsReflectDuration()
{
    AssertEqual("5h", TrayApplicationContext.ShortWindow(300), "five-hour tooltip label");
    AssertEqual("7d", TrayApplicationContext.ShortWindow(10080), "weekly tooltip label");
    AssertEqual("30d", TrayApplicationContext.ShortWindow(43200), "monthly tooltip label");
}

static CodexRateLimitStabilizer InitializedStabilizer(DateTimeOffset now, double usedPercent)
{
    var stabilizer = new CodexRateLimitStabilizer();
    var reset = now.AddHours(2);
    var weeklyReset = now.AddDays(5);
    var samples = Enumerable.Range(0, 3)
        .Select(index => Success(CodexSnapshot(
            now.AddSeconds(index),
            usedPercent,
            reset.AddSeconds(index),
            12,
            weeklyReset.AddSeconds(index))))
        .ToArray();
    var initial = stabilizer.Stabilize(samples, now);
    Assert(initial.HasSnapshot, "stabilizer initialization");
    return stabilizer;
}

static UsageLookupResult Success(CodexRateLimitSnapshot snapshot)
{
    return new UsageLookupResult(snapshot, null);
}

static CodexRateLimitSnapshot CodexSnapshot(
    DateTimeOffset observedAt,
    double usedPercent,
    DateTimeOffset reset,
    double weeklyUsedPercent,
    DateTimeOffset weeklyReset)
{
    return new CodexRateLimitSnapshot(
        observedAt,
        "prolite",
        new UsageWindow(usedPercent, 300, reset),
        new UsageWindow(weeklyUsedPercent, 10080, weeklyReset),
        "test");
}

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

static void CodexHistoryPricesGpt56AtPublishedRates()
{
    using var fixture = new CodexFixture();
    fixture.WriteSessionLog("session.jsonl", CodexTokenCountLine(
        model: "gpt-5.6-sol",
        input: 1000,
        cacheRead: 100,
        output: 20,
        limitId: "codex"));

    // 900 uncached input at $5/M, 100 cached at $0.50/M, 20 output at $30/M.
    AssertClose(0.00515m, Today(fixture.Read()).EstimatedCostUsd, "gpt-5.6-sol must not borrow gpt-5 rates");

    using var aliasFixture = new CodexFixture();
    aliasFixture.WriteSessionLog("session.jsonl", CodexTokenCountLine(
        model: "gpt-5.6",
        input: 1000,
        cacheRead: 100,
        output: 20,
        limitId: "codex"));

    AssertClose(0.00515m, Today(aliasFixture.Read()).EstimatedCostUsd, "the bare gpt-5.6 alias routes to Sol");
}

static void CodexHistorySkipsReplayedSubagentPrefix()
{
    using var fixture = new CodexFixture();
    fixture.WriteSessionLog(
        "session.jsonl",
        CodexSessionMetaLine("leaf-session", forkedFromId: "parent-session"),
        CodexCumulativeTokenCountLine(totalInput: 1000, totalCached: 100, totalOutput: 20, lastInput: 1000, lastCached: 100, lastOutput: 20),
        CodexSessionMetaLine("ancestor-session"),
        CodexCumulativeTokenCountLine(totalInput: 6000, totalCached: 600, totalOutput: 120, lastInput: 5000, lastCached: 500, lastOutput: 100),
        CodexTurnContextLine("gpt-5.4"),
        CodexInterAgentTriggerLine(),
        CodexCumulativeTokenCountLine(totalInput: 7000, totalCached: 700, totalOutput: 140, lastInput: 1000, lastCached: 100, lastOutput: 20));

    var today = Today(fixture.Read());

    AssertEqual(1000L, today.InputTokens, "only the turns after the owned-suffix boundary count");
    AssertEqual(100L, today.CachedInputTokens, "replayed cached input must not be counted again");
    AssertEqual(20L, today.OutputTokens, "replayed output must not be counted again");
    AssertClose(0.002575m, today.EstimatedCostUsd, "replayed ancestor turns must not be billed");
}

static void CodexHistoryIgnoresRepeatedCumulativeSnapshots()
{
    using var fixture = new CodexFixture();
    fixture.WriteSessionLog(
        "session.jsonl",
        CodexTurnContextLine("gpt-5.4"),
        CodexCumulativeTokenCountLine(totalInput: 1000, totalCached: 100, totalOutput: 20, lastInput: 1000, lastCached: 100, lastOutput: 20),
        CodexCumulativeTokenCountLine(totalInput: 1000, totalCached: 100, totalOutput: 20, lastInput: 1000, lastCached: 100, lastOutput: 20));

    var today = Today(fixture.Read());

    AssertEqual(1020L, today.TotalTokens, "an exact cumulative re-emission is a replay, not new usage");
}

static void CodexHistorySuppressesUnownedCopiedPrefix()
{
    using var fixture = new CodexFixture();
    fixture.WriteSessionLog(
        "session.jsonl",
        CodexSessionMetaLine("leaf-session"),
        CodexCumulativeTokenCountLine(totalInput: 1000, totalCached: 100, totalOutput: 20, lastInput: 1000, lastCached: 100, lastOutput: 20),
        CodexSessionMetaLine("ancestor-session"),
        CodexCumulativeTokenCountLine(totalInput: 6000, totalCached: 600, totalOutput: 120, lastInput: 5000, lastCached: 500, lastOutput: 100));

    var result = fixture.Read();

    AssertEqual(0L, result.Insights!.Last30DaysTokens, "a copied prefix with no owned turns belongs to the rollout it was copied from");
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

static void CodexHistoryRescansChangedSessionFiles()
{
    using var fixture = new CodexFixture();
    fixture.WriteSessionLog("session.jsonl", CodexTokenCountLine(
        model: "gpt-5.4", input: 1000, cacheRead: 100, output: 20, limitId: "codex"));

    var first = Today(fixture.Read());
    // The second read of the unchanged file is served from the per-file row cache.
    var second = Today(fixture.Read());
    AssertEqual(first.TotalTokens, second.TotalTokens, "cached codex totals match a fresh scan");
    AssertClose(first.EstimatedCostUsd, second.EstimatedCostUsd, "cached codex cost matches a fresh scan");

    fixture.WriteSessionLog("session.jsonl",
        CodexTokenCountLine(model: "gpt-5.4", input: 1000, cacheRead: 100, output: 20, limitId: "codex"),
        CodexTokenCountLine(model: "gpt-5.4", input: 3000, cacheRead: 100, output: 60, limitId: "codex"));

    var third = Today(fixture.Read());
    AssertEqual(3000L, third.InputTokens, "changed codex file is rescanned");
    AssertEqual(60L, third.OutputTokens, "changed codex file output tokens");
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

static void ClaudeHistoryRescansChangedSessionFiles()
{
    using var fixture = new ClaudeFixture();
    fixture.WriteProjectLog("project", "session.jsonl",
        AssistantLine("claude-sonnet-4-5", "msg_1", "req_1", input: 1000, cacheRead: 100, cacheCreate: 200, output: 300));

    var first = Today(fixture.Read());
    // The second read of the unchanged file is served from the per-file row cache.
    var second = Today(fixture.Read());
    AssertEqual(first.TotalTokens, second.TotalTokens, "cached claude totals match a fresh scan");
    AssertClose(first.EstimatedCostUsd, second.EstimatedCostUsd, "cached claude cost matches a fresh scan");

    fixture.WriteProjectLog("project", "session.jsonl",
        AssistantLine("claude-sonnet-4-5", "msg_1", "req_1", input: 1000, cacheRead: 100, cacheCreate: 200, output: 300),
        AssistantLine("claude-sonnet-4-5", "msg_2", "req_2", input: 500, cacheRead: 0, cacheCreate: 0, output: 50));

    var third = Today(fixture.Read());
    AssertEqual(2150L, third.TotalTokens, "changed claude file is rescanned");
}

static void ClaudeUsageMapsScopedFableLimit()
{
    var usage = DeserializeClaudeUsage("""
        {
          "five_hour": { "utilization": 12.5, "resets_at": "2026-07-10T12:30:00Z" },
          "seven_day": { "utilization": 34.5, "resets_at": "2026-07-13T12:30:00Z" },
          "limits": [
            {
              "kind": "weekly_scoped",
              "percent": 56.75,
              "resets_at": "2026-07-13T12:30:00Z",
              "scope": { "model": { "display_name": "Fable" } }
            }
          ]
        }
        """);

    var snapshot = ClaudeUsageReader.MapUsage(usage, planLabel: "max");

    Assert(snapshot.Tertiary is not null, "scoped Fable limit should be present");
    AssertEqual("Fable 5 limit", snapshot.Tertiary!.Title, "Fable limit title");
    AssertClose(56.75m, (decimal)snapshot.Tertiary.UsedPercent, "Fable utilization");
    AssertEqual(10080, snapshot.Tertiary.WindowMinutes, "Fable weekly window minutes");
    Assert(snapshot.Tertiary.ResetsAt is not null, "Fable reset should be parsed");
}

static void ClaudeUsageMapsLegacyFableLimit()
{
    var usage = DeserializeClaudeUsage("""
        {
          "five_hour": { "utilization": 12.5 },
          "seven_day": { "utilization": 34.5 },
          "seven_day_overage_included": { "utilization": 78.25 }
        }
        """);

    var snapshot = ClaudeUsageReader.MapUsage(usage, planLabel: "max");

    Assert(snapshot.Tertiary is not null, "legacy Fable limit should be present");
    AssertClose(78.25m, (decimal)snapshot.Tertiary!.UsedPercent, "legacy Fable utilization");
}

static void ClaudeUsageOmitsMissingFableLimit()
{
    var usage = DeserializeClaudeUsage("""
        {
          "five_hour": { "utilization": 12.5 },
          "seven_day": { "utilization": 34.5 }
        }
        """);

    var snapshot = ClaudeUsageReader.MapUsage(usage, planLabel: "max");

    Assert(snapshot.Tertiary is null, "Fable limit should remain optional");
}

static ClaudeUsageReader.OAuthUsageResponse DeserializeClaudeUsage(string json)
{
    return JsonSerializer.Deserialize<ClaudeUsageReader.OAuthUsageResponse>(json)
        ?? throw new InvalidOperationException("Claude usage response should deserialize");
}

static void ClaudePlanIncludesMultiplier()
{
    AssertEqual(
        "max 5x",
        ProviderPlanFormatter.ClaudePlanType("max", "default_claude_max_5x")!,
        "Claude Max 5x plan");
    AssertEqual(
        "max 20x",
        ProviderPlanFormatter.ClaudePlanType("max", "default_claude_max_20x")!,
        "Claude Max 20x plan");
    AssertEqual(
        "max",
        ProviderPlanFormatter.ClaudePlanType("max", null)!,
        "Claude plan fallback without a tier");
}

static void ProviderPlanLabelsMapCodexTiers()
{
    AssertEqual("Pro 5x", ProviderPlanFormatter.DisplayName(UsageProvider.Codex, "prolite"), "Codex ProLite plan");
    AssertEqual("Pro 20x", ProviderPlanFormatter.DisplayName(UsageProvider.Codex, "pro"), "Codex Pro plan");
    AssertEqual("Pro 40x", ProviderPlanFormatter.DisplayName(UsageProvider.Codex, "pro_40x"), "future Codex Pro multiplier");
    AssertEqual("Plus", ProviderPlanFormatter.DisplayName(UsageProvider.Codex, "plus"), "unmapped Codex plan");
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

static string CodexSessionMetaLine(string sessionId, string? forkedFromId = null)
{
    var payload = new
    {
        timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        type = "session_meta",
        payload = new
        {
            id = sessionId,
            forked_from_id = forkedFromId
        }
    };

    return JsonSerializer.Serialize(payload);
}

static string CodexInterAgentTriggerLine()
{
    var payload = new
    {
        timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        type = "inter_agent_communication_metadata",
        payload = new
        {
            trigger_turn = true
        }
    };

    return JsonSerializer.Serialize(payload);
}

static string CodexCumulativeTokenCountLine(
    long totalInput,
    long totalCached,
    long totalOutput,
    long lastInput,
    long lastCached,
    long lastOutput)
{
    var payload = new
    {
        timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new
            {
                total_token_usage = new
                {
                    input_tokens = totalInput,
                    cached_input_tokens = totalCached,
                    output_tokens = totalOutput,
                    total_tokens = totalInput + totalOutput
                },
                last_token_usage = new
                {
                    input_tokens = lastInput,
                    cached_input_tokens = lastCached,
                    output_tokens = lastOutput,
                    total_tokens = lastInput + lastOutput
                }
            },
            rate_limits = new
            {
                limit_id = "codex",
                plan_type = "plus"
            }
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
