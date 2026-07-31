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
        // Cost is printed, not just compared, so a refactor of the pricing path can be shown NOT to
        // have moved the scan's figures against a build of the previous revision.
        Console.WriteLine($"{name}: cold={coldMs}ms warm={warmMs}ms tokens={warm.Insights?.Last30DaysTokens} cost={warm.Insights?.Last30DaysEstimatedCostUsd} agree={matches}");
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
    ("Usage ledger re-merges a day idempotently", UsageLedgerRemergesDayIdempotently),
    ("Usage ledger lets a complete rescan decrease a day", UsageLedgerCompleteRescanCanDecreaseDay),
    ("Usage ledger merges a partial batch monotonically", UsageLedgerPartialBatchMergesMonotonically),
    ("Usage ledger tolerates corrupt and future shards", UsageLedgerToleratesCorruptShards),
    ("Usage ledger buckets a range by granularity", UsageLedgerBucketsRangeByGranularity),
    ("Usage ledger buckets by the query time zone", UsageLedgerBucketsByQueryTimeZone),
    ("Usage ledger splits Claude components per tier", UsageLedgerSplitsClaudeComponentsPerTier),
    ("Usage ledger prices at read time and never stores cost", UsageLedgerPricesAtReadTime),
    ("Usage ledger prices Codex fast turns at per-model priority rates", UsageLedgerPricesFastTurnsAtPriorityRates),
    ("Usage ledger reports an unpriceable model as incomplete, not free", UsageLedgerReportsUnpriceableModelAsIncomplete),
    ("Usage ledger prices built-in-only Claude models exactly like the scan", UsageLedgerPricesBuiltInOnlyClaudeModelsLikeTheScan),
    ("Usage ledger labels models exactly like the scan", UsageLedgerLabelsModelsLikeTheScan),
    ("Usage ledger bounds a pathological day", UsageLedgerBoundsPathologicalDay),
    ("Usage ledger reports coverage and total ever", UsageLedgerReportsCoverageAndTotalEver),
    ("Usage ledger tests leave the production ledger untouched", UsageLedgerTestsLeaveProductionUntouched),
    ("Usage ledger backfill imports months outside the scan window", UsageLedgerBackfillImportsOutsideScanWindow),
    ("Usage ledger backfill is re-runnable without doubling", UsageLedgerBackfillIsRerunnable),
    ("Usage ledger backfill writes nothing when cancelled", UsageLedgerBackfillWritesNothingWhenCancelled),
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

// ---- Usage ledger -----------------------------------------------------------------------------
// Every one of these runs under UsageLedgerFixture, which redirects UsageLedger's root to a temp
// directory for the duration. That is the ledger's equivalent of the readers' persistCache gate:
// a test must never be able to write the user's real history.

static UsageLedgerBatch LedgerCodexBatch(bool complete, params (DateTimeOffset At, string Model, long Input, long Cached, long Output, bool Fast)[] rows)
{
    var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
    if (!complete)
    {
        builder.MarkIncomplete();
    }

    foreach (var row in rows)
    {
        builder.CoverDay(row.At);
        builder.AddCodexRow(row.At, row.Model, row.Input, row.Cached, row.Output, row.Fast, thresholdTokens: 272_000);
    }

    return builder.Build(rows[0].At);
}

static UsageLedgerSeries LedgerQueryUtc(UsageLedgerScope scope, DateTimeOffset from, DateTimeOffset to, UsageLedgerGranularity granularity, UsageLedgerPricing? pricing = null)
{
    return UsageLedger.Query(scope, from, to, granularity, TimeZoneInfo.Utc, pricing);
}

static void UsageLedgerRemergesDayIdempotently()
{
    using var fixture = new UsageLedgerFixture();
    var at = new DateTimeOffset(2026, 5, 10, 13, 30, 0, TimeSpan.Zero);

    UsageLedgerBatch Batch() => LedgerCodexBatch(
        complete: true,
        (at, "gpt-5.6-sol", 1000, 100, 200, false),
        (at, "gpt-5.6-sol", 500, 0, 50, false));

    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, Batch()), "first merge must succeed");
    var first = LedgerQueryUtc(UsageLedgerScope.Codex, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), UsageLedgerGranularity.Day);
    AssertEqual(1750L, first.TotalTokens, "rows in the same hour and key are summed within one batch");

    for (var i = 0; i < 3; i++)
    {
        Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, Batch()), "re-merge must succeed");
    }

    var again = LedgerQueryUtc(UsageLedgerScope.Codex, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), UsageLedgerGranularity.Day);
    AssertEqual(first.TotalTokens, again.TotalTokens, "re-scanning a day must replace it, not add to it");
    AssertEqual(1500L, again.InputTokens, "input after re-merge");
    AssertEqual(100L, again.CachedInputTokens, "cached input after re-merge");
    AssertEqual(250L, again.OutputTokens, "output after re-merge");
}

static void UsageLedgerCompleteRescanCanDecreaseDay()
{
    using var fixture = new UsageLedgerFixture();
    var at = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);
    var from = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);
    var to = from.AddDays(1);

    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 9000, 0, 1000, false))), "seed merge");
    AssertEqual(10_000L, LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day).TotalTokens, "seeded total");

    // An accounting fix (the Codex overcount fix, Claude dedup dropping a fork) legitimately
    // LOWERS a day. An additive merge could never express that.
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 4000, 0, 500, false))), "corrected merge");
    AssertEqual(4500L, LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day).TotalTokens, "a complete rescan must be able to decrease a day");
}

static void UsageLedgerPartialBatchMergesMonotonically()
{
    using var fixture = new UsageLedgerFixture();
    var at = new DateTimeOffset(2026, 5, 12, 4, 0, 0, TimeSpan.Zero);
    var from = new DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.Zero);
    var to = from.AddDays(1);

    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 8000, 0, 800, false))), "seed merge");

    // A truncated scan saw less than the whole corpus, so it may only raise a key, never lower it.
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(false, (at, "gpt-5.6-sol", 3000, 0, 300, false))), "smaller partial merge");
    var afterSmaller = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day);
    AssertEqual(8800L, afterSmaller.TotalTokens, "a partial batch must not lower a key");
    Assert(afterSmaller.HasPartialDays, "a partial merge must stamp the day partial");

    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(false, (at, "gpt-5.6-sol", 12000, 0, 900, false))), "larger partial merge");
    AssertEqual(12_900L, LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day).TotalTokens, "a partial batch must raise a key");

    // A later complete batch is authoritative and clears the partial stamp.
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 5000, 0, 500, false))), "complete override");
    var final = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day);
    AssertEqual(5500L, final.TotalTokens, "a complete batch supersedes a partial day");
    Assert(!final.HasPartialDays, "a complete batch clears the partial stamp");
}

static void UsageLedgerToleratesCorruptShards()
{
    using var fixture = new UsageLedgerFixture();
    var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var to = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    foreach (var payload in new[]
    {
        "",
        "{",
        "not json at all",
        // Day 20500 is 2026-02-16. One well-formed row, then an impossible hour, an out-of-range
        // model index, and a short row — each must be dropped, not thrown on.
        """{"v":1,"a":3,"m":["gpt-5.6-sol"],"d":{"20500":{"r":[[1,0,0,0,1,2,3,4,5,6,7,8,9],[99,0,0,0,1,1,1,1,0,0,0,0,1],[0,77,0,0,1,1,1,1,0,0,0,0,1],[0,0,0,0,1]]}}}""",
        """{"v":9999,"a":3,"m":[],"d":{}}""",
        """{"v":1,"a":3,"m":null,"d":null}""",
        """{"v":1,"d":{"not-a-day":{"r":[]}}}""",
    })
    {
        fixture.WriteRawShard(UsageLedgerScope.Codex, 2026, payload);

        var series = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Month);
        Assert(series.Buckets.Count == 12, "a corrupt shard must still yield a dense, empty series");

        var coverage = UsageLedger.GetCoverage(UsageLedgerScope.Codex);
        Assert(coverage is not null, "coverage must never throw on a corrupt shard");

        // The only row in the hand-edited payload that survives sanitisation is the well-formed
        // one; every other row is structurally impossible and is dropped rather than thrown on.
        Assert(series.TotalTokens is 0 or 28, $"corrupt shard degraded to unexpected totals: {series.TotalTokens}");
    }

    // Salvage is real, not vacuous: the one well-formed row in the hand-edited payload survives
    // while its three malformed siblings are dropped.
    fixture.WriteRawShard(UsageLedgerScope.Codex, 2026, """{"v":1,"a":3,"m":["gpt-5.6-sol"],"d":{"20500":{"r":[[1,0,0,0,1,2,3,4,5,6,7,8,9],[99,0,0,0,1,1,1,1,0,0,0,0,1],[0,77,0,0,1,1,1,1,0,0,0,0,1],[0,0,0,0,1]]}}}""");
    var salvaged = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Month);
    AssertEqual(28L, salvaged.TotalTokens, "the one well-formed row in a hand-edited shard is kept");
    AssertEqual(1, UsageLedger.GetCoverage(UsageLedgerScope.Codex).RecordCount, "the malformed rows are dropped");

    // A merge over a corrupt shard rebuilds it rather than failing forever.
    var at = new DateTimeOffset(2026, 3, 3, 3, 0, 0, TimeSpan.Zero);
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 100, 0, 10, false))), "merge over a corrupt shard");
    AssertEqual(138L, LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Month).TotalTokens, "a merge rebuilds the shard while keeping the days it salvaged");
}

static void UsageLedgerBucketsRangeByGranularity()
{
    using var fixture = new UsageLedgerFixture();
    // 2026-03-02 is a Monday, so these three days land in one ISO week and one month.
    var rows = new (DateTimeOffset At, string Model, long Input, long Cached, long Output, bool Fast)[]
    {
        (new DateTimeOffset(2026, 3, 2, 1, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 100, 0, 10, false),
        (new DateTimeOffset(2026, 3, 2, 5, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 200, 0, 20, true),
        (new DateTimeOffset(2026, 3, 4, 7, 0, 0, TimeSpan.Zero), "gpt-5.6-luna", 400, 0, 40, false),
        (new DateTimeOffset(2026, 4, 6, 9, 0, 0, TimeSpan.Zero), "gpt-5.6-luna", 800, 0, 80, false),
    };
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, rows)), "seed merge");

    var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    var to = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    var days = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day);
    AssertEqual(61, days.Buckets.Count, "March plus April is 61 dense day buckets");
    AssertEqual(1650L, days.TotalTokens, "day series total");
    AssertEqual(330L, days.Buckets[1].TotalTokens, "2026-03-02 holds both of its rows");
    AssertEqual(0L, days.Buckets[2].TotalTokens, "an empty day is materialised, not skipped");

    var hours = LedgerQueryUtc(UsageLedgerScope.Codex, new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero), UsageLedgerGranularity.Hour);
    AssertEqual(24, hours.Buckets.Count, "a day at hourly resolution is 24 buckets");
    AssertEqual(110L, hours.Buckets[1].TotalTokens, "01:00 UTC bucket");
    AssertEqual(220L, hours.Buckets[5].TotalTokens, "05:00 UTC bucket");

    var weeks = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Week);
    AssertEqual(770L, weeks.Buckets.First(bucket => bucket.TotalTokens > 0).TotalTokens, "the ISO week starting Mon 2026-03-02 holds all three March rows");

    var months = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Month);
    AssertEqual(2, months.Buckets.Count, "two month buckets");
    AssertEqual(770L, months.Buckets[0].TotalTokens, "March");
    AssertEqual(880L, months.Buckets[1].TotalTokens, "April");

    var all = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.All);
    AssertEqual(1, all.Buckets.Count, "All collapses the range into one bucket");
    AssertEqual(1650L, all.Buckets[0].TotalTokens, "All bucket total");

    // The per-model breakdown is the type the graphs window already binds, and the fast suffix is
    // the reader's own labelling convention.
    var march = months.Buckets[0];
    AssertEqual(3, march.Models.Count, "March breaks down into three model rows");
    Assert(march.Models.Any(model => model.Model == "gpt-5.6-sol fast" && model.TotalTokens == 220), "a fast row is labelled and split out");

    var range = months.Models;
    AssertEqual(3, range.Count, "the range breakdown collapses a model across buckets");
    AssertEqual(1320L, range.First(model => model.Model == "gpt-5.6-luna").TotalTokens, "luna across March and April");
}

static void UsageLedgerBucketsByQueryTimeZone()
{
    using var fixture = new UsageLedgerFixture();
    var at = new DateTimeOffset(2026, 5, 10, 19, 0, 0, TimeSpan.Zero);
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 1000, 0, 100, false))), "seed merge");

    // Records are UTC instants and local bucketing happens only at query time, so the SAME shard
    // has to re-bucket under a different zone with no re-import. IST's half-hour offset is the
    // motivating case: 19:00Z is 00:30 the next local day.
    var ist = TimeZoneInfo.CreateCustomTimeZone("test-ist", TimeSpan.FromMinutes(330), "test-ist", "test-ist");
    var pacific = TimeZoneInfo.CreateCustomTimeZone("test-pst", TimeSpan.FromHours(-8), "test-pst", "test-pst");

    static DateOnly BusiestDay(UsageLedgerSeries series)
        => DateOnly.FromDateTime(series.Buckets.First(bucket => bucket.TotalTokens > 0).StartLocal.DateTime);

    var utcDays = UsageLedger.Query(UsageLedgerScope.Codex, new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero), UsageLedgerGranularity.Day, TimeZoneInfo.Utc);
    AssertEqual(new DateOnly(2026, 5, 10), BusiestDay(utcDays), "UTC places 19:00Z on the 10th");

    var istDays = UsageLedger.Query(UsageLedgerScope.Codex, new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.FromMinutes(330)), new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.FromMinutes(330)), UsageLedgerGranularity.Day, ist);
    AssertEqual(new DateOnly(2026, 5, 11), BusiestDay(istDays), "+05:30 places 19:00Z on the 11th");

    var pacificDays = UsageLedger.Query(UsageLedgerScope.Codex, new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.FromHours(-8)), new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.FromHours(-8)), UsageLedgerGranularity.Day, pacific);
    AssertEqual(new DateOnly(2026, 5, 10), BusiestDay(pacificDays), "-08:00 keeps 19:00Z on the 10th");

    AssertEqual(1100L, istDays.TotalTokens, "re-bucketing must not change the totals");
    AssertEqual(1100L, pacificDays.TotalTokens, "re-bucketing must not change the totals");

    // 11:00 local under +05:30 is the half of 05:30Z..06:30Z the bucket cannot subdivide; assert
    // the documented behaviour explicitly so a future resolution change is a visible test change.
    var istHours = UsageLedger.Query(UsageLedgerScope.Codex, new DateTimeOffset(2026, 5, 11, 0, 0, 0, TimeSpan.FromMinutes(330)), new DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.FromMinutes(330)), UsageLedgerGranularity.Hour, ist);
    AssertEqual(24, istHours.Buckets.Count, "a half-hour offset still yields 24 hourly buckets");
    AssertEqual(1100L, istHours.Buckets[0].TotalTokens, "19:00Z lands in the 00:00-01:00 local bucket");
}

static void UsageLedgerSplitsClaudeComponentsPerTier()
{
    using var fixture = new UsageLedgerFixture();
    var at = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

    var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
    builder.CoverDay(at);
    // Claude splits EACH component independently at the cutoff, unlike Codex which moves the whole
    // row. rawInput is 50k over; output is under; so only rawInput has a long-context part.
    builder.AddClaudeRow(at, "claude-sonnet-4-6", rawInput: 250_000, cachedInput: 10_000, cacheCreation: 5_000, output: 8_000, thresholdTokens: 200_000);
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Claude, builder.Build(at)), "claude merge");

    var from = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
    var series = LedgerQueryUtc(
        UsageLedgerScope.Claude,
        from,
        from.AddDays(1),
        UsageLedgerGranularity.Day,
        new UsageLedgerPricing(CostUsd: record =>
        {
            // A deliberately lopsided rate: only a genuine per-component split can produce this.
            var standard = record.Standard.Input * 1m;
            var above = record.LongContext.Input * 2m;
            return (standard + above) / 1_000_000m;
        }));

    AssertEqual(263_000L, series.TotalTokens, "components round-trip through the tier split");
    AssertEqual(250_000L, series.InputTokens, "input is preserved across the split");
    AssertClose(0.30m, series.TotalEstimatedCostUsd, "200k at the base rate plus 50k at the above-threshold rate");
}

static void UsageLedgerPricesAtReadTime()
{
    using var fixture = new UsageLedgerFixture();
    var at = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
    var from = new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero);
    var to = from.AddDays(1);

    var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
    builder.CoverDay(at);
    builder.AddCodexRow(at, "gpt-5.6-sol", input: 1_000_000, cachedInput: 0, output: 0, isFast: false, thresholdTokens: 272_000);
    builder.AddCodexRow(at, "gpt-5.6-sol", input: 1_000_000, cachedInput: 0, output: 0, isFast: true, thresholdTokens: 272_000);
    // A vendor-priced row: real money the vendor supplied, which this store refuses to keep.
    builder.AddCodexRow(at, "pi-model", input: 1_000, cachedInput: 0, output: 0, isFast: false, thresholdTokens: null, vendorPriced: true);
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, builder.Build(at)), "seed merge");

    // The stored bytes must contain tokens and no money at all.
    var raw = fixture.ReadRawShard(UsageLedgerScope.Codex, 2026);
    Assert(!raw.Contains("cost", StringComparison.OrdinalIgnoreCase) && !raw.Contains("usd", StringComparison.OrdinalIgnoreCase), "the shard must not carry a cost column");

    static UsageLedgerPricing At(decimal perMillionInput) => new(CostUsd: record =>
        record.Key.Model == "pi-model"
            ? null
            : (record.Combined.Input * perMillionInput) / 1_000_000m);

    var cheap = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day, At(5m));
    AssertClose(10m, cheap.TotalEstimatedCostUsd, "two priced rows at $5/M");
    AssertClose(5m, cheap.TotalFastEstimatedCostUsd, "only the fast row counts as fast spend");
    AssertClose(5m, cheap.TotalRegularEstimatedCostUsd, "regular is the remainder");
    Assert(cheap.HasIncompleteCost, "an underivable row must surface as incomplete cost");

    // The same bytes, a corrected rate: history re-prices with no re-import. This is the property
    // the whole tokens-only design exists to protect.
    var dear = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day, At(20m));
    AssertClose(40m, dear.TotalEstimatedCostUsd, "a rate correction retroactively re-prices history");
    AssertEqual(cheap.TotalTokens, dear.TotalTokens, "re-pricing must not move a single token");

    var unpriced = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day);
    AssertEqual(0m, unpriced.TotalEstimatedCostUsd, "with no pricing supplied the ledger reports tokens only");
    AssertEqual(2_001_000L, unpriced.TotalTokens, "tokens are independent of pricing");

    // A moved cutoff is detectable rather than silently miscomputed.
    var drifted = LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day, new UsageLedgerPricing(ThresholdTokens: _ => 400_000));
    Assert(drifted.ThresholdMismatch, "a threshold change against the live table must be reported");
    Assert(!cheap.ThresholdMismatch, "no mismatch is reported when no threshold resolver is supplied");
}

// The defect this pins: LedgerPricing could not reach the priority (fast) rate columns at all, so
// every ledger-backed view priced fast turns at BASE rates and — because a non-null cost came back —
// never flagged the result incomplete. The rates are per MODEL and are not a constant multiple of
// the base column, so the assertions below check the ACTUAL published figures rather than a ratio.
static void UsageLedgerPricesFastTurnsAtPriorityRates()
{
    using var fixture = new UsageLedgerFixture();
    var day = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
    var pricing = LedgerPricing.For(UsageLedgerScope.Codex);
    var slot = 0;

    // One row per HOUR of the same day, each queried on its own, so the cases stay independent
    // without swapping the ledger root out from under the fixture mid-test.
    decimal Cost(string model, long input, long cachedInput, long output, bool isFast, int? threshold)
    {
        var at = day.AddHours(slot++);
        var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
        builder.CoverDay(at);
        builder.AddCodexRow(at, model, input, cachedInput, output, isFast, threshold);
        Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, builder.Build(at)), $"seed merge for {model}");

        var series = LedgerQueryUtc(UsageLedgerScope.Codex, at, at.AddHours(1), UsageLedgerGranularity.Hour, pricing);
        Assert(!series.HasIncompleteCost, $"{model} is a known model and must price completely");
        Assert(series.HasPriceableData, $"{model} must count as priceable");
        return series.TotalEstimatedCostUsd;
    }

    // Every priority case keeps input UNDER the 272k ceiling, because exceeding it is itself a
    // disqualifier — see the over-the-ceiling cases below.

    // gpt-5.5: base 5.00 in / 30.00 out, priority 12.50 / 75.00 — a 2.5x model.
    // regular: 0.1M*5.00 + 1M*30.00 = 30.50   priority: 0.1M*12.50 + 1M*75.00 = 76.25
    AssertClose(30.50m, Cost("gpt-5.5", 100_000, 0, 1_000_000, isFast: false, 272_000), "gpt-5.5 regular turn at base rates");
    AssertClose(76.25m, Cost("gpt-5.5", 100_000, 0, 1_000_000, isFast: true, 272_000), "gpt-5.5 fast turn at its own 2.5x priority rates");

    // gpt-5.6-sol: identical BASE rates to gpt-5.5, but priority is 10.00 / 60.00 — 2x. Pricing the
    // two the same (or gpt-5.5 at 2x) is exactly the 25% error a blanket multiplier would produce.
    AssertClose(30.50m, Cost("gpt-5.6-sol", 100_000, 0, 1_000_000, isFast: false, 272_000), "gpt-5.6-sol regular turn at base rates");
    AssertClose(61m, Cost("gpt-5.6-sol", 100_000, 0, 1_000_000, isFast: true, 272_000), "gpt-5.6-sol fast turn at its own 2x priority rates");

    // Cached input has its own priority column: gpt-5.5 cached is 0.50 base, 1.25 priority.
    AssertClose(0.125m, Cost("gpt-5.5", 100_000, 100_000, 0, isFast: true, 272_000), "cached input bills at the priority cached rate");

    // OVER THE PRIORITY CEILING (272k input): priority does not apply even though the model has it.
    // 300k input is also over gpt-5.5's long-context threshold, so the row bills at the ABOVE rates:
    // 0.3M * 10.00 = 3.00, plus 1M output * 45.00 = 45.00.
    AssertClose(48m, Cost("gpt-5.5", 300_000, 0, 1_000_000, isFast: true, 272_000), "a fast turn over the priority ceiling falls back to long-context rates");

    // gpt-5.4-mini has priority rates and NO threshold, so "over the priority limit" and "long
    // context" really are independent bits: over the ceiling it falls back to BASE, not to above.
    AssertClose(9.15m, Cost("gpt-5.4-mini", 100_000, 0, 1_000_000, isFast: true, null), "gpt-5.4-mini fast turn at priority rates without any threshold");
    AssertClose(4.725m, Cost("gpt-5.4-mini", 300_000, 0, 1_000_000, isFast: true, null), "over the ceiling a threshold-less model falls back to base rates");

    // A model with no priority column at all: the fast flag must change nothing.
    AssertClose(10.125m, Cost("gpt-5", 100_000, 0, 1_000_000, isFast: false, null), "gpt-5 regular turn");
    AssertClose(10.125m, Cost("gpt-5", 100_000, 0, 1_000_000, isFast: true, null), "a model without priority rates prices a fast turn at base rates");
}

// The defect this pins: a model the ledger cannot price contributed tokens but no cost, and the
// graphs window's token-based gate then treated the period as "the ledger has this covered" — which
// permanently suppressed the scan fallback that DOES carry correct cost. The decision recorded here
// is that an unpriceable model is worth $0.00, distorts nothing else, and is VISIBLE as incomplete.
static void UsageLedgerReportsUnpriceableModelAsIncomplete()
{
    using var fixture = new UsageLedgerFixture();
    var mixedAt = new DateTimeOffset(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    var aloneAt = mixedAt.AddHours(1);
    var freeAt = mixedAt.AddHours(2);
    var pricing = LedgerPricing.For(UsageLedgerScope.Codex);

    // Three independent hours of one day rather than three ledgers: the query is per hour, so the
    // cases cannot contaminate each other and the fixture root is never swapped mid-test.
    // "zz-not-a-real-model" matches none of the table keys and none of the substring fallbacks
    // (gpt-4.1 / o4-mini / o3), so it is genuinely unpriceable.
    var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
    builder.CoverDay(mixedAt);
    builder.AddCodexRow(mixedAt, "gpt-5.6-sol", input: 1_000_000, cachedInput: 0, output: 0, isFast: false, thresholdTokens: 272_000);
    builder.AddCodexRow(mixedAt, "zz-not-a-real-model", input: 4_000_000, cachedInput: 0, output: 2_000_000, isFast: false, thresholdTokens: null);
    builder.AddCodexRow(aloneAt, "zz-not-a-real-model", input: 1_000_000, cachedInput: 0, output: 0, isFast: false, thresholdTokens: null);
    // gpt-5.3-codex-spark publishes 0.00 across the board — its zero is an ANSWER, not a gap.
    builder.AddCodexRow(freeAt, "gpt-5.3-codex-spark", input: 1_000_000, cachedInput: 0, output: 1_000_000, isFast: false, thresholdTokens: null);
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, builder.Build(mixedAt)), "mixed seed merge");

    var mixed = LedgerQueryUtc(UsageLedgerScope.Codex, mixedAt, mixedAt.AddHours(1), UsageLedgerGranularity.Hour, pricing);

    // 1M input at gpt-5.6-sol's long-context rate (1M > the 272k threshold) = $10.00, and the
    // unknown model adds nothing to it.
    AssertClose(10m, mixed.TotalEstimatedCostUsd, "an unpriceable model contributes exactly $0.00 and distorts nothing");
    Assert(mixed.HasIncompleteCost, "an unpriceable model must be surfaced, not silently under-reported");
    Assert(mixed.HasPriceableData, "one unpriceable model must not make a period unaccountable");

    // Its TOKENS still count everywhere tokens are shown.
    AssertEqual(7_000_000L, mixed.TotalTokens, "an unpriceable model's tokens are as real as any other's");
    var unknown = mixed.Models.Single(model => model.Model == "zz-not-a-real-model");
    AssertEqual(6_000_000L, unknown.TotalTokens, "the unpriceable row keeps its own tokens");
    AssertEqual(0m, unknown.EstimatedCostUsd, "the unpriceable row is worth zero dollars");
    Assert(unknown.HasIncompleteCost, "the unpriceable row is individually marked");
    Assert(!mixed.Models.Single(model => model.Model == "gpt-5.6-sol").HasIncompleteCost, "a priceable neighbour must stay clean");

    // An hour holding NOTHING but the unpriceable model: tokens, no money, and no claim to have
    // accounted for the period — which is what lets the graphs window fall back to the scan.
    var onlyUnknown = LedgerQueryUtc(UsageLedgerScope.Codex, aloneAt, aloneAt.AddHours(1), UsageLedgerGranularity.Hour, pricing);
    AssertEqual(1_000_000L, onlyUnknown.TotalTokens, "the view is not blanked: the tokens are still there");
    AssertEqual(0m, onlyUnknown.TotalEstimatedCostUsd, "no money can be derived");
    Assert(onlyUnknown.HasIncompleteCost, "and it says so");
    Assert(!onlyUnknown.HasPriceableData, "nothing here was priceable, so the fallback must not be suppressed");

    // GENUINELY FREE is a different thing and must not be conflated with it.
    var freeSeries = LedgerQueryUtc(UsageLedgerScope.Codex, freeAt, freeAt.AddHours(1), UsageLedgerGranularity.Hour, pricing);
    AssertEqual(0m, freeSeries.TotalEstimatedCostUsd, "a free model costs nothing");
    Assert(!freeSeries.HasIncompleteCost, "a free model is priced, not unpriced");
    Assert(freeSeries.HasPriceableData, "free usage is an accounting of the period, so the ledger keeps it");
}

// The defect this pins: the Claude ledger cost path reached ONLY the network-fetched models.dev
// catalog, never ClaudeUsageInsightsReader's built-in table. Every Claude model the catalog does not
// carry therefore priced at exactly $0.00 on the ledger — and because zero is a NUMBER, not a null,
// nothing was ever marked incomplete — while the scan path priced the same rows correctly.
// claude-sonnet-4-20250514 is the case that proves it: built-in only, AND the only shape with a
// long-context tier, so the per-component split is exercised at the same time.
static void UsageLedgerPricesBuiltInOnlyClaudeModelsLikeTheScan()
{
    const string model = "claude-sonnet-4-20250514";

    // The precondition, asserted rather than assumed: if models.dev ever starts carrying this id the
    // literals below stop describing the built-in table, and this message says so out loud instead
    // of the test failing for an opaque reason. Catalog-FIRST precedence is deliberate — see
    // ClaudeModelPricing.For — so a catalog entry would legitimately win.
    Assert(ModelsDevPricing.Lookup("anthropic", model) is null,
        $"{model} must be absent from the models.dev catalog for this test to exercise the BUILT-IN table");
    Assert(ClaudeModelPricing.For(model) is not null, "the built-in table must still price it");
    AssertEqual(200_000, ClaudeModelPricing.ThresholdTokensFor(model) ?? 0, "and must carry the 200k long-context cutoff");

    using var fixture = new UsageLedgerFixture();
    var day = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
    var slot = 0;

    // One row per hour of the same day, each queried alone, so the cases stay independent without
    // swapping the ledger root out from under the fixture mid-test.
    decimal LedgerCost(long rawInput, long cachedInput, long cacheCreation, long output)
    {
        var at = day.AddHours(slot++);
        var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
        builder.CoverDay(at);
        builder.AddClaudeRow(at, model, rawInput, cachedInput, cacheCreation, output, ClaudeModelPricing.ThresholdTokensFor(model));
        Assert(UsageLedger.TryMerge(UsageLedgerScope.Claude, builder.Build(at)), "seed merge");

        var series = LedgerQueryUtc(UsageLedgerScope.Claude, at, at.AddHours(1), UsageLedgerGranularity.Hour, LedgerPricing.For(UsageLedgerScope.Claude));
        Assert(!series.HasIncompleteCost, "a built-in-priced model must price COMPLETELY on the ledger path");
        Assert(!series.ThresholdMismatch, "the query's threshold resolver must agree with the one the batch recorded");
        return series.TotalEstimatedCostUsd;
    }

    // The scan path's own arithmetic, reached through the reader's private entry point so this is
    // the number the 30-day history actually shows — not a re-implementation of it.
    var scanEstimate = typeof(ClaudeUsageInsightsReader).GetMethod("EstimateCost", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    Assert(scanEstimate is not null, "ClaudeUsageInsightsReader.EstimateCost should exist");
    decimal ScanCost(long rawInput, long cachedInput, long cacheCreation, long output)
        => (decimal)(scanEstimate!.Invoke(null, [model, rawInput, cachedInput, cacheCreation, output]) as decimal? ?? -1m);

    void Both(decimal expected, long rawInput, long cachedInput, long cacheCreation, long output, string what)
    {
        AssertClose(expected, ScanCost(rawInput, cachedInput, cacheCreation, output), $"scan path: {what}");
        AssertClose(expected, LedgerCost(rawInput, cachedInput, cacheCreation, output), $"ledger path: {what}");
    }

    // Rates: 3.00 input / 15.00 output / 0.30 cache read / 3.75 cache write, and above 200k
    // 6.00 / 22.50 / 0.60 / 7.50.

    // Entirely BELOW the cutoff: 0.1M*3 + 0.05M*0.30 + 0.02M*3.75 + 0.01M*15
    //                          = 0.30 + 0.015 + 0.075 + 0.15
    Both(0.54m, 100_000, 50_000, 20_000, 10_000, "a wholly sub-threshold row");

    // INPUT over the cutoff, everything else under. Only the input splits — this is the rule that
    // separates Claude from Codex, which would have repriced the WHOLE row.
    // input 0.2M*3 + 0.05M*6 = 0.90; cache read 0.01M*0.30 = 0.003;
    // cache write 0.005M*3.75 = 0.01875; output 0.008M*15 = 0.12
    Both(1.04175m, 250_000, 10_000, 5_000, 8_000, "input past the cutoff splits and nothing else does");

    // OUTPUT over the cutoff with input far under it: if the split were per ROW rather than per
    // COMPONENT, the 10k of input would bill at the above-threshold rate too and this would be 0.06
    // higher. output 0.2M*15 + 0.05M*22.50 = 4.125; input 0.01M*3 = 0.03
    Both(4.155m, 10_000, 0, 0, 250_000, "output past the cutoff splits independently of input");

    // Every component past the cutoff at once, so no component can be quietly borrowing another's
    // tier. input 0.2*3+0.1*6=1.20; cache read 0.2*0.30+0.1*0.60=0.12;
    // cache write 0.2*3.75+0.1*7.50=1.50; output 0.2*15+0.1*22.50=5.25
    Both(8.07m, 300_000, 300_000, 300_000, 300_000, "all four components split at their own cutoff");
}

// The defect this pins: LedgerPricing supplied no ModelLabel, so the ledger grouped rows under
// UsageLedger.DefaultModelLabel — the RAW logged model id — while the scan grouped under its
// normalised one. The same model then appeared under two labels depending on which source answered
// the query, which also splits its per-model colour override and its row in the breakdown.
static void UsageLedgerLabelsModelsLikeTheScan()
{
    using var fixture = new UsageLedgerFixture();
    var at = new DateTimeOffset(2026, 6, 25, 9, 0, 0, TimeSpan.Zero);

    var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
    builder.CoverDay(at);
    // Raw ids exactly as the logs spell them: the vendor prefix, the alias OpenAI routes to Sol, and
    // a fast row whose suffix the scan appends.
    builder.AddCodexRow(at, "openai/gpt-5.6", input: 1_000, cachedInput: 0, output: 100, isFast: false, thresholdTokens: 272_000);
    builder.AddCodexRow(at, "gpt-5.6-sol", input: 2_000, cachedInput: 0, output: 200, isFast: false, thresholdTokens: 272_000);
    builder.AddCodexRow(at, "gpt-5.4-mini", input: 3_000, cachedInput: 0, output: 300, isFast: true, thresholdTokens: null);
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, builder.Build(at)), "codex label seed");

    var codex = LedgerQueryUtc(UsageLedgerScope.Codex, at, at.AddHours(1), UsageLedgerGranularity.Hour, LedgerPricing.For(UsageLedgerScope.Codex));

    // The alias and the canonical id are ONE model in the breakdown, exactly as the scan reports it.
    AssertEqual(2, codex.Models.Count, "openai/gpt-5.6 and gpt-5.6-sol are the same model, plus one fast row");
    var sol = codex.Models.Single(row => row.Model == "gpt-5.6-sol");
    AssertEqual(3_300L, sol.TotalTokens, "both spellings land in the same row");
    Assert(codex.Models.Any(row => row.Model == "gpt-5.4-mini fast"), "the scan's fast suffix survives the ledger path");

    // And the labels are literally the scan's, not a lookalike.
    var scanLabel = typeof(CodexUsageInsightsReader).GetMethod("ModelBreakdownLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    Assert(scanLabel is not null, "CodexUsageInsightsReader.ModelBreakdownLabel should exist");
    AssertEqual("gpt-5.6-sol", (string)scanLabel!.Invoke(null, ["openai/gpt-5.6", false])!, "scan normalises the alias too");
    AssertEqual("gpt-5.4-mini fast", (string)scanLabel.Invoke(null, ["gpt-5.4-mini", true])!, "scan appends the same suffix");

    // Claude groups on its own normalisation: the Bedrock prefix and version suffix are one model.
    var claudeAt = at.AddHours(1);
    var claudeBuilder = new UsageLedgerBatchBuilder(accountingVersion: 3);
    claudeBuilder.CoverDay(claudeAt);
    claudeBuilder.AddClaudeRow(claudeAt, "anthropic.claude-opus-4-5-v1:0", rawInput: 1_000, cachedInput: 0, cacheCreation: 0, output: 100, thresholdTokens: null);
    claudeBuilder.AddClaudeRow(claudeAt, "claude-opus-4-5", rawInput: 2_000, cachedInput: 0, cacheCreation: 0, output: 200, thresholdTokens: null);
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Claude, claudeBuilder.Build(claudeAt)), "claude label seed");

    var claude = LedgerQueryUtc(UsageLedgerScope.Claude, claudeAt, claudeAt.AddHours(1), UsageLedgerGranularity.Hour, LedgerPricing.For(UsageLedgerScope.Claude));
    AssertEqual(1, claude.Models.Count, "the Bedrock spelling collapses onto the plain id");
    AssertEqual("claude-opus-4-5", claude.Models[0].Model, "and under the scan's label");
    AssertEqual(3_300L, claude.Models[0].TotalTokens, "with both rows' tokens");
}

static void UsageLedgerBoundsPathologicalDay()
{
    using var fixture = new UsageLedgerFixture();
    var day = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
    builder.CoverDay(day);
    var ordinal = 0;
    for (var hour = 0; hour < 24; hour++)
    {
        for (var model = 0; model < 30; model++)
        {
            ordinal++;
            builder.AddCodexRow(day.AddHours(hour), $"model-{model}", input: 0, cachedInput: 0, output: ordinal, isFast: false, thresholdTokens: 0);
        }
    }

    AssertEqual(720, builder.RecordCount, "the pathological batch really does carry 720 distinct keys");
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, builder.Build(day)), "pathological merge");

    var series = LedgerQueryUtc(UsageLedgerScope.Codex, day, day.AddDays(1), UsageLedgerGranularity.Day);
    var recorded = UsageLedger.GetCoverage(UsageLedgerScope.Codex);
    AssertEqual(UsageLedger.MaxRecordsPerDay, recorded.RecordCount, "a day is capped at the per-day record ceiling");
    Assert(series.HasTruncatedDays, "a truncated day must say so rather than quietly under-report");

    // The cap keeps the LARGEST keys, so a runaway loses noise and not its headline: ordinals
    // 209..720 survive.
    AssertEqual(237_824L, series.TotalTokens, "the surviving keys are the largest ones");

    var bytes = fixture.ShardBytes(UsageLedgerScope.Codex, 2026);
    Assert(bytes < UsageLedger.MaxShardBytes, $"a shard must stay under the {UsageLedger.MaxShardBytes} byte ceiling, was {bytes}");
    // 512 records/day x 366 days is the structural worst case for one shard; assert the per-record
    // encoding stays small enough for that product to fit under the byte ceiling.
    Assert(bytes / UsageLedger.MaxRecordsPerDay < 90, $"per-record encoding must stay compact, was {bytes / UsageLedger.MaxRecordsPerDay} B");
}

static void UsageLedgerReportsCoverageAndTotalEver()
{
    using var fixture = new UsageLedgerFixture();
    AssertEqual(0, UsageLedger.GetCoverage(UsageLedgerScope.Codex).RecordCount, "a cold ledger has no history");
    AssertEqual(0L, UsageLedger.QueryTotal(UsageLedgerScope.Codex, TimeZoneInfo.Utc).TotalTokens, "a cold total-ever is zero, not an exception");

    var early = new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero);
    var late = new DateTimeOffset(2026, 1, 2, 5, 0, 0, TimeSpan.Zero);
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (early, "gpt-5.6-sol", 100, 0, 10, false), (late, "gpt-5.6-sol", 200, 0, 20, false))), "cross-year merge");

    var coverage = UsageLedger.GetCoverage(UsageLedgerScope.Codex);
    AssertEqual(new DateOnly(2025, 12, 31), coverage.FirstRecordedDay!.Value, "earliest recorded day drives the back arrow");
    AssertEqual(new DateOnly(2026, 1, 2), coverage.LastRecordedDay!.Value, "latest recorded day");
    AssertEqual(early, coverage.FirstUsageUtc!.Value, "earliest instant with real tokens");
    AssertEqual(2, coverage.RecordedDayCount, "two days recorded");
    Assert(coverage.HasHistory, "coverage reports history");

    // A year shard is a real boundary: the two rows live in different files and must still sum.
    AssertEqual(330L, UsageLedger.QueryTotal(UsageLedgerScope.Codex, TimeZoneInfo.Utc).TotalTokens, "total ever spans year shards");

    // Scopes are isolated: Claude must not see Codex's rows.
    AssertEqual(0, UsageLedger.GetCoverage(UsageLedgerScope.Claude).RecordCount, "scopes are separate shards");
}

static void UsageLedgerTestsLeaveProductionUntouched()
{
    var production = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexBarWindows",
        "usage-ledger");

    static string Snapshot(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return "<absent>";
        }

        return string.Join(
            "|",
            Directory.GetFiles(directory)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => $"{Path.GetFileName(path)}:{new FileInfo(path).Length}:{new FileInfo(path).LastWriteTimeUtc.Ticks}"));
    }

    var before = Snapshot(production);

    using (var fixture = new UsageLedgerFixture())
    {
        Assert(UsageLedger.RootDirectory == fixture.Root, "the fixture must redirect the ledger root away from production");
        var at = new DateTimeOffset(2026, 5, 10, 13, 0, 0, TimeSpan.Zero);
        Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 1000, 0, 100, false))), "redirected merge");
        Assert(UsageLedger.TryMerge(UsageLedgerScope.Claude, LedgerCodexBatch(true, (at, "claude-opus-5", 1000, 0, 100, false))), "redirected merge");
        Assert(Directory.GetFiles(fixture.Root).Length == 2, "both scopes wrote into the temp root");
    }

    Assert(UsageLedger.RootDirectory == production, "disposing the fixture must restore the production root");
    AssertEqual(before, Snapshot(production), "a test run must not create or modify anything in the real ledger");
}

// ---- Usage ledger backfill --------------------------------------------------------------------
// Driven through a stub corpus rather than the real readers on purpose: the production sources
// resolve ~/.codex and ~/.claude, so a test that used them would scan the developer's own gigabytes
// and take minutes. What is under test here is the JOB - coverage, idempotency, and the promise
// that a cancelled run writes nothing - not the parsers, which their own tests already cover.

static void UsageLedgerBackfillImportsOutsideScanWindow()
{
    using var fixture = new UsageLedgerFixture();
    var march = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);
    var july = new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero);
    var progress = new CollectingProgress();

    var result = UsageLedgerBackfill.Run(progress, CancellationToken.None, [new StubBackfillSource(UsageLedgerScope.Codex, march, july)]);

    Assert(result.Outcome == UsageLedgerBackfillOutcome.Imported, $"backfill outcome: {result.Outcome} - {result.Message}");
    AssertEqual(2, result.FilesScanned, "every file is scanned");
    AssertEqual(2, result.DaysImported, "one day per row");
    AssertEqual(new DateOnly(2026, 3, 4), result.FirstDay!.Value, "first imported day");
    AssertEqual(new DateOnly(2026, 7, 20), result.LastDay!.Value, "last imported day");

    var series = LedgerQueryUtc(
        UsageLedgerScope.Codex,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        UsageLedgerGranularity.Month);
    AssertEqual(2400L, series.TotalTokens, "both rows reach the ledger");

    var coverage = UsageLedger.GetCoverage(UsageLedgerScope.Codex);
    AssertEqual(new DateOnly(2026, 3, 4), coverage.FirstRecordedDay!.Value, "coverage starts at the earliest row on disk");
    Assert(!coverage.HasPartialDays, "a complete corpus walk is not a partial batch");
    Assert(progress.Last is { } last && last.FilesDone == last.FileCount, "progress ends at 100%");
}

static void UsageLedgerBackfillIsRerunnable()
{
    using var fixture = new UsageLedgerFixture();
    var at = new DateTimeOffset(2026, 4, 9, 8, 0, 0, TimeSpan.Zero);

    UsageLedgerSeries RunOnce()
    {
        UsageLedgerBackfill.Run(null, CancellationToken.None, [new StubBackfillSource(UsageLedgerScope.Codex, at, at)]);
        return LedgerQueryUtc(
            UsageLedgerScope.Codex,
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            UsageLedgerGranularity.Day);
    }

    var first = RunOnce();
    AssertEqual(2400L, first.TotalTokens, "two files in the same hour are summed once");
    AssertEqual(first.TotalTokens, RunOnce().TotalTokens, "re-importing must replace the day, not add to it");
    AssertEqual(first.TotalTokens, RunOnce().TotalTokens, "and stay converged");
}

static void UsageLedgerBackfillWritesNothingWhenCancelled()
{
    using var fixture = new UsageLedgerFixture();
    using var cancellation = new CancellationTokenSource();
    var at = new DateTimeOffset(2026, 6, 2, 11, 0, 0, TimeSpan.Zero);
    var source = new StubBackfillSource(UsageLedgerScope.Codex, at, at) { CancelOnScan = cancellation };

    var result = UsageLedgerBackfill.Run(null, cancellation.Token, [source]);

    Assert(result.Outcome == UsageLedgerBackfillOutcome.Cancelled, $"cancelled outcome: {result.Outcome} - {result.Message}");
    Assert(!UsageLedger.GetCoverage(UsageLedgerScope.Codex).HasHistory, "a cancelled import must leave the ledger untouched");
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

/// <summary>
/// A corpus of one row per "file", so the backfill job can be exercised without touching the real
/// session logs (see the note above the backfill tests).
/// </summary>
sealed class StubBackfillSource : IUsageLedgerBackfillSource
{
    private readonly Dictionary<string, DateTimeOffset> rows = new(StringComparer.Ordinal);

    public StubBackfillSource(UsageLedgerScope scope, params DateTimeOffset[] timestamps)
    {
        Scope = scope;
        for (var i = 0; i < timestamps.Length; i++)
        {
            rows[$"stub-{i}.jsonl"] = timestamps[i];
        }
    }

    /// <summary>Cancels as the first file is opened, which is the only interesting moment.</summary>
    public CancellationTokenSource? CancelOnScan { get; init; }

    public UsageLedgerScope Scope { get; }

    public string DisplayName => "Stub";

    public int AccountingVersion => 3;

    public IReadOnlyList<UsageLedgerBackfillFile> EnumerateFiles() => rows
        .Select(pair => new UsageLedgerBackfillFile(pair.Key, DateOnly.FromDateTime(pair.Value.UtcDateTime), false))
        .ToArray();

    public void Scan(UsageLedgerBackfillFile file, UsageLedgerBatchBuilder builder)
    {
        CancelOnScan?.Cancel();
        builder.AddCodexRow(rows[file.Path], "gpt-5.6-sol", 1000, 100, 200, isFast: false, thresholdTokens: 272_000);
    }
}

sealed class CollectingProgress : IProgress<UsageLedgerBackfillProgress>
{
    private readonly object gate = new();

    public UsageLedgerBackfillProgress? Last { get; private set; }

    public void Report(UsageLedgerBackfillProgress value)
    {
        lock (gate)
        {
            Last = value;
        }
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

/// <summary>
/// Redirects the ledger root at the same seam the readers use for persistCache: the production
/// %LOCALAPPDATA% path is durable USER DATA, and a test that could write it would be able to
/// silently corrupt months of imported history.
/// </summary>
sealed class UsageLedgerFixture : IDisposable
{
    public UsageLedgerFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "codexbar-ledger-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        UsageLedger.OverrideRootForTests(Root);
    }

    public string Root { get; }

    private string ShardPath(UsageLedgerScope scope, int year)
        => Path.Combine(Root, $"{(scope == UsageLedgerScope.Codex ? "codex" : "claude")}-{year}-v{UsageLedger.SchemaVersion}.json");

    public void WriteRawShard(UsageLedgerScope scope, int year, string payload)
        => File.WriteAllText(ShardPath(scope, year), payload);

    public string ReadRawShard(UsageLedgerScope scope, int year) => File.ReadAllText(ShardPath(scope, year));

    public long ShardBytes(UsageLedgerScope scope, int year) => new FileInfo(ShardPath(scope, year)).Length;

    public void Dispose()
    {
        UsageLedger.OverrideRootForTests(null);
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
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
