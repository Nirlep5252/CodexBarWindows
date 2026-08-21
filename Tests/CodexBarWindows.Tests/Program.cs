using System.Globalization;
using System.Text.Json;
using CodexBar.WinUI;
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
    ("OpenCode Go cookie header normalization trims prefix", OpenCodeGoCookieHeaderNormalizationTrimsPrefix),
    ("OpenCode Go normalizes workspace URLs", OpenCodeGoNormalizesWorkspaceUrls),
    ("OpenCode Go parses serialized usage windows", OpenCodeGoParsesSerializedUsageWindows),
    ("OpenCode Go keeps serialized one percent", OpenCodeGoKeepsSerializedOnePercent),
    ("OpenCode Go parses nested JSON usage", OpenCodeGoParsesNestedJsonUsage),
    ("Grok billing maps weekly credit percent", GrokBillingMapsWeeklyCreditPercent),
    ("Grok billing maps on-demand when cap is set", GrokBillingMapsOnDemandWhenCapIsSet),
    ("Grok billing accepts wrapped config payloads", GrokBillingAcceptsWrappedConfigPayloads),
    ("Grok billing maps monthly used/limit percent", GrokBillingMapsMonthlyUsedLimitPercent),
    ("Grok credits view without usage is unmappable", GrokBillingCreditsViewWithoutUsageThrows),
    ("Grok reads the plan from the token tier claim", GrokReadsPlanFromTokenTierClaim),
    ("Grok plan slugs render as brand names", GrokPlanSlugsRenderAsBrandNames),
    ("Grok accounts key and resolve per home folder", GrokAccountsKeyAndResolvePerHome),
    ("Tooltip names every configured Grok account", TooltipNamesEveryGrokAccount),
    ("Grok history aggregates turn_completed usage", GrokHistoryAggregatesTurnCompletedUsage),
    ("Grok history excludes rows outside the 30-day report", GrokHistoryExcludesRowsOutsideReport),
    ("Grok history prefers stamped cost ticks", GrokHistoryPrefersStampedCostTicks),
    ("Grok history does not double count reasoning tokens", GrokHistoryDoesNotDoubleCountReasoningTokens),
    ("Usage ledger re-merges a day idempotently", UsageLedgerRemergesDayIdempotently),
    ("Usage ledger lets a complete rescan decrease a day", UsageLedgerCompleteRescanCanDecreaseDay),
    ("Usage ledger merges a partial batch monotonically", UsageLedgerPartialBatchMergesMonotonically),
    ("Usage ledger tolerates corrupt and future shards", UsageLedgerToleratesCorruptShards),
    ("Usage ledger buckets a range by granularity", UsageLedgerBucketsRangeByGranularity),
    ("Usage ledger buckets by the query time zone", UsageLedgerBucketsByQueryTimeZone),
    ("Usage ledger builds a day's hours on the local timeline across DST", UsageLedgerBuildsDayHoursOnTheLocalTimeline),
    ("Usage ledger names hour columns for the time they cover", UsageLedgerNamesHourColumnsForTheTimeTheyCover),
    ("Usage ledger splits Claude components per tier", UsageLedgerSplitsClaudeComponentsPerTier),
    ("Usage ledger prices at read time and never stores cost", UsageLedgerPricesAtReadTime),
    ("Usage ledger prices Codex fast turns at per-model priority rates", UsageLedgerPricesFastTurnsAtPriorityRates),
    ("Usage ledger reports an unpriceable model as incomplete, not free", UsageLedgerReportsUnpriceableModelAsIncomplete),
    ("Usage ledger prices built-in-only Claude models exactly like the scan", UsageLedgerPricesBuiltInOnlyClaudeModelsLikeTheScan),
    ("Claude pricing keeps the built-in long-context tier where models.dev is silent", ClaudePricingKeepsBuiltInTierWhereCatalogIsSilent),
    ("Usage ledger labels models exactly like the scan", UsageLedgerLabelsModelsLikeTheScan),
    ("Usage ledger bounds a pathological day", UsageLedgerBoundsPathologicalDay),
    ("Usage ledger reports coverage and total ever", UsageLedgerReportsCoverageAndTotalEver),
    ("Usage ledger tests leave the production ledger untouched", UsageLedgerTestsLeaveProductionUntouched),
    ("Usage ledger backfill imports months outside the scan window", UsageLedgerBackfillImportsOutsideScanWindow),
    ("Usage ledger backfill is re-runnable without doubling", UsageLedgerBackfillIsRerunnable),
    ("Usage ledger backfill writes nothing when cancelled", UsageLedgerBackfillWritesNothingWhenCancelled),
    ("Usage ledger backfill reports the corpus it committed before a cancel", UsageLedgerBackfillReportsWhatItCommittedWhenCancelled),
    ("Usage ledger drops implausible timestamps instead of fanning out shards", UsageLedgerDropsImplausibleTimestamps),
    ("Usage ledger caches parsed shards and invalidates on merge", UsageLedgerCachesParsedShardsAndInvalidatesOnMerge),
    ("Codex scan keeps history imported from an old-named session", CodexScanKeepsHistoryImportedFromOldNamedSession),
    ("Usage ledger keeps imported history on days a scan only clipped", UsageLedgerKeepsImportedHistoryOnClippedDays),
    ("Usage ledger claims only the UTC days a report window fully covers", UsageLedgerClaimsOnlyFullyCoveredUtcDays),
    ("A stale vibes flag is inert for a shell without vibes", StaleVibesFlagIsInertWithoutVibes),
    ("Chart palette colours stay perceptually apart in both themes", ChartPaletteTests.ColorsStayApart),
    ("Chart palette colours stay apart for dichromatic vision", ChartPaletteTests.ColorsStayApartForDichromats),
    ("Chart palette clears its contrast floors on both cards", ChartPaletteTests.ClearsContrastFloors),
    ("Chart palette fast tiers stay related but separable", ChartPaletteTests.FastTiersStayRelatedButSeparable),
    ("Chart palette brands a model by its provider", ChartPaletteTests.BrandsAModelByItsProvider),
    ("Chart palette keeps a build variant off its base model", ChartPaletteTests.KeepsVariantsOffTheirBaseModel),
    ("Chart palette assigns a colour independently of the other models", ChartPaletteTests.AssignsColorsIndependently),
    ("Chart palette returns a user override exactly as picked", ChartPaletteTests.ReturnsOverridesExactly),
    ("Chart palette gives every live model its own colour", ChartPaletteTests.GivesEveryLiveModelItsOwnColor),
    ("Graphs period bounds every granularity including Year", GraphsPeriodBoundsEveryGranularity),
    ("Graphs period counts the current bucket fractionally", GraphsPeriodCountsCurrentBucketFractionally),
    ("Graphs period clamps elapsed buckets to the coverage floor", GraphsPeriodClampsElapsedToCoverageFloor),
    ("Graphs period arrows follow the granularity's own bound", GraphsPeriodArrowsFollowGranularity),
    ("Graphs period drills one level finer per double-click", GraphsPeriodDrillsOneLevelFiner),
    ("Graphs period names a whole-day column without an hour", GraphsPeriodNamesWholeDayColumnWithoutHour),
    ("Graphs period puts the day axis labels on the columns", GraphsPeriodPutsDayAxisLabelsOnColumns),
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

static void OpenCodeGoCookieHeaderNormalizationTrimsPrefix()
{
    var normalized = OpenCodeGoUsageReader.NormalizeCookieHeader("  Cookie: theme=dark; auth=abc123  ");
    AssertEqual("auth=abc123", normalized, "full Cookie headers retain only the OpenCode auth cookie");

    AssertEqual("auth=bare-token", OpenCodeGoUsageReader.NormalizeCookieHeader("bare-token"), "bare values become auth cookies");
    AssertEqual("auth=abc==", OpenCodeGoUsageReader.NormalizeCookieHeader("abc=="), "padding in bare values is preserved");
    AssertEqual("__Host-auth=host-token", OpenCodeGoUsageReader.NormalizeCookieHeader("__Host-auth=host-token"), "host auth cookies remain compatible");
    AssertEqual("host-token", OpenCodeGoUsageReader.SessionValue("__Host-auth=host-token"), "settings display only the value");
    AssertEqual(string.Empty, OpenCodeGoUsageReader.NormalizeCookieHeader("session=abc\r\nInjected: yes"), "header injection is rejected");
}

static void OpenCodeGoNormalizesWorkspaceUrls()
{
    AssertEqual(
        "wrk_01HABC123",
        OpenCodeGoUsageReader.NormalizeWorkspaceId("https://opencode.ai/workspace/wrk_01HABC123/go")!,
        "workspace id is extracted from the dashboard URL");
    Assert(OpenCodeGoUsageReader.NormalizeWorkspaceId("workspace-123") is null, "invalid workspace ids are rejected");
    Assert(OpenCodeGoUsageReader.NormalizeWorkspaceId("  ") is null, "blank workspace uses automatic discovery");
}

static void OpenCodeGoParsesSerializedUsageWindows()
{
    var observedAt = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
    const string response =
        "_$HY.r[\"lite.subscription.get[\\\"wrk_LIVE123\\\"]\"]=$R[17];" +
        "$R[24]($R[18],$R[27]={" +
        "rollingUsage:$R[28]={status:\"ok\",resetInSec:3600,usagePercent:42.5}," +
        "weeklyUsage:$R[29]={status:\"ok\",resetInSec:7200,usagePercent:7}," +
        "monthlyUsage:$R[30]={status:\"ok\",resetInSec:10800,usagePercent:90}});";

    var snapshot = OpenCodeGoUsageReader.ParseUsage(response, observedAt);

    Assert(snapshot.Provider == UsageProvider.OpenCodeGo, "snapshot is attributed to OpenCode Go");
    Assert(snapshot.PlanType is null, "OpenCode Go does not repeat its name as a plan label");
    AssertEqual(3, snapshot.Windows.Count, "all available dashboard windows are retained");
    AssertClose(42.5m, (decimal)snapshot.Primary.UsedPercent, "rolling usage");
    AssertClose(7m, (decimal)snapshot.Secondary!.UsedPercent, "weekly usage");
    AssertClose(90m, (decimal)snapshot.AdditionalWindows![0].UsedPercent, "monthly usage");
    AssertEqual(observedAt.AddHours(1), snapshot.Primary.ResetsAt!.Value, "rolling reset time");
}

static void OpenCodeGoKeepsSerializedOnePercent()
{
    var observedAt = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
    const string response =
        "rollingUsage:{resetInSec:3600,usagePercent:3}," +
        "weeklyUsage:{resetInSec:7200,usagePercent:1}";

    var snapshot = OpenCodeGoUsageReader.ParseUsage(response, observedAt);

    AssertClose(3m, (decimal)snapshot.Primary.UsedPercent, "serialized rolling usage is already a percentage");
    AssertClose(1m, (decimal)snapshot.Secondary!.UsedPercent, "serialized weekly usage is not rescaled to 100");
}

static void OpenCodeGoParsesNestedJsonUsage()
{
    var observedAt = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
    const string response = """
        {
          "data": {
            "usage": {
              "rollingUsage": { "usagePercent": 0.25, "resetAt": "2026-08-01T12:00:00Z" },
              "monthlyUsage": { "used": 30, "limit": 60, "resetInSec": 86400 }
            }
          }
        }
        """;

    var snapshot = OpenCodeGoUsageReader.ParseUsage(response, observedAt);

    AssertEqual(2, snapshot.Windows.Count, "an omitted weekly window remains optional");
    AssertClose(25m, (decimal)snapshot.Primary.UsedPercent, "fractional rolling utilization is normalized");
    AssertClose(50m, (decimal)snapshot.AdditionalWindows![0].UsedPercent, "used and limit become a percent");
    AssertEqual(observedAt.AddDays(1), snapshot.AdditionalWindows[0].ResetsAt!.Value, "monthly reset time");
}

static void GrokBillingMapsWeeklyCreditPercent()
{
    var billing = GrokUsageReader.ParseBillingResponse("""
        {
          "creditUsagePercent": 12.5,
          "currentPeriod": {
            "type": "USAGE_PERIOD_TYPE_WEEKLY",
            "start": "2026-07-26T04:07:16.204303+00:00",
            "end": "2026-08-02T04:07:16.204303+00:00"
          },
          "onDemandCap": { "val": 0 },
          "onDemandUsed": { "val": 0 },
          "subscriptionTier": "SuperGrok"
        }
        """);

    var snapshot = GrokUsageReader.MapUsage(
        billing,
        new GrokUsageReader.GrokSessionCredentials(
            "token",
            true,
            null,
            DateTimeOffset.UtcNow.AddHours(1),
            "user@example.com",
            "user-1",
            "https://auth.x.ai",
            "client",
            null));

    Assert(snapshot.Provider == UsageProvider.Grok, "provider should be Grok");
    AssertEqual("Weekly limit", snapshot.Primary.Title, "primary title");
    AssertClose(12.5m, (decimal)snapshot.Primary.UsedPercent, "credit percent");
    AssertEqual(10080, snapshot.Primary.WindowMinutes, "weekly window minutes");
    Assert(snapshot.Secondary is null, "on-demand should be omitted when cap is zero");
    AssertEqual("SuperGrok", snapshot.PlanType!, "plan");
    Assert(snapshot.AccountEmail is null, "Grok must not surface account email in the snapshot");
}

static void GrokBillingMapsOnDemandWhenCapIsSet()
{
    var billing = GrokUsageReader.ParseBillingResponse("""
        {
          "creditUsagePercent": 40,
          "currentPeriod": {
            "type": "USAGE_PERIOD_TYPE_WEEKLY",
            "start": "2026-07-26T00:00:00Z",
            "end": "2026-08-02T00:00:00Z"
          },
          "onDemandCap": { "val": 20 },
          "onDemandUsed": { "val": 5 }
        }
        """);

    var snapshot = GrokUsageReader.MapUsage(billing);

    Assert(snapshot.Secondary is not null, "on-demand window expected");
    AssertEqual("On-demand", snapshot.Secondary!.Title, "on-demand title");
    AssertClose(25m, (decimal)snapshot.Secondary.UsedPercent, "on-demand percent");
    Assert(snapshot.Cost is not null, "on-demand cost expected");
    AssertClose(5m, snapshot.Cost!.Used, "on-demand used");
    AssertClose(20m, snapshot.Cost.Limit!.Value, "on-demand cap");
}

static void GrokBillingAcceptsWrappedConfigPayloads()
{
    var billing = GrokUsageReader.ParseBillingResponse("""
        {
          "config": {
            "creditUsagePercent": 5,
            "currentPeriod": {
              "type": "USAGE_PERIOD_TYPE_WEEKLY",
              "start": "2026-07-26T00:00:00Z",
              "end": "2026-08-02T00:00:00Z"
            }
          },
          "subscriptionTier": "SuperGrok Heavy"
        }
        """);

    var snapshot = GrokUsageReader.MapUsage(billing);
    AssertClose(5m, (decimal)snapshot.Primary.UsedPercent, "wrapped credit percent");
    AssertEqual("SuperGrok Heavy", snapshot.PlanType!, "subscription tier from wrapper root");
}

// The plain /v1/billing endpoint (no ?format=credits) that the reader falls back to on
// monthly-limit / unified-billing accounts. It carries no creditUsagePercent - only used and
// monthlyLimit - and MapUsage must derive the percentage from those. This is the exact payload
// shape that produced "Grok billing response did not include credit usage" before the fallback.
static void GrokBillingMapsMonthlyUsedLimitPercent()
{
    var billing = GrokUsageReader.ParseBillingResponse("""
        {
          "config": {
            "monthlyLimit": { "val": 15000 },
            "used": { "val": 1391 },
            "onDemandCap": { "val": 0 },
            "billingPeriodStart": "2026-08-01T00:00:00+00:00",
            "billingPeriodEnd": "2026-09-01T00:00:00+00:00"
          }
        }
        """);

    var snapshot = GrokUsageReader.MapUsage(billing);
    AssertClose(9.273333m, (decimal)snapshot.Primary.UsedPercent, "monthly used/limit percent");
    Assert(snapshot.Secondary is null, "on-demand should be omitted when cap is zero");
}

// The ?format=credits view returns period and on-demand data but no usage figure on those same
// accounts. MapUsage cannot produce a percentage from it, which is why the reader retries the
// plain endpoint; asserting the throw documents the trigger the fallback exists for.
static void GrokBillingCreditsViewWithoutUsageThrows()
{
    var billing = GrokUsageReader.ParseBillingResponse("""
        {
          "config": {
            "currentPeriod": {
              "type": "USAGE_PERIOD_TYPE_WEEKLY",
              "start": "2026-08-02T04:07:16.204303+00:00",
              "end": "2026-08-09T04:07:16.204303+00:00"
            },
            "onDemandCap": { "val": 0 },
            "onDemandUsed": { "val": 0 },
            "prepaidBalance": { "val": 0 },
            "isUnifiedBillingUser": true
          }
        }
        """);

    var threw = false;
    try
    {
        GrokUsageReader.MapUsage(billing);
    }
    catch (InvalidOperationException)
    {
        threw = true;
    }

    Assert(threw, "credits view without usage should be unmappable");
}

// Unified-billing accounts get no subscriptionTier from the billing API and no subscription_tier in
// auth.json, so the token's tier claim is the only statement of the plan. 7 is SuperGrok Plus.
static void GrokReadsPlanFromTokenTierClaim()
{
    var billing = GrokUsageReader.ParseBillingResponse("""
        {
          "config": {
            "creditUsagePercent": 79,
            "currentPeriod": {
              "type": "USAGE_PERIOD_TYPE_WEEKLY",
              "start": "2026-08-02T17:17:50.027845+00:00",
              "end": "2026-08-09T17:17:50.027845+00:00"
            },
            "onDemandCap": { "val": 0 },
            "isUnifiedBillingUser": true
          }
        }
        """);

    AssertEqual("supergrok_plus", GrokUsageReader.TierSlugFromAccessToken(GrokAccessToken("""{"tier":7}"""))!, "tier 7");
    AssertEqual("supergrok_heavy", GrokUsageReader.TierSlugFromAccessToken(GrokAccessToken("""{"tier":8}"""))!, "tier 8");
    Assert(
        GrokUsageReader.TierSlugFromAccessToken(GrokAccessToken("""{"tier":99}""")) is null,
        "an unknown tier ordinal must not be guessed at");
    Assert(
        GrokUsageReader.TierSlugFromAccessToken("not-a-jwt") is null,
        "an opaque token carries no tier");

    var snapshot = GrokUsageReader.MapUsage(
        billing,
        new GrokUsageReader.GrokSessionCredentials(
            GrokAccessToken("""{"tier":7}"""),
            true,
            null,
            DateTimeOffset.UtcNow.AddHours(1),
            "user@example.com",
            "user-1",
            "https://auth.x.ai",
            "client",
            GrokUsageReader.TierSlugFromAccessToken(GrokAccessToken("""{"tier":7}"""))));

    AssertEqual("supergrok_plus", snapshot.PlanType!, "plan from the token claim");
    AssertEqual("SuperGrok Plus", ProviderPlanFormatter.DisplayName(UsageProvider.Grok, snapshot.PlanType!), "displayed plan");
}

static void GrokPlanSlugsRenderAsBrandNames()
{
    AssertEqual("SuperGrok", ProviderPlanFormatter.DisplayName(UsageProvider.Grok, "supergrok"), "base tier slug");
    AssertEqual("SuperGrok Plus", ProviderPlanFormatter.DisplayName(UsageProvider.Grok, "supergrok_plus"), "plus tier slug");
    AssertEqual("SuperGrok Heavy", ProviderPlanFormatter.DisplayName(UsageProvider.Grok, "supergrok_heavy"), "heavy tier slug");
    // The billing API states the brand directly; both spellings must land on one string.
    AssertEqual("SuperGrok Heavy", ProviderPlanFormatter.DisplayName(UsageProvider.Grok, "SuperGrok Heavy"), "brand from billing");
}

static void GrokAccountsKeyAndResolvePerHome()
{
    Assert(ProviderKeys.IsGrok(ProviderKeys.Grok("default")), "a Grok key must be recognised as one");
    Assert(!ProviderKeys.IsGrok(ProviderKeys.Codex("default")), "a Codex key is not a Grok key");
    Assert(
        ProviderKeys.ProviderOf(ProviderKeys.Grok("work")) == UsageProvider.Grok,
        "an account-scoped Grok key routes to the Grok provider");
    Assert(
        ProviderKeys.ProviderOf(ProviderKeys.Codex("work")) == UsageProvider.Codex,
        "Codex keys still route to Codex");
    Assert(
        ProviderKeys.ProviderOf(ProviderKeys.Claude) == UsageProvider.Claude,
        "the singleton providers are unaffected");

    var configured = new GrokAccountEntry("work", "Work", @"C:\grok-homes\work");
    Assert(!configured.IsDefault, "an entry with a home folder is not the built-in one");
    AssertEqual(@"C:\grok-homes\work", configured.ResolveHome(), "configured home");
    AssertEqual(@"C:\grok-homes\work\auth.json", configured.ResolveAuthPath(), "configured auth path");
    AssertEqual(@"C:\grok-homes\work\sessions", configured.ResolveSessionsPath(), "configured sessions path");

    // The built-in entry follows GROK_HOME, exactly like the Grok CLI itself.
    var previous = Environment.GetEnvironmentVariable("GROK_HOME");
    try
    {
        Environment.SetEnvironmentVariable("GROK_HOME", @"C:\grok-homes\env");
        var builtIn = new GrokAccountEntry(GrokAccountSettings.DefaultId, "Grok", null);
        Assert(builtIn.IsDefault, "an entry without a home folder is the built-in one");
        AssertEqual(@"C:\grok-homes\env\auth.json", builtIn.ResolveAuthPath(), "GROK_HOME auth path");
    }
    finally
    {
        Environment.SetEnvironmentVariable("GROK_HOME", previous);
    }
}

// Two Grok accounts have to be two named segments, not one "Grok" line: the tooltip is the only
// place the tray states both, and a shared label would make them indistinguishable.
static void TooltipNamesEveryGrokAccount()
{
    var grokEntries = new[]
    {
        new GrokAccountEntry("default", "Grok", null),
        new GrokAccountEntry("alt", "Grok alt", @"C:\grok-homes\alt")
    };

    var grokUsage = new Dictionary<string, ProviderUsageLookupResult>(StringComparer.Ordinal)
    {
        [ProviderKeys.Grok("default")] = new(GrokSnapshot(20), null),
        [ProviderKeys.Grok("alt")] = new(GrokSnapshot(60), null)
    };

    var tooltip = UsageTooltip.Build(
        [],
        new Dictionary<string, ProviderUsageLookupResult>(StringComparer.Ordinal),
        new ProviderUsageLookupResult(null, "not loaded"),
        grokEntries,
        grokUsage,
        new ProviderUsageLookupResult(null, "not loaded"),
        new ProviderUsageLookupResult(null, "not loaded"),
        new UiSettings
        {
            CodexEnabled = false,
            ClaudeEnabled = false,
            GrokEnabled = true,
            CursorEnabled = false,
            OpenCodeGoEnabled = false
        });

    AssertEqual("Grok 20% 7d, Grok alt 60% 7d", tooltip, "both Grok accounts in the tooltip");
}

static ProviderUsageSnapshot GrokSnapshot(double usedPercent) => new(
    UsageProvider.Grok,
    DateTimeOffset.Now,
    "supergrok_plus",
    new ProviderUsageWindow("Weekly limit", usedPercent, 10080, null),
    null,
    "Grok CLI billing");

/// <summary>Builds a JWT-shaped token whose payload is <paramref name="payloadJson"/>.</summary>
static string GrokAccessToken(string payloadJson)
{
    var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    return $"header.{payload}.signature";
}

static void GrokHistoryAggregatesTurnCompletedUsage()
{
    using var fixture = new GrokFixture();
    var now = DateTimeOffset.Now.ToUnixTimeSeconds();
    fixture.WriteSessionLog(
        "session-a",
        GrokTurnCompletedLine(now, "prompt-1", "grok-4.5-build", input: 1000, cacheRead: 200, cacheCreate: 0, output: 50, reasoning: 10, costTicks: null),
        GrokTurnCompletedLine(now, "prompt-2", "grok-4.5", input: 500, cacheRead: 0, cacheCreate: 0, output: 25, reasoning: 0, costTicks: null));

    // 1000 + 50 and 500 + 25. The reasoning columns (10 and 0) are NOT added: reasoningTokens is a
    // breakdown of outputTokens, which the fixture encodes the way real logs do (totalTokens ==
    // inputTokens + outputTokens). Adding them made this 1585 and inflated every Grok chart.
    var today = Today(fixture.Read());
    AssertEqual(1575L, today.TotalTokens, "grok local tokens");
    Assert(today.EstimatedCostUsd > 0, "estimated cost should be positive from built-in rates");
}

static void GrokHistoryExcludesRowsOutsideReport()
{
    using var fixture = new GrokFixture();
    fixture.WriteSessionLog(
        "session-window",
        GrokTurnCompletedLine(DateTimeOffset.Now.AddDays(-31).ToUnixTimeSeconds(), "prompt-old", "grok-4.5", input: 100, cacheRead: 0, cacheCreate: 0, output: 10, reasoning: 0, costTicks: null),
        GrokTurnCompletedLine(DateTimeOffset.Now.ToUnixTimeSeconds(), "prompt-current", "grok-4.5", input: 10, cacheRead: 0, cacheCreate: 0, output: 5, reasoning: 0, costTicks: null));

    var result = fixture.Read();
    Assert(result.Insights is not null, result.Error ?? "missing Grok insights");
    AssertEqual(15L, result.Insights!.Last30DaysTokens, "30-day total excludes the lookback buffer");
    AssertEqual(15L, result.Insights.Models.Sum(model => model.TotalTokens), "model totals use the report window");
}

static void GrokHistoryDoesNotDoubleCountReasoningTokens()
{
    using var fixture = new GrokFixture();
    var now = DateTimeOffset.Now.ToUnixTimeSeconds();

    // The shape that made the bug visible: a short answer that is mostly thinking. Folding
    // reasoning into output reported 106 output tokens for 57, an 86% overstatement.
    fixture.WriteSessionLog(
        "session-reasoning",
        GrokTurnCompletedLine(now, "prompt-r", "grok-4.5", input: 46327, cacheRead: 46080, cacheCreate: 0, output: 57, reasoning: 49, costTicks: null));

    var today = Today(fixture.Read());
    AssertEqual(46384L, today.TotalTokens, "reasoning tokens are part of output, not extra to it");
}

static void GrokHistoryPrefersStampedCostTicks()
{
    using var fixture = new GrokFixture();
    var now = DateTimeOffset.Now.ToUnixTimeSeconds();
    fixture.WriteSessionLog(
        "session-b",
        GrokTurnCompletedLine(now, "prompt-cost", "grok-4.5", input: 10, cacheRead: 0, cacheCreate: 0, output: 5, reasoning: 0, costTicks: 15_000_000_000m));

    var today = Today(fixture.Read());
    AssertClose(1.5m, today.EstimatedCostUsd, "stamped cost ticks should win over estimates");
}

static string GrokTurnCompletedLine(
    long unixSeconds,
    string promptId,
    string model,
    long input,
    long cacheRead,
    long cacheCreate,
    long output,
    long reasoning,
    decimal? costTicks)
{
    var modelUsage = new Dictionary<string, object?>
    {
        [model] = new Dictionary<string, object?>
        {
            ["inputTokens"] = input,
            ["outputTokens"] = output,
            ["totalTokens"] = input + output,
            ["cachedReadTokens"] = cacheRead,
            ["cacheCreationTokens"] = cacheCreate,
            ["reasoningTokens"] = reasoning,
            ["modelCalls"] = 1,
            ["costUsdTicks"] = costTicks
        }
    };

    var usage = new Dictionary<string, object?>
    {
        ["inputTokens"] = input,
        ["outputTokens"] = output,
        ["totalTokens"] = input + output,
        ["cachedReadTokens"] = cacheRead,
        ["cacheCreationTokens"] = cacheCreate,
        ["reasoningTokens"] = reasoning,
        ["modelCalls"] = 1,
        ["modelUsage"] = modelUsage
    };

    if (costTicks is { } ticks)
    {
        usage["costUsdTicks"] = ticks;
    }

    var payload = new
    {
        timestamp = unixSeconds,
        method = "_x.ai/session/update",
        @params = new
        {
            sessionId = "test-session",
            update = new
            {
                sessionUpdate = "turn_completed",
                prompt_id = promptId,
                stop_reason = "end_turn",
                usage
            }
        }
    };

    return JsonSerializer.Serialize(payload);
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

    // A half-hour offset puts the columns on the UTC grid, so they run :30 to :30 and the first one
    // of the local day is 00:30 - which is where 19:00Z belongs and where it is labelled.
    var istHours = UsageLedger.Query(UsageLedgerScope.Codex, new DateTimeOffset(2026, 5, 11, 0, 0, 0, TimeSpan.FromMinutes(330)), new DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.FromMinutes(330)), UsageLedgerGranularity.Hour, ist);
    AssertEqual(24, istHours.Buckets.Count, "a half-hour offset still yields 24 hourly buckets");
    AssertEqual(30, istHours.Buckets[0].StartLocal.DateTime.Minute, "a half-hour offset puts the columns on the UTC grid");
    AssertEqual(1100L, istHours.Buckets[0].TotalTokens, "19:00Z lands in the 00:30-01:30 local bucket");
}

/// <summary>
/// The half-hour-offset regression: an hour column must be named for the time it actually covers.
/// </summary>
/// <remarks>
/// Records are keyed by a whole UTC hour, so under +05:30 the record covering 16:30-17:30 local
/// used to land whole in a column drawn as 16:00-17:00 and labelled "4 PM" - a 17:12 session read
/// as usage at 4 PM, every bar in the day half an hour early.
/// </remarks>
static void UsageLedgerNamesHourColumnsForTheTimeTheyCover()
{
    using var fixture = new UsageLedgerFixture();
    var ist = TimeZoneInfo.CreateCustomTimeZone("test-ist", TimeSpan.FromMinutes(330), "test-ist", "test-ist");

    // 11:42Z is 17:12 local - inside the 16:30-17:30 local column.
    var at = new DateTimeOffset(2026, 5, 11, 11, 42, 0, TimeSpan.Zero);
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 1000, 0, 100, false))), "seed merge");

    var from = new DateTimeOffset(2026, 5, 11, 0, 0, 0, TimeSpan.FromMinutes(330));
    var hours = UsageLedger.Query(UsageLedgerScope.Codex, from, from.AddDays(1), UsageLedgerGranularity.Hour, ist);

    var used = hours.Buckets.Where(bucket => bucket.TotalTokens > 0).ToArray();
    AssertEqual(1, used.Length, "the record belongs to exactly one column");
    AssertEqual(16, used[0].StartLocal.DateTime.Hour, "the column holding 17:12 local starts at 16:30");
    AssertEqual(30, used[0].StartLocal.DateTime.Minute, "the column holding 17:12 local starts at 16:30");
    Assert(
        used[0].EndLocalExclusive.DateTime == used[0].StartLocal.DateTime.AddHours(1),
        "and it ends an hour later, at 17:30 - the session is inside it");
    AssertEqual("h:mm tt", GraphsPeriod.HourPattern(used[0].StartLocal.DateTime), "a :30 column must be labelled with its minutes, not rounded to \"4 PM\"");

    // The columns still partition the day the DAY bucket claims, so the two totals agree.
    var day = UsageLedger.Query(UsageLedgerScope.Codex, from, from.AddDays(1), UsageLedgerGranularity.Day, ist);
    AssertEqual(day.TotalTokens, hours.Buckets.Sum(bucket => bucket.TotalTokens), "the hour columns must sum to the day");
    AssertEqual(1100L, day.TotalTokens, "and to the record that was merged");
}

/// <summary>
/// A zone with US-style DST rules, built by hand.
/// </summary>
/// <remarks>
/// EXPLICIT because the machine's own zone is not a test input: half the world (IST, UTC on most CI
/// images) has no transitions at all, so a DST test that read <c>TimeZoneInfo.Local</c> would pass
/// everywhere by testing nothing, and fail nowhere until a user in Chicago hit it.
/// </remarks>
static TimeZoneInfo DstTestZone()
{
    // Second Sunday in March and first Sunday in November, both at 02:00 local - so the
    // short day is 9 March 2025 and the long day is 2 November 2025 - both safely in the PAST, since
    // the ledger refuses to record a future day at all.
    var toDst = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
    var toStandard = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
    var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
        DateTime.MinValue.Date,
        DateTime.MaxValue.Date,
        TimeSpan.FromHours(1),
        toDst,
        toStandard);

    return TimeZoneInfo.CreateCustomTimeZone(
        "test-dst",
        TimeSpan.FromHours(-8),
        "test-dst",
        "test-dst-standard",
        "test-dst-daylight",
        [rule]);
}

/// <summary>Local midnight as the instant it actually is in <paramref name="zone"/>.</summary>
static DateTimeOffset LocalMidnight(TimeZoneInfo zone, int year, int month, int day)
{
    var local = new DateTime(year, month, day, 0, 0, 0);
    return new DateTimeOffset(local, zone.GetUtcOffset(local));
}

static void UsageLedgerBuildsDayHoursOnTheLocalTimeline()
{
    using var fixture = new UsageLedgerFixture();
    var zone = DstTestZone();

    static (int Buckets, double Elapsed, int WholeBuckets) Probe(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, out UsageLedgerSeries series)
    {
        series = UsageLedger.Query(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Hour, zone);

        // The count the CHART draws and the count the METRICS divide by have to be the same number,
        // which is the whole point of building the buckets from the real timeline: Elapsed is fed
        // the very buckets that were plotted.
        var elapsed = GraphsPeriod.Elapsed(
            series.Buckets.Select(bucket => (bucket.StartLocal, bucket.EndLocalExclusive)).ToArray(),
            to,
            null);
        return (series.Buckets.Count, elapsed.Fraction, elapsed.Buckets);
    }

    static void AssertHourlyPartition(UsageLedgerSeries series, DateTimeOffset from, DateTimeOffset to)
    {
        for (var index = 0; index < series.Buckets.Count; index++)
        {
            var bucket = series.Buckets[index];
            Assert(
                bucket.EndLocalExclusive - bucket.StartLocal == TimeSpan.FromHours(1),
                $"every hour column must be exactly one real hour wide, bucket {index} was {bucket.EndLocalExclusive - bucket.StartLocal}");

            var expected = from.AddHours(index);
            Assert(
                bucket.StartLocal.UtcDateTime == expected.UtcDateTime,
                $"bucket {index} must start at {expected:u}, was {bucket.StartLocal:u}");
        }

        Assert(
            series.Buckets[^1].EndLocalExclusive.UtcDateTime == to.UtcDateTime,
            "the columns must cover the day exactly, with nothing left over");
    }

    // ---- SPRING FORWARD: 23 real hours, and 02:00 local never happens -------------------------
    var shortFrom = LocalMidnight(zone, 2025, 3, 9);
    var shortTo = LocalMidnight(zone, 2025, 3, 10);
    Assert(shortTo - shortFrom == TimeSpan.FromHours(23), "9 March 2025 is a 23-hour day in this zone");

    // 08:00Z is 00:00 local; 10:00Z is 03:00 local, the hour on the far side of the gap.
    Assert(
        UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(
            true,
            (new DateTimeOffset(2025, 3, 9, 8, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 1000, 0, 100, false),
            (new DateTimeOffset(2025, 3, 9, 10, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 2000, 0, 200, false))),
        "short-day seed merge");

    var shortProbe = Probe(shortFrom, shortTo, zone, out var shortDay);
    AssertEqual(23, shortProbe.Buckets, "a 23-hour day has 23 columns, not 24 with a phantom");
    AssertHourlyPartition(shortDay, shortFrom, shortTo);
    AssertEqual(3, shortDay.Buckets[2].StartLocal.DateTime.Hour, "the third column is 03:00 local - 02:00 does not exist");
    AssertEqual(1100L, shortDay.Buckets[0].TotalTokens, "00:00 local keeps its own usage");
    AssertEqual(2200L, shortDay.Buckets[2].TotalTokens, "the hour after the gap keeps its own usage");
    AssertEqual(3300L, shortDay.TotalTokens, "no usage is lost or doubled across the transition");

    // The regression this fixes: the phantom zero-width column was counted as a whole elapsed hour,
    // so a finished 23-hour day claimed 24 and both Average and Projected were diluted by 1/24.
    AssertEqual(23, shortProbe.WholeBuckets, "a finished short day has 23 elapsed hours, not 24");
    Assert(Math.Abs(shortProbe.Elapsed - 23) < 0.001, $"the elapsed fraction matches the column count, got {shortProbe.Elapsed}");

    // ---- FALL BACK: 25 real hours, and 01:00 local happens twice -------------------------------
    var longFrom = LocalMidnight(zone, 2025, 11, 2);
    var longTo = LocalMidnight(zone, 2025, 11, 3);
    Assert(longTo - longFrom == TimeSpan.FromHours(25), "2 November 2025 is a 25-hour day in this zone");

    // Both of these are "01:00 local" - the first in daylight time, the second in standard time.
    Assert(
        UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(
            true,
            (new DateTimeOffset(2025, 11, 2, 8, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 1000, 0, 100, false),
            (new DateTimeOffset(2025, 11, 2, 9, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 2000, 0, 200, false))),
        "long-day seed merge");

    var longProbe = Probe(longFrom, longTo, zone, out var longDay);
    AssertEqual(25, longProbe.Buckets, "a 25-hour day has 25 columns");
    AssertHourlyPartition(longDay, longFrom, longTo);
    AssertEqual(1, longDay.Buckets[1].StartLocal.DateTime.Hour, "the repeated hour's first pass is 01:00 local");
    AssertEqual(1, longDay.Buckets[2].StartLocal.DateTime.Hour, "and so is its second pass");
    Assert(longDay.Buckets[1].StartLocal.Offset != longDay.Buckets[2].StartLocal.Offset, "the two passes differ by their offset, which is what tells them apart");

    // The regression this fixes: both hours used to fold into one column, so one of them vanished.
    AssertEqual(1100L, longDay.Buckets[1].TotalTokens, "the first 01:00 keeps its own usage");
    AssertEqual(2200L, longDay.Buckets[2].TotalTokens, "the second 01:00 is a column of its own");
    AssertEqual(3300L, longDay.TotalTokens, "no usage is lost or doubled across the transition");
    AssertEqual(25, longProbe.WholeBuckets, "a finished long day has 25 elapsed hours");
    Assert(Math.Abs(longProbe.Elapsed - 25) < 0.001, $"the elapsed fraction matches the column count, got {longProbe.Elapsed}");
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

// The defect this pins: Claude pricing chose between models.dev and the built-in table per RECORD
// (catalog ?? built-in). models.dev's anthropic entries for claude-sonnet-4-5 / -4-6 carry base rates
// and NO context_over_200k block, so the catalog record replaced the built-in one wholesale and took
// the 200k tier with it — those models priced FLAT, and the long-context premium was live only for
// the one sonnet id the catalog happens not to carry. Resolution is per FIELD now.
static void ClaudePricingKeepsBuiltInTierWhereCatalogIsSilent()
{
    // A pinned catalog, because the real one is a network fetch cached per machine: without this the
    // test would assert whatever models.dev served this developer today, which is exactly the kind of
    // "passes here" pricing test that let the defect through.
    //   claude-sonnet-4-5  base rates only  -> the silent case, built-in 200k tier must survive
    //   claude-opus-4-5    fully described  -> catalog must win outright, rates AND tier
    //   claude-sonnet-4-6  tier disagrees   -> an EXPLICIT catalog tier must override the built-in one
    const string catalogJson = """
        {
          "anthropic": {
            "id": "anthropic",
            "models": {
              "claude-sonnet-4-5": {
                "id": "claude-sonnet-4-5",
                "cost": { "input": 3, "output": 15, "cache_read": 0.3, "cache_write": 3.75 }
              },
              "claude-opus-4-5": {
                "id": "claude-opus-4-5",
                "cost": {
                  "input": 7, "output": 35, "cache_read": 0.7, "cache_write": 8.75,
                  "context_over_200k": { "input": 14, "output": 70, "cache_read": 1.4, "cache_write": 17.5 }
                }
              },
              "claude-sonnet-4-6": {
                "id": "claude-sonnet-4-6",
                "cost": {
                  "input": 3, "output": 15, "cache_read": 0.3, "cache_write": 3.75,
                  "context_over_200k": { "input": 9, "output": 45, "cache_read": 0.9, "cache_write": 11.25 }
                }
              }
            }
          }
        }
        """;

    using var catalogOverride = ModelsDevPricing.OverrideCatalogForTests(catalogJson);

    // The precondition the whole test rests on, asserted rather than assumed: the catalog really is
    // SILENT about a long-context tier for this model, which is what used to erase the built-in one.
    var rawCatalog = ModelsDevPricing.Lookup("anthropic", "claude-sonnet-4-5");
    Assert(rawCatalog is not null, "the pinned catalog must resolve claude-sonnet-4-5");
    Assert(rawCatalog!.ThresholdTokens is null, "and must carry no long-context tier of its own");

    var sonnet45 = ClaudeModelPricing.For("claude-sonnet-4-5");
    Assert(sonnet45 is not null, "claude-sonnet-4-5 must price");
    AssertEqual(200_000, sonnet45!.ThresholdTokens ?? 0, "the built-in 200k tier survives a silent catalog");
    AssertEqual(3.00m, sonnet45.InputPerMillion, "base rates still come from the catalog");
    AssertEqual(6.00m, sonnet45.InputPerMillionAboveThreshold ?? 0m, "above-threshold input from the built-in table");
    AssertEqual(22.50m, sonnet45.OutputPerMillionAboveThreshold ?? 0m, "above-threshold output from the built-in table");
    AssertEqual(0.60m, sonnet45.CacheReadPerMillionAboveThreshold ?? 0m, "above-threshold cache read from the built-in table");
    AssertEqual(7.50m, sonnet45.CacheCreationPerMillionAboveThreshold ?? 0m, "above-threshold cache write from the built-in table");

    // A model the catalog fully describes stays catalog-driven end to end — that is the whole point of
    // fetching it. Every one of these numbers differs from the built-in row (5.00 / 25.00 / 0.50 /
    // 6.25, no tier at all), so a built-in leak would show up here immediately.
    var opus45 = ClaudeModelPricing.For("claude-opus-4-5");
    Assert(opus45 is not null, "claude-opus-4-5 must price");
    AssertEqual(7.00m, opus45!.InputPerMillion, "catalog base input wins over the built-in 5.00");
    AssertEqual(35.00m, opus45.OutputPerMillion, "catalog base output wins over the built-in 25.00");
    AssertEqual(0.70m, opus45.CacheReadPerMillion ?? 0m, "catalog cache read wins");
    AssertEqual(8.75m, opus45.CacheCreationPerMillion ?? 0m, "catalog cache write wins");
    AssertEqual(200_000, opus45.ThresholdTokens ?? 0, "a catalog tier applies even where the built-in row has none");
    AssertEqual(14.00m, opus45.InputPerMillionAboveThreshold ?? 0m, "and its above-threshold column is the catalog's");

    // And where BOTH sources carry a tier the catalog's wins: the built-in above-input for -4-6 is
    // 6.00, so 9.00 proves the fallback is per-field and not "built-in wins the tier".
    var sonnet46 = ClaudeModelPricing.For("claude-sonnet-4-6");
    AssertEqual(9.00m, sonnet46!.InputPerMillionAboveThreshold ?? 0m, "an explicit catalog tier overrides the built-in one");
    AssertEqual(45.00m, sonnet46.OutputPerMillionAboveThreshold ?? 0m, "on every component");

    // Now the dollars, on BOTH paths. The scan path is reached through the reader's own private entry
    // point, so these are the figures the 30-day history actually renders.
    var scanEstimate = typeof(ClaudeUsageInsightsReader).GetMethod("EstimateCost", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    Assert(scanEstimate is not null, "ClaudeUsageInsightsReader.EstimateCost should exist");
    decimal ScanCost(string model, long rawInput, long cachedInput, long cacheCreation, long output)
        => (decimal)(scanEstimate!.Invoke(null, [model, rawInput, cachedInput, cacheCreation, output]) as decimal? ?? -1m);

    using var fixture = new UsageLedgerFixture();
    var day = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
    var slot = 0;

    // One row per hour of the same day, each queried alone, so the cases stay independent.
    decimal LedgerCost(string model, long rawInput, long cachedInput, long cacheCreation, long output)
    {
        var at = day.AddHours(slot++);
        var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
        builder.CoverDay(at);
        builder.AddClaudeRow(at, model, rawInput, cachedInput, cacheCreation, output, ClaudeModelPricing.ThresholdTokensFor(model));
        Assert(UsageLedger.TryMerge(UsageLedgerScope.Claude, builder.Build(at)), "seed merge");

        var series = LedgerQueryUtc(UsageLedgerScope.Claude, at, at.AddHours(1), UsageLedgerGranularity.Hour, LedgerPricing.For(UsageLedgerScope.Claude));
        Assert(!series.HasIncompleteCost, "the model must price COMPLETELY on the ledger path");
        Assert(!series.ThresholdMismatch, "the query's threshold resolver must agree with the one the batch recorded");
        return series.TotalEstimatedCostUsd;
    }

    void Both(decimal expected, string model, long rawInput, long cachedInput, long cacheCreation, long output, string what)
    {
        AssertClose(expected, ScanCost(model, rawInput, cachedInput, cacheCreation, output), $"scan path: {what}");
        AssertClose(expected, LedgerCost(model, rawInput, cachedInput, cacheCreation, output), $"ledger path: {what}");
    }

    // claude-sonnet-4-5, wholly under the cutoff: unaffected by the tier either way, so it pins that
    // the merge did not disturb the base column. 0.1M*3 + 0.05M*0.30 + 0.02M*3.75 + 0.01M*15.
    Both(0.54m, "claude-sonnet-4-5", 100_000, 50_000, 20_000, 10_000, "sonnet-4-5 wholly sub-threshold");

    // Input past the cutoff. Priced FLAT (the defect) this is 250k*3 = 0.75 + 0.003 + 0.01875 + 0.12
    // = 0.89175; with the tier restored the 50k over bills at 6.00: 0.2M*3 + 0.05M*6 = 0.90.
    Both(1.04175m, "claude-sonnet-4-5", 250_000, 10_000, 5_000, 8_000, "sonnet-4-5 input past the cutoff splits");

    // Every component past the cutoff at once. Flat this would be 300k at base across the board
    // = 0.90 + 0.09 + 1.125 + 4.50 = 6.615; tiered: input 0.2*3+0.1*6=1.20; cache read
    // 0.2*0.30+0.1*0.60=0.12; cache write 0.2*3.75+0.1*7.50=1.50; output 0.2*15+0.1*22.50=5.25.
    Both(8.07m, "claude-sonnet-4-5", 300_000, 300_000, 300_000, 300_000, "sonnet-4-5 all four components split");

    // The fully-described model, in dollars, at the catalog's own rates: input 0.2*7+0.1*14=2.80;
    // cache read 0.2*0.70+0.1*1.40=0.28; cache write 0.2*8.75+0.1*17.50=3.50;
    // output 0.2*35+0.1*70=14.00.
    Both(20.58m, "claude-opus-4-5", 300_000, 300_000, 300_000, 300_000, "opus-4-5 prices entirely from the catalog");
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
    AssertEqual(0, result.DaysImported, "nothing committed, so nothing is claimed");
    Assert(result.Message.Contains("Nothing was changed", StringComparison.Ordinal), "and it may say so, because it is true");
}

/// <summary>
/// The merge is per CORPUS, so a cancel that arrives while the second one is being read finds the
/// first already fully written. Reporting "nothing was changed" there is a lie about the user's own
/// data - the one report a data store must never produce.
/// </summary>
static void UsageLedgerBackfillReportsWhatItCommittedWhenCancelled()
{
    using var fixture = new UsageLedgerFixture();
    using var cancellation = new CancellationTokenSource();
    var at = new DateTimeOffset(2026, 6, 2, 11, 0, 0, TimeSpan.Zero);

    var committed = new StubBackfillSource(UsageLedgerScope.Codex, at);
    var abandoned = new StubBackfillSource(UsageLedgerScope.Claude, at) { CancelOnScan = cancellation };

    var result = UsageLedgerBackfill.Run(null, cancellation.Token, [committed, abandoned]);

    Assert(result.Outcome == UsageLedgerBackfillOutcome.Cancelled, $"cancelled outcome: {result.Outcome} - {result.Message}");

    // The guarantee that survives: no corpus is ever half-written.
    Assert(UsageLedger.GetCoverage(UsageLedgerScope.Codex).HasHistory, "the corpus that committed before the cancel keeps its rows");
    Assert(!UsageLedger.GetCoverage(UsageLedgerScope.Claude).HasHistory, "the corpus cancelled mid-scan writes nothing at all");

    // The guarantee that was missing: the report matches what is on disk.
    AssertEqual(1, result.DaysImported, "a cancelled run must report the days it actually wrote");
    AssertEqual(new DateOnly(2026, 6, 2), result.FirstDay!.Value, "and the range it wrote them over");
    Assert(result.FilesScanned > 0, "and the files it read on the way");
    Assert(
        !result.Message.Contains("Nothing was changed", StringComparison.Ordinal),
        $"a cancel that committed a corpus must not claim the ledger is untouched: {result.Message}");
}

/// <summary>
/// One corrupt timestamp used to expand a merge to ~739,000 covered days over ~2,000 year shards -
/// each one a read-modify-write under a cross-process mutex - and the import stopped responding.
/// </summary>
static void UsageLedgerDropsImplausibleTimestamps()
{
    using var fixture = new UsageLedgerFixture();
    var good = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
    // A zeroed timestamp, a garbage epoch far in the future, and a Unix-epoch sentinel: the three
    // shapes a broken or future log format actually produces.
    builder.AddCodexRow(DateTimeOffset.MinValue, "gpt-5.6-sol", 1000, 0, 100, isFast: false, thresholdTokens: 272_000);
    builder.AddCodexRow(DateTimeOffset.MaxValue, "gpt-5.6-sol", 1000, 0, 100, isFast: false, thresholdTokens: 272_000);
    builder.AddCodexRow(DateTimeOffset.UnixEpoch, "gpt-5.6-sol", 1000, 0, 100, isFast: false, thresholdTokens: 272_000);
    builder.AddCodexRow(good, "gpt-5.6-sol", 1000, 0, 100, isFast: false, thresholdTokens: 272_000);

    AssertEqual(1, builder.RecordCount, "only the plausible row is recorded");

    // Exactly what the backfill asks for when the earliest row it saw is corrupt.
    builder.CoverDays(DateTimeOffset.MinValue, DateTimeOffset.Now);

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, builder.Build(good)), "the merge must still succeed");
    stopwatch.Stop();

    Assert(
        stopwatch.Elapsed < TimeSpan.FromSeconds(30),
        $"a corrupt timestamp must not make the merge quadratic: it took {stopwatch.Elapsed}");

    // Fan-out is bounded by the plausible span, not by the corrupt value.
    var shards = Directory.GetFiles(fixture.Root, "codex-*.json").Length;
    var span = DateTime.UtcNow.Year - UsageTimestampText.EarliestPlausibleDay.Year + 1;
    Assert(shards <= span, $"shard fan-out must stay inside the plausible span: {shards} files for a {span}-year span");

    AssertEqual(
        1100L,
        LedgerQueryUtc(
            UsageLedgerScope.Codex,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UsageLedgerGranularity.Month).TotalTokens,
        "the one plausible row still lands");

    // Text timestamps are rejected at the same door, before anything downstream sees them.
    Assert(!UsageTimestampText.IsPlausibleDay(new DateOnly(1, 1, 1)), "year 0001 is not a session day");
    Assert(!UsageTimestampText.IsPlausibleDay(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)), "a month in the future is corrupt, not future");
    Assert(UsageTimestampText.IsPlausibleDay(DateOnly.FromDateTime(DateTime.UtcNow)), "today is plausible");
    Assert(UsageTimestampText.IsPlausibleDay(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)), "tomorrow is within the offset slack");
}

/// <summary>
/// The read paths deserialize whole year shards, and the graphs window drove all of them on the UI
/// thread for every history update and every period change.
/// </summary>
static void UsageLedgerCachesParsedShardsAndInvalidatesOnMerge()
{
    using var fixture = new UsageLedgerFixture();
    var at = new DateTimeOffset(2026, 5, 10, 13, 0, 0, TimeSpan.Zero);
    var from = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    var to = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    long Parses() => Interlocked.Read(ref UsageLedger.ShardParseCount);

    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 1000, 0, 100, false))), "seed merge");

    var beforeFirstRead = Parses();
    AssertEqual(1100L, LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day).TotalTokens, "seeded total");
    var afterFirstRead = Parses();
    Assert(afterFirstRead > beforeFirstRead, "the first read after a merge has to parse the shard");

    // Every read after it is served from the parsed copy - which is the whole point, because these
    // three are exactly what one graphs-window refresh runs.
    LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day);
    UsageLedger.GetCoverage(UsageLedgerScope.Codex);
    UsageLedger.QueryTotal(UsageLedgerScope.Codex, TimeZoneInfo.Utc);
    UsageLedger.WarmCache(UsageLedgerScope.Codex);
    AssertEqual(afterFirstRead, Parses(), "a warm shard is never deserialized twice");

    // A MERGE MUST INVALIDATE. A cache that survived one would show the user numbers their own
    // rescan had already corrected.
    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (at, "gpt-5.6-sol", 4000, 0, 500, false))), "corrected merge");
    AssertEqual(4500L, LedgerQueryUtc(UsageLedgerScope.Codex, from, to, UsageLedgerGranularity.Day).TotalTokens, "a merge must invalidate the read cache");
    Assert(Parses() > afterFirstRead, "and the shard is parsed again afterwards");
}

/// <summary>
/// The data-loss case the ledger's replace-by-scope makes possible: a Codex rollout named after the
/// day its session STARTED, still being appended to months later. The manual import reaches it; the
/// 30-day scan used to skip it by name and then delete the day it had contributed to.
/// </summary>
static void CodexScanKeepsHistoryImportedFromOldNamedSession()
{
    using var ledger = new UsageLedgerFixture();
    using var fixture = new CodexFixture();

    var now = DateTimeOffset.Now;
    var longAgo = DateOnly.FromDateTime(now.DateTime).AddDays(-120);

    // Named 120 days ago, written NOW, and carrying today's rows.
    fixture.WriteSessionLog(
        $"rollout-{longAgo:yyyy-MM-dd}T09-00-00-01960000-0000-7000-8000-000000000001.jsonl",
        CodexTokenCountLine(model: "gpt-5.6-sol", input: 5000, cacheRead: 0, output: 100, limitId: "codex"));

    // An ordinary session from today, so the scan has something to find either way and cannot bail
    // out early with "no session logs".
    fixture.WriteSessionLog(
        $"rollout-{DateOnly.FromDateTime(now.DateTime):yyyy-MM-dd}T10-00-00-01960000-0000-7000-8000-000000000002.jsonl",
        CodexTokenCountLine(model: "gpt-5.6-sol", input: 1000, cacheRead: 0, output: 20, limitId: "codex"));

    // What the manual import wrote for today, including the old-named file's 5,100 tokens.
    Assert(
        UsageLedger.TryMerge(UsageLedgerScope.Codex, LedgerCodexBatch(true, (now, "gpt-5.6-sol", 5000, 0, 100, false))),
        "seed the ledger the way the manual import would");

    var result = fixture.ReadWithLedger();
    Assert(result.Insights is not null, $"the scan must produce insights: {result.Error}");

    var window = LedgerQueryUtc(
        UsageLedgerScope.Codex,
        now.AddDays(-2).ToUniversalTime(),
        now.AddDays(2).ToUniversalTime(),
        UsageLedgerGranularity.Day);

    // The scan declares its 30 days COMPLETE, which deletes every existing record for them. Before
    // the enumeration was widened it could not see the old-named file, so the day came back holding
    // only the fresh session's 1,020 tokens and the imported 5,100 were gone.
    Assert(
        window.TotalTokens >= 5100L,
        $"a complete scan must not delete history it could not enumerate: {window.TotalTokens} tokens left");
    AssertEqual(6120L, window.TotalTokens, "both sessions are enumerated and the day is rewritten from both");

    // The flyout's own numbers agree, which is what makes the ledger and the chart the same story.
    AssertEqual(6000L, result.Insights!.Daily.Sum(day => day.InputTokens), "the 30-day scan counts the long-running session too");
}

/// <summary>
/// The worst failure this feature has: a scan DELETING months the user spent 1-3 minutes importing.
/// </summary>
/// <remarks>
/// A row is admitted by the scan on the calendar date its own log SPELLS, but a ledger record is
/// keyed by the true UTC instant - and one local day straddles two UTC days. A scan whose window
/// opens on local day F therefore emits records on UTC day F-1 (the tail of that UTC day which fell
/// inside local day F) after reading only a few hours of it. While a complete batch promoted its
/// records' days into its authority set, that sliver licensed the merge to delete the WHOLE of UTC
/// day F-1 and write the sliver back, so every graphs-window open shaved the oldest boundary day off
/// the backfilled history. The same arithmetic runs at the other end of the window with the opposite
/// sign of offset, which is why the second batch below clips a day AFTER its window.
/// </remarks>
static void UsageLedgerKeepsImportedHistoryOnClippedDays()
{
    using var fixture = new UsageLedgerFixture();

    var may = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    var june = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    UsageLedgerSeries DayTotals(DateTimeOffset from) =>
        LedgerQueryUtc(UsageLedgerScope.Codex, from, from.AddDays(1), UsageLedgerGranularity.Day);

    // ---- what the manual import wrote, in full -------------------------------------------------
    var import = new UsageLedgerBatchBuilder(accountingVersion: 3);
    for (var day = 1; day <= 31; day++)
    {
        import.CoverDay(new DateTimeOffset(2026, 5, day, 12, 0, 0, TimeSpan.Zero));
    }

    // The day BEFORE the scan window, holding a full day of usage: morning, plus the late hour a
    // +05:30 scan will re-read as the head of its own first local day.
    import.AddCodexRow(new DateTimeOffset(2026, 5, 9, 3, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 5000, 0, 500, false, 272_000);
    import.AddCodexRow(new DateTimeOffset(2026, 5, 9, 23, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 100, 0, 10, false, 272_000);

    // The day AFTER the scan window, same shape mirrored: the early hour is what a negative-offset
    // scan re-reads as the tail of its own last local day.
    import.AddCodexRow(new DateTimeOffset(2026, 5, 21, 1, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 200, 0, 20, false, 272_000);
    import.AddCodexRow(new DateTimeOffset(2026, 5, 21, 15, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 8000, 0, 800, false, 272_000);

    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, import.Build(june)), "the import must land");
    AssertEqual(5610L, DayTotals(new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero)).TotalTokens, "imported day before the window");
    AssertEqual(9020L, DayTotals(new DateTimeOffset(2026, 5, 21, 0, 0, 0, TimeSpan.Zero)).TotalTokens, "imported day after the window");

    // ---- and what a complete 30-day scan does to it ---------------------------------------------
    UsageLedgerBatch Scan()
    {
        var scan = new UsageLedgerBatchBuilder(accountingVersion: 3);

        // Declared window: UTC days 2026-05-10 .. 2026-05-20, read in full.
        for (var day = 10; day <= 20; day++)
        {
            scan.CoverDay(new DateTimeOffset(2026, 5, day, 12, 0, 0, TimeSpan.Zero));
        }

        // Exactly the rows the window's straddling local days drag in from either side.
        scan.AddCodexRow(new DateTimeOffset(2026, 5, 9, 23, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 100, 0, 10, false, 272_000);
        scan.AddCodexRow(new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 700, 0, 70, false, 272_000);
        scan.AddCodexRow(new DateTimeOffset(2026, 5, 21, 1, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 200, 0, 20, false, 272_000);
        return scan.Build(june);
    }

    Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, Scan()), "the scan must merge");

    AssertEqual(
        5610L,
        DayTotals(new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero)).TotalTokens,
        "a scan that only clipped the day before its window must not delete the imported day");
    AssertEqual(
        9020L,
        DayTotals(new DateTimeOffset(2026, 5, 21, 0, 0, 0, TimeSpan.Zero)).TotalTokens,
        "nor the imported day after it");
    AssertEqual(770L, DayTotals(new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero)).TotalTokens, "the declared days are still replaced");

    // Still idempotent on the clipped days: MAX is what keeps the sliver from accumulating.
    for (var i = 0; i < 3; i++)
    {
        Assert(UsageLedger.TryMerge(UsageLedgerScope.Codex, Scan()), "the scan must re-merge");
    }

    var month = LedgerQueryUtc(UsageLedgerScope.Codex, may, june, UsageLedgerGranularity.Month);
    AssertEqual(15_400L, month.TotalTokens, "re-scanning a clipped day must converge, not double count");
    Assert(!month.HasPartialDays, "a clipped day the ledger already held in full is not demoted to partial");
}

/// <summary>
/// The declaration side of the same boundary: a scan may only CLAIM a UTC day it read end to end.
/// </summary>
/// <remarks>
/// Both readers filter rows on the wall-clock date the log spells, and a log line is written either
/// in UTC (both CLIs stamp a Z) or in the local frame (no zone, or a numeric epoch). A row in frame
/// o at instant t survives iff t >= firstReportDay - o, so the scan can only vouch for instants from
/// firstReportDay - min(o) onward. With a NEGATIVE local offset that lands inside the first report
/// day itself, and claiming it would delete the hours before it.
/// </remarks>
static void UsageLedgerClaimsOnlyFullyCoveredUtcDays()
{
    var firstReportDay = new DateOnly(2026, 5, 10);
    var scannedAt = new DateTimeOffset(2026, 6, 8, 20, 0, 0, TimeSpan.FromHours(-8));

    static int UtcDay(int year, int month, int day)
        => UsageLedger.ToUtcDay(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero));

    static IReadOnlySet<int> Claimed(DateOnly firstReportDay, DateTimeOffset scannedAt, TimeSpan offset)
    {
        var builder = new UsageLedgerBatchBuilder(accountingVersion: 3);
        builder.CoverReportWindow(
            firstReportDay,
            scannedAt,
            TimeZoneInfo.CreateCustomTimeZone($"test{offset}", offset, "test", "test"));
        return builder.Build(scannedAt).CoveredUtcDays;
    }

    // +05:30. The guarantee starts exactly on the report day's midnight, so it is claimable - and
    // the day before it, which the local frame only clips, never is.
    var ahead = Claimed(firstReportDay, scannedAt, new TimeSpan(5, 30, 0));
    Assert(ahead.Contains(UtcDay(2026, 5, 10)), "a non-negative offset covers the first report day in full");
    Assert(!ahead.Contains(UtcDay(2026, 5, 9)), "the day before the window is never claimed");

    // -08:00. The scan has read 2026-05-10 only from 08:00 UTC, so it may not claim it.
    var behind = Claimed(firstReportDay, scannedAt, TimeSpan.FromHours(-8));
    Assert(!behind.Contains(UtcDay(2026, 5, 10)), "a negative offset leaves the first report day only partly read");
    Assert(behind.Contains(UtcDay(2026, 5, 11)), "the day after it is covered end to end");

    // The top is the clock, not local "today": nothing past the scan may be claimed, in either
    // frame, or a complete batch would delete a future-stamped row it never read.
    foreach (var claimed in new[] { ahead, behind })
    {
        Assert(claimed.Contains(UtcDay(2026, 6, 9)), "the UTC day the scan ran in is claimed");
        Assert(!claimed.Contains(UtcDay(2026, 6, 10)), "no day past the scan is ever claimed");
    }
}

/// <summary>
/// The persisted UiVibes flag is shared with the frozen WinForms app and survives an in-place
/// upgrade, so the WinUI shell - which implements none of the vibes appearance - must be able to
/// make it inert. Pure record logic, no registry: Load/Save touch HKCU and would rewrite the
/// developer's real settings.
/// </summary>
static void StaleVibesFlagIsInertWithoutVibes()
{
    // What a user who enabled vibes in the old shell and then picked Solid arrives with.
    var stale = new UiSettings { VibesEnabled = true, Material = BackdropMaterial.Solid, Theme = AppThemeMode.Light };

    // Honoured, by design, for the shell that DOES render vibes: the chosen material and the
    // chosen theme are both overridden. This is exactly what stranded the WinUI user.
    Assert(stale.EffectiveMaterial == BackdropMaterial.Acrylic, "vibes pins the backdrop to Acrylic");
    Assert(stale.ResolveIsDark(), "vibes forces dark over an explicit Light theme");

    var neutral = stale.WithoutVibes();

    Assert(!neutral.VibesEnabled, "WithoutVibes clears the flag");
    Assert(neutral.Material == stale.Material, "the user's material choice is preserved, not rewritten");
    Assert(neutral.EffectiveMaterial == BackdropMaterial.Solid, "the chosen material applies again");
    Assert(!neutral.ResolveIsDark(), "the explicit Light theme applies again");

    // Idempotent, and identity-preserving when there is nothing to clear: the shell calls this on
    // every load and on every settings change.
    AssertEqual(neutral.VibesEnabled, neutral.WithoutVibes().VibesEnabled, "WithoutVibes is idempotent");
    var clean = new UiSettings { Material = BackdropMaterial.Mica };
    Assert(ReferenceEquals(clean, clean.WithoutVibes()), "a settings record without vibes is returned unchanged");
    Assert(clean.WithoutVibes().EffectiveMaterial == BackdropMaterial.Mica, "a clean record keeps its material");

    // Every non-vibes field must survive the copy - this is what the settings window writes back.
    var carried = new UiSettings
    {
        VibesEnabled = true,
        Theme = AppThemeMode.Dark,
        Material = BackdropMaterial.MicaAlt,
        TintOpacityPercent = 12,
        CodexEnabled = false,
        ClaudeEnabled = true,
        CursorEnabled = false,
        OpenCodeGoEnabled = true,
        ChartColorOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["gpt-5.6"] = "#ABCDEF" }
    }.WithoutVibes();

    Assert(carried.Theme == AppThemeMode.Dark, "theme survives");
    Assert(carried.Material == BackdropMaterial.MicaAlt, "material survives");
    AssertEqual(12, carried.TintOpacityPercent, "tint survives");
    Assert(
        !carried.CodexEnabled && carried.ClaudeEnabled && !carried.CursorEnabled && carried.OpenCodeGoEnabled,
        "provider choices survive");
    AssertEqual(1, carried.ChartColorOverrides.Count, "chart colour overrides survive");
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


// ---------------------------------------------------------------- graphs period
//
// GraphsPeriod is compiled into this exe (see the csproj) rather than referenced, because
// CodexBar.WinUI cannot be referenced from a plain net10.0-windows test host. These exercise the
// exact source the window runs on.

static void GraphsPeriodBoundsEveryGranularity()
{
    var anchor = new DateOnly(2026, 7, 30); // a Thursday

    var (dayStart, dayEnd) = GraphsPeriod.Bounds(UsageLedgerGranularity.Day, anchor);
    AssertEqual(anchor, dayStart, "a day period starts on its anchor");
    AssertEqual(anchor, dayEnd, "a day period ends on its anchor");
    Assert(GraphsPeriod.BucketOf(UsageLedgerGranularity.Day) == UsageLedgerGranularity.Hour, "day view is HOURLY");

    // ISO Monday, matching UsageLedger.Floor - a strip that disagreed would place a column outside
    // its own period.
    var (weekStart, weekEnd) = GraphsPeriod.Bounds(UsageLedgerGranularity.Week, anchor);
    AssertEqual(new DateOnly(2026, 7, 27), weekStart, "weeks start on Monday");
    AssertEqual(new DateOnly(2026, 8, 2), weekEnd, "weeks end on Sunday");

    var (monthStart, monthEnd) = GraphsPeriod.Bounds(UsageLedgerGranularity.Month, anchor);
    AssertEqual(new DateOnly(2026, 7, 1), monthStart, "month start");
    AssertEqual(new DateOnly(2026, 7, 31), monthEnd, "month end");

    var (yearStart, yearEnd) = GraphsPeriod.Bounds(UsageLedgerGranularity.Year, anchor);
    AssertEqual(new DateOnly(2026, 1, 1), yearStart, "year start");
    AssertEqual(new DateOnly(2026, 12, 31), yearEnd, "year end");
    Assert(GraphsPeriod.BucketOf(UsageLedgerGranularity.Year) == UsageLedgerGranularity.Month, "year view is monthly");

    // The arrows step in whole periods and stay inside the period they land in.
    AssertEqual(new DateOnly(2025, 1, 1), GraphsPeriod.Shift(UsageLedgerGranularity.Year, anchor, -1), "the year arrow steps a whole year");
    AssertEqual(new DateOnly(2026, 6, 1), GraphsPeriod.Shift(UsageLedgerGranularity.Month, anchor, -1), "the month arrow steps a calendar month");
    AssertEqual(new DateOnly(2026, 7, 20), GraphsPeriod.Shift(UsageLedgerGranularity.Week, anchor, -1), "the week arrow steps to the previous Monday");
    AssertEqual(new DateOnly(2026, 7, 29), GraphsPeriod.Shift(UsageLedgerGranularity.Day, anchor, -1), "the day arrow steps a day");
}

static void GraphsPeriodCountsCurrentBucketFractionally()
{
    // Six days of a month, the sixth of them in progress at 06:00 - a quarter of a day.
    var buckets = DayBuckets(new DateTime(2026, 7, 1), 10);
    var now = new DateTimeOffset(new DateTime(2026, 7, 6, 6, 0, 0), TimeSpan.Zero);

    var elapsed = GraphsPeriod.Elapsed(buckets, now, null);
    Assert(elapsed.CurrentInProgress, "today is in progress");
    AssertEqual(6, elapsed.Buckets, "six whole days are named in prose");
    Assert(Math.Abs(elapsed.Fraction - 5.25) < 0.001, $"today counts as a quarter of a day, got {elapsed.Fraction}");

    // The point of the fraction: at 06:00 a whole-day count would divide by 6 and understate the
    // rate, and the projection built on it would be low by the same amount.
    Assert(elapsed.Fraction < 6, "a partial day must not count as a whole one");

    // A completed period has no in-progress bucket at all.
    var past = GraphsPeriod.Elapsed(buckets, new DateTimeOffset(new DateTime(2026, 8, 1), TimeSpan.Zero), null);
    Assert(!past.CurrentInProgress, "a finished period has nothing in progress");
    AssertEqual(10, past.Buckets, "every bucket of a finished period is elapsed");
    Assert(Math.Abs(past.Fraction - 10) < 0.001, "a finished period is exactly its bucket count");

    // A period that has barely begun is floored rather than allowed to blow the divisor up.
    var justStarted = GraphsPeriod.Elapsed(buckets, new DateTimeOffset(new DateTime(2026, 7, 1, 0, 1, 0), TimeSpan.Zero), null);
    Assert(justStarted.Fraction >= 0.25, "the elapsed fraction is floored");
    AssertEqual(1, justStarted.Buckets, "the in-progress bucket still counts as one in prose");
}

static void GraphsPeriodClampsElapsedToCoverageFloor()
{
    // Recording began on the 20th, so the month has 12 days of data and 31 days of calendar.
    var buckets = DayBuckets(new DateTime(2026, 7, 1), 31);
    var floor = new DateTimeOffset(new DateTime(2026, 7, 20), TimeSpan.Zero);
    var now = new DateTimeOffset(new DateTime(2026, 7, 31, 23, 0, 0), TimeSpan.Zero);

    var elapsed = GraphsPeriod.Elapsed(buckets, now, floor);
    AssertEqual(12, elapsed.Buckets, "only the days at or after the coverage floor are elapsed");
    Assert(elapsed.Fraction < 12 && elapsed.Fraction > 11.9, $"the last day is still partial, got {elapsed.Fraction}");
}

static void GraphsPeriodArrowsFollowGranularity()
{
    var anchor = new DateOnly(2026, 7, 30);

    // Nothing known about how far back the data goes must not disable navigation.
    Assert(GraphsPeriod.CanGoBack(UsageLedgerGranularity.Month, anchor, null), "an unknown floor leaves the arrow live");

    // The rule is the PREVIOUS period's end against the floor, one rule for all four granularities.
    Assert(GraphsPeriod.CanGoBack(UsageLedgerGranularity.Month, anchor, new DateOnly(2026, 6, 15)), "June is reachable from July when the floor is mid-June");
    Assert(!GraphsPeriod.CanGoBack(UsageLedgerGranularity.Month, anchor, new DateOnly(2026, 7, 1)), "nothing precedes the month the floor starts");

    // Year: live as soon as the floor falls in an earlier year, dead when it does not. This is the
    // case that kept Year hidden.
    Assert(!GraphsPeriod.CanGoBack(UsageLedgerGranularity.Year, anchor, new DateOnly(2026, 3, 1)), "2025 is unreachable when history starts in March 2026");
    Assert(GraphsPeriod.CanGoBack(UsageLedgerGranularity.Year, anchor, new DateOnly(2025, 12, 31)), "2025 is reachable when history reaches its last day");

    Assert(GraphsPeriod.CanGoBack(UsageLedgerGranularity.Day, anchor, new DateOnly(2026, 7, 29)), "yesterday is reachable when the floor is yesterday");
    Assert(!GraphsPeriod.CanGoBack(UsageLedgerGranularity.Day, anchor, anchor), "no day precedes the floor day");

    Assert(GraphsPeriod.IsCurrent(UsageLedgerGranularity.Month, anchor, new DateOnly(2026, 7, 1)), "July contains 1 July");
    Assert(!GraphsPeriod.IsCurrent(UsageLedgerGranularity.Month, anchor, new DateOnly(2026, 8, 1)), "July does not contain 1 August");
}

static void GraphsPeriodDrillsOneLevelFiner()
{
    Assert(GraphsPeriod.Finer(UsageLedgerGranularity.Year) == UsageLedgerGranularity.Month, "a year drills to a month");
    Assert(GraphsPeriod.Finer(UsageLedgerGranularity.Month) == UsageLedgerGranularity.Day, "a month drills to a day");
    Assert(GraphsPeriod.Finer(UsageLedgerGranularity.Week) == UsageLedgerGranularity.Day, "a week drills to a day");

    Assert(GraphsPeriod.CanStepFiner(UsageLedgerGranularity.Year), "year can drill");
    Assert(GraphsPeriod.CanStepFiner(UsageLedgerGranularity.Week), "week can drill");
    Assert(!GraphsPeriod.CanStepFiner(UsageLedgerGranularity.Day), "day is the floor - there is no hour PERIOD");
}

static void GraphsPeriodNamesWholeDayColumnWithoutHour()
{
    var at = new DateTime(2026, 7, 30, 15, 0, 0);

    // A real hourly column names its hour...
    Assert(
        GraphsPeriod.BucketLabel(UsageLedgerGranularity.Day, at, TimeSpan.FromHours(1)).Contains("3 PM", StringComparison.Ordinal),
        "an hourly column is named by its hour");

    // ...but the single column the SCAN can answer with covers the whole day, and calling it "12 AM"
    // would claim an hour the data never had.
    var wholeDay = GraphsPeriod.BucketLabel(UsageLedgerGranularity.Day, at.Date, TimeSpan.FromDays(1));
    Assert(
        !wholeDay.Contains("AM", StringComparison.Ordinal) && !wholeDay.Contains("PM", StringComparison.Ordinal),
        $"a whole-day column must not name an hour, got \"{wholeDay}\"");
}

/// <summary>
/// The other half of the half-hour-offset regression: a label that names a column has to be DRAWN
/// on it. LiveCharts places its own separators at absolute multiples of the step, which is the
/// whole clock hour - the seam between two columns of a grid that starts at :30.
/// </summary>
static void GraphsPeriodPutsDayAxisLabelsOnColumns()
{
    // A whole-hour zone is already on the grid: nothing is overridden, and the chart keeps placing
    // its own labels exactly as it did before.
    var onGrid = new DateTime[24];
    for (var index = 0; index < onGrid.Length; index++)
    {
        onGrid[index] = new DateTime(2026, 5, 11, 0, 0, 0).AddHours(index);
    }

    Assert(
        GraphsPeriod.DayAxisLabelTicks(UsageLedgerGranularity.Day, onGrid) is null,
        "a whole-hour zone must keep the automatic separators");
    Assert(
        GraphsPeriod.DayAxisLabelTicks(UsageLedgerGranularity.Month, onGrid) is null,
        "only the Day axis draws hour columns");

    // +05:30 puts the columns on the UTC grid, so they start at :30 and the labels have to follow.
    var offGrid = new DateTime[24];
    for (var index = 0; index < offGrid.Length; index++)
    {
        offGrid[index] = new DateTime(2026, 5, 11, 0, 30, 0).AddHours(index);
    }

    var ticks = GraphsPeriod.DayAxisLabelTicks(UsageLedgerGranularity.Day, offGrid);
    Assert(ticks is not null, "a half-hour offset must place the labels explicitly");
    AssertEqual(8, ticks!.Length, "every third column of 24 carries a label");

    for (var index = 0; index < ticks.Length; index++)
    {
        var at = new DateTime((long)ticks[index]);
        AssertEqual(
            offGrid[index * GraphsPeriod.DayAxisLabelEvery].Ticks,
            at.Ticks,
            "a label must sit on a column's start, not between two of them");
        AssertEqual("h:mm tt", GraphsPeriod.HourPattern(at), "and must then be written with its minutes");
    }

    // Lord Howe Island shifts by THIRTY minutes across DST, so a day can start on the hour and go
    // off the grid halfway through - the check is every column, not the first.
    var shifts = new[]
    {
        new DateTime(2026, 4, 5, 0, 0, 0),
        new DateTime(2026, 4, 5, 1, 0, 0),
        new DateTime(2026, 4, 5, 1, 30, 0),
        new DateTime(2026, 4, 5, 2, 30, 0)
    };

    Assert(
        GraphsPeriod.DayAxisLabelTicks(UsageLedgerGranularity.Day, shifts) is not null,
        "a grid that goes off the hour mid-day must place its labels explicitly");
}

/// <summary>Dense day buckets, the shape UsageLedger.Query returns them in.</summary>
static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset EndExclusive)> DayBuckets(DateTime start, int count)
{
    var buckets = new List<(DateTimeOffset, DateTimeOffset)>(count);
    for (var index = 0; index < count; index++)
    {
        buckets.Add((
            new DateTimeOffset(start.AddDays(index), TimeSpan.Zero),
            new DateTimeOffset(start.AddDays(index + 1), TimeSpan.Zero)));
    }

    return buckets;
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

    /// <summary>
    /// Reads the fixture corpus AND merges into the ledger, synchronously. Only usable under a
    /// <see cref="UsageLedgerFixture"/> - the factory refuses otherwise - because what it exercises
    /// is the scan's authority to DELETE days, which against the real root would delete real months.
    /// </summary>
    public ProviderUsageInsightsLookupResult ReadWithLedger()
    {
        return CodexUsageInsightsReader.CreateLedgerWritingReaderForTests(Path.Combine(root, "codex")).ReadLatest();
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
    {
        File.WriteAllText(ShardPath(scope, year), payload);

        // Written BEHIND the store's back, so the store's own identity check (length + last write
        // time) cannot be relied on: successive payloads of equal length inside one file-time tick
        // are indistinguishable to it. A hand-edited shard is not a case production has to survive
        // at millisecond resolution, so the test says so explicitly instead of weakening the check.
        UsageLedger.InvalidateShardCache();
    }

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

sealed class GrokFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "codexbar-grok-tests", Guid.NewGuid().ToString("N"));

    public void WriteSessionLog(string sessionId, params string[] lines)
    {
        var dir = Path.Combine(root, "workspace", sessionId);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "updates.jsonl");
        File.WriteAllLines(file, lines);
        File.SetLastWriteTime(file, DateTime.Now);
    }

    public ProviderUsageInsightsLookupResult Read()
    {
        return new GrokUsageInsightsReader([root], refreshModelsDevPricing: false).ReadLatest();
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
