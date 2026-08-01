using System.Collections.Concurrent;

namespace CodexBarWindows;

/// <summary>State of one Codex account's banked-reset row, owned by the service.</summary>
/// <param name="Busy">A redemption is in flight; the row must stay visible and inert.</param>
/// <param name="Message">Outcome text to show instead of the inventory description.</param>
/// <param name="ClearOnNextSnapshot">
/// True when refreshed usage tells the story better than <paramref name="Message"/> does - the
/// case after a reset actually lands, where the new numbers ARE the report.
/// </param>
public sealed record ResetCreditState(bool Busy, string? Message, bool ClearOnNextSnapshot)
{
    public static readonly ResetCreditState Idle = new(false, null, false);

    public bool HasSomethingToSay => Busy || Message is not null;
}

/// <summary>
/// UI-agnostic port of the WinForms tray context's refresh orchestration: it owns the provider
/// readers, the latest results per provider, the stale-data retention, the visibility-gated
/// poll timer and the banked-reset redemption flow. It raises events; it never touches a widget.
/// </summary>
/// <remarks>
/// <para>
/// THE VISIBILITY GATE IS LOAD-BEARING. Usage is only recalculated while a window is showing
/// it: nothing polls in the background, because a background poll on this app means spawning
/// the Codex app-server and hitting cursor.com on a timer for numbers nobody is looking at.
/// Callers report window visibility through <see cref="SetWindowOpen"/> and the timer follows.
/// </para>
/// <para>
/// Every mutation of the cached results happens on the UI thread via the <c>post</c> callback
/// handed to the constructor, so the dictionaries below need no locking and events always
/// arrive somewhere a UI can act on them directly.
/// </para>
/// </remarks>
public sealed class UsageRefreshService : IDisposable
{
    /// <summary>Poll interval while at least one window is open.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Floor between two USER-REQUESTED refreshes. A click-storm on the refresh button (or a
    /// leaned-on F5) must not stack refreshes: each one spawns a Codex app-server process and
    /// two network calls, and the results would land out of order.
    /// </summary>
    public static readonly TimeSpan ManualRefreshDebounce = TimeSpan.FromSeconds(2);

    private readonly Action<Action> post;
    private readonly ClaudeUsageReader claudeUsageReader = new();
    private readonly GrokUsageReader grokUsageReader = new();
    private readonly CursorUsageReader cursorUsageReader = new();
    private readonly OpenCodeGoUsageReader openCodeGoUsageReader = new();
    private readonly Dictionary<string, ProviderUsageLookupResult> latestCodexUsage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderUsageInsightsLookupResult> latestHistory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CodexRateLimitStabilizer> codexStabilizers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResetCreditState> resetCreditStates = new(StringComparer.Ordinal);
    private readonly HashSet<string> openWindows = new(StringComparer.Ordinal);
    private readonly Timer refreshTimer;

    private ProviderUsageLookupResult latestClaudeUsage = NotLoaded;
    private ProviderUsageLookupResult latestGrokUsage = NotLoaded;
    private ProviderUsageLookupResult latestCursorUsage = NotLoaded;
    private ProviderUsageLookupResult latestOpenCodeGoUsage = NotLoaded;
    private CancellationTokenSource? refreshCancellation;
    private int refreshGeneration;
    private int inFlightRefreshes;
    private bool timerRunning;
    private DateTime lastManualRefreshUtc = DateTime.MinValue;
    private bool disposed;

    private static ProviderUsageLookupResult NotLoaded => new(null, "Usage has not been loaded yet.");

    private static ProviderUsageInsightsLookupResult HistoryNotLoaded =>
        new(null, "Usage history has not been loaded yet.");

    /// <param name="post">
    /// Marshals an action onto the UI thread. Every event this service raises, and every write
    /// to its caches, goes through it.
    /// </param>
    public UsageRefreshService(Action<Action> post)
    {
        this.post = post;
        CodexEntries = CodexCliSettings.Load();
        foreach (var entry in CodexEntries)
        {
            var providerKey = ProviderKeys.Codex(entry.Id);
            latestCodexUsage[providerKey] = NotLoaded;
            latestHistory[providerKey] = HistoryNotLoaded;
            codexStabilizers[providerKey] = new CodexRateLimitStabilizer();
        }

        latestHistory[ProviderKeys.Claude] = HistoryNotLoaded;
        latestHistory[ProviderKeys.Grok] = HistoryNotLoaded;

        // Created stopped. Start/Stop is driven purely by SetWindowOpen.
        refreshTimer = new Timer(_ => post(() => BeginRefresh()), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<CodexCliEntry> CodexEntries { get; private set; }

    /// <summary>Whether at least one refresh is currently running.</summary>
    public bool IsRefreshing => Volatile.Read(ref inFlightRefreshes) > 0;

    /// <summary>
    /// Whether the 30-day history scan runs as part of a refresh. It parses every local session
    /// log, so it is skipped unless a window is actually plotting it.
    /// </summary>
    public bool IncludeHistory { get; set; }

    /// <summary>Raised on the UI thread whenever one provider's cached usage result changes.</summary>
    public event Action<string, ProviderUsageLookupResult>? UsageUpdated;

    /// <summary>Raised on the UI thread whenever one provider's cached history changes.</summary>
    public event Action<string, ProviderUsageInsightsLookupResult>? HistoryUpdated;

    /// <summary>Raised on the UI thread with a fresh tray tooltip after any usage change.</summary>
    public event Action<string>? TooltipChanged;

    /// <summary>Raised on the UI thread when <see cref="IsRefreshing"/> flips.</summary>
    public event Action<bool>? RefreshingChanged;

    /// <summary>Raised on the UI thread when a Codex account's banked-reset row state changes.</summary>
    public event Action<string, ResetCreditState>? ResetCreditStateChanged;

    /// <summary>Raised on the UI thread after the configured Codex accounts are re-read.</summary>
    public event Action? CodexEntriesChanged;

    public ProviderUsageLookupResult GetUsage(string providerKey)
    {
        if (providerKey == ProviderKeys.Claude)
        {
            return latestClaudeUsage;
        }

        if (providerKey == ProviderKeys.Grok)
        {
            return latestGrokUsage;
        }

        if (providerKey == ProviderKeys.Cursor)
        {
            return latestCursorUsage;
        }

        if (providerKey == ProviderKeys.OpenCodeGo)
        {
            return latestOpenCodeGoUsage;
        }

        return latestCodexUsage.TryGetValue(providerKey, out var result) ? result : NotLoaded;
    }

    public ProviderUsageInsightsLookupResult GetHistory(string providerKey) =>
        latestHistory.TryGetValue(providerKey, out var result) ? result : HistoryNotLoaded;

    public ResetCreditState GetResetCreditState(string providerKey) =>
        resetCreditStates.TryGetValue(providerKey, out var state) ? state : ResetCreditState.Idle;

    public string BuildTooltip() =>
        UsageTooltip.Build(
            CodexEntries,
            latestCodexUsage,
            latestClaudeUsage,
            latestGrokUsage,
            latestCursorUsage,
            latestOpenCodeGoUsage,
            UiSettings.Load());

    /// <summary>
    /// Reports whether a usage-showing window is open. The poll timer runs while any is, and
    /// stops when the last one closes - see the class remarks: this is not an optimisation,
    /// it is the app's contract.
    /// </summary>
    public void SetWindowOpen(string windowId, bool open)
    {
        if (open)
        {
            openWindows.Add(windowId);
        }
        else
        {
            openWindows.Remove(windowId);
        }

        var shouldRun = openWindows.Count > 0 && !disposed;
        if (shouldRun == timerRunning)
        {
            return;
        }

        timerRunning = shouldRun;
        refreshTimer.Change(
            shouldRun ? RefreshInterval : Timeout.InfiniteTimeSpan,
            shouldRun ? RefreshInterval : Timeout.InfiniteTimeSpan);
    }

    /// <summary>Whether the poll timer is currently armed. Exposed so the gate can be tested.</summary>
    public bool IsPolling => timerRunning;

    /// <summary>
    /// Abandons the refresh that is currently running, but only once NOTHING is showing usage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart to <see cref="SetWindowOpen"/> for work already in flight. Closing the
    /// graphs window stops the next scan from being scheduled, but the one it started keeps
    /// running - and the 30-day session-log scan is by far the most expensive thing this app ever
    /// does, so leaving it grinding for a window that no longer exists is exactly the idle cost
    /// the visibility gate exists to prevent.
    /// </para>
    /// <para>
    /// The open-window check is what makes this safe to call from any window's teardown: with the
    /// flyout still up this is a no-op, so closing the graphs window can never yank the numbers
    /// out from under it.
    /// </para>
    /// <para>
    /// It cancels what is CANCELLABLE - the provider HTTP calls and the Codex app-server read all
    /// take the token, and anything not yet started is dropped. A scan already inside
    /// <c>CodexUsageInsightsReader.ReadLatest</c> runs to completion (it is synchronous file
    /// parsing with no token to check); the cancelled token still ensures its result is discarded
    /// rather than published, through <c>PostIfCurrent</c>.
    /// </para>
    /// </remarks>
    public void CancelRefreshIfUnwatched()
    {
        if (disposed || openWindows.Count > 0)
        {
            return;
        }

        refreshCancellation?.Cancel();
    }

    /// <summary>
    /// A user-initiated refresh (button or F5). Returns false when it was swallowed - either a
    /// refresh is already running or the debounce window has not elapsed - so the caller can
    /// leave the UI alone rather than flashing a loading state that never resolves.
    /// </summary>
    public bool RequestManualRefresh()
    {
        if (disposed || IsRefreshing)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - lastManualRefreshUtc < ManualRefreshDebounce)
        {
            return false;
        }

        lastManualRefreshUtc = now;
        BeginRefresh();
        return true;
    }

    /// <summary>An unconditional refresh: opening a window, a settings change, a poll tick.</summary>
    public void Refresh() => BeginRefresh();

    /// <summary>
    /// Re-reads the configured Codex CLI accounts, keeping cached usage for accounts whose
    /// binary did not move and dropping state for accounts that disappeared.
    /// </summary>
    public void ReloadCodexEntries()
    {
        var previousPaths = CodexEntries.ToDictionary(
            entry => ProviderKeys.Codex(entry.Id),
            entry => entry.BinaryPath ?? string.Empty,
            StringComparer.Ordinal);

        CodexEntries = CodexCliSettings.Load();
        var activeProviderKeys = CodexEntries
            .Select(entry => ProviderKeys.Codex(entry.Id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in CodexEntries)
        {
            var providerKey = ProviderKeys.Codex(entry.Id);
            var binaryPath = entry.BinaryPath ?? string.Empty;
            var binaryChanged = previousPaths.TryGetValue(providerKey, out var previousPath) &&
                !string.Equals(previousPath, binaryPath, StringComparison.OrdinalIgnoreCase);
            if (binaryChanged)
            {
                // A different binary is a different account: its numbers, and the stabilizer's
                // idea of what a plausible transition looks like, no longer apply.
                latestCodexUsage[providerKey] = NotLoaded;
                codexStabilizers[providerKey] = new CodexRateLimitStabilizer();
            }
            else
            {
                latestCodexUsage.TryAdd(providerKey, NotLoaded);
                codexStabilizers.GetOrAdd(providerKey, _ => new CodexRateLimitStabilizer());
            }

            latestHistory.TryAdd(providerKey, HistoryNotLoaded);
        }

        foreach (var providerKey in codexStabilizers.Keys.Where(key => !activeProviderKeys.Contains(key)).ToArray())
        {
            codexStabilizers.TryRemove(providerKey, out _);
            latestCodexUsage.Remove(providerKey);
            latestHistory.Remove(providerKey);
            resetCreditStates.Remove(providerKey);
        }

        CodexEntriesChanged?.Invoke();
        foreach (var entry in CodexEntries)
        {
            var providerKey = ProviderKeys.Codex(entry.Id);
            UsageUpdated?.Invoke(providerKey, GetUsage(providerKey));
        }

        UsageUpdated?.Invoke(ProviderKeys.Claude, latestClaudeUsage);
        UsageUpdated?.Invoke(ProviderKeys.Grok, latestGrokUsage);
        UsageUpdated?.Invoke(ProviderKeys.Cursor, latestCursorUsage);
        UsageUpdated?.Invoke(ProviderKeys.OpenCodeGo, latestOpenCodeGoUsage);
        BeginRefresh();
    }

    /// <summary>
    /// Spends one banked reset credit on the Codex account that owns <paramref name="request"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not tied to <see cref="refreshCancellation"/>: a consume that has reached
    /// the backend cannot be recalled, so cancelling it would only lose the outcome, not the
    /// charge. The credit id is always sent explicitly and the redeemer runs THIS entry's own
    /// binary, so a routing mistake fails loudly instead of charging another account.
    /// </remarks>
    public void RedeemResetCredit(CodexResetRedeemRequest request)
    {
        var entry = CodexEntries.FirstOrDefault(
            candidate => ProviderKeys.Codex(candidate.Id) == request.ProviderKey);
        if (entry is null)
        {
            SetResetCreditState(request.ProviderKey, new ResetCreditState(false, "That Codex account is no longer configured.", false));
            return;
        }

        if (GetResetCreditState(request.ProviderKey).Busy)
        {
            return;
        }

        SetResetCreditState(request.ProviderKey, new ResetCreditState(true, null, false));

        var redeemer = new CodexResetCreditRedeemer(entry.Id, entry.BinaryPath);
        var creditId = request.Credit.Id;

        _ = Task.Run(() => redeemer.Redeem(creditId)).ContinueWith(
            task =>
            {
                var result = task.IsFaulted
                    ? new CodexResetRedeemResult(CodexResetOutcome.Failed, task.Exception?.GetBaseException().Message)
                    : task.Result;

                post(() =>
                {
                    if (disposed)
                    {
                        return;
                    }

                    SetResetCreditState(
                        request.ProviderKey,
                        new ResetCreditState(
                            false,
                            CodexResetCreditRedeemer.DescribeOutcome(result),
                            result.ChangedUsage));

                    if (result.ChangedUsage &&
                        codexStabilizers.TryGetValue(request.ProviderKey, out var stabilizer))
                    {
                        // Usage drops and the reset time moves, which the stabilizer reads as a
                        // conflict; without this the pre-reset numbers would stick.
                        stabilizer.InvalidateAcceptedSnapshot();
                    }

                    BeginRefresh();
                });
            },
            TaskScheduler.Default);
    }

    /// <summary>Clears a finished redemption's note, e.g. when the flyout closes.</summary>
    public void ClearResetCreditMessages()
    {
        foreach (var providerKey in resetCreditStates.Keys.ToArray())
        {
            if (!resetCreditStates[providerKey].Busy)
            {
                SetResetCreditState(providerKey, ResetCreditState.Idle);
            }
        }
    }

    private void SetResetCreditState(string providerKey, ResetCreditState state)
    {
        if (state == ResetCreditState.Idle)
        {
            resetCreditStates.Remove(providerKey);
        }
        else
        {
            resetCreditStates[providerKey] = state;
        }

        ResetCreditStateChanged?.Invoke(providerKey, state);
    }

    private void BeginRefresh()
    {
        if (disposed)
        {
            return;
        }

        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();

        var includeHistory = IncludeHistory;
        var generation = Interlocked.Increment(ref refreshGeneration);
        var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;

        SetRefreshing(delta: 1);

        _ = Task.Run(async () =>
        {
            void PostIfCurrent(Action update)
            {
                post(() =>
                {
                    if (disposed || cancellation.IsCancellationRequested || generation != refreshGeneration)
                    {
                        return;
                    }

                    update();
                });
            }

            void PublishUsage(string providerKey, ProviderUsageLookupResult merged)
            {
                UsageUpdated?.Invoke(providerKey, merged);
                TooltipChanged?.Invoke(BuildTooltip());
            }

            async Task RefreshCodexLimitsAsync()
            {
                var entries = CodexEntries;
                var codexTasks = entries
                    .Select(entry => Task.Run(
                        () =>
                        {
                            var providerKey = ProviderKeys.Codex(entry.Id);
                            try
                            {
                                var stabilizer = codexStabilizers.GetOrAdd(
                                    providerKey,
                                    _ => new CodexRateLimitStabilizer());
                                var reader = new CodexUsageReader(entry.BinaryPath, stabilizer);
                                return new KeyValuePair<string, ProviderUsageLookupResult>(
                                    providerKey,
                                    reader.ReadLatest(cancellation.Token).ToProviderResult());
                            }
                            catch (Exception exception)
                            {
                                return new KeyValuePair<string, ProviderUsageLookupResult>(
                                    providerKey,
                                    new ProviderUsageLookupResult(
                                        null,
                                        $"Could not refresh {entry.Name} limits: {exception.Message}"));
                            }
                        },
                        cancellation.Token))
                    .ToArray();

                // Grouped rather than ToDictionary: a duplicated Codex account id would throw
                // here and take the whole refresh down through the outer catch.
                var codexResults = (await Task.WhenAll(codexTasks).ConfigureAwait(false))
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

                PostIfCurrent(() =>
                {
                    foreach (var pair in codexResults)
                    {
                        var merged = ProviderUsageLookupResult.KeepLastGood(
                            latestCodexUsage.TryGetValue(pair.Key, out var previous) ? previous : null,
                            pair.Value);
                        latestCodexUsage[pair.Key] = merged;

                        // The post-reset numbers have landed; the row's own inventory is the report now.
                        if (merged.HasSnapshot &&
                            resetCreditStates.TryGetValue(pair.Key, out var creditState) &&
                            creditState.ClearOnNextSnapshot)
                        {
                            SetResetCreditState(pair.Key, ResetCreditState.Idle);
                        }

                        PublishUsage(pair.Key, merged);
                    }
                });
            }

            async Task RefreshCodexHistoryAsync()
            {
                var codexHistory = await Task.Run(() => new CodexUsageInsightsReader().ReadLatest(), cancellation.Token)
                    .ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    foreach (var entry in CodexEntries)
                    {
                        var providerKey = ProviderKeys.Codex(entry.Id);
                        var merged = ProviderUsageInsightsLookupResult.KeepLastGood(
                            latestHistory.TryGetValue(providerKey, out var previous) ? previous : null,
                            codexHistory);
                        latestHistory[providerKey] = merged;
                        HistoryUpdated?.Invoke(providerKey, merged);
                    }
                });
            }

            async Task RefreshClaudeLimitsAsync()
            {
                var claudeResult = await claudeUsageReader.ReadLatestAsync(cancellation.Token).ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestClaudeUsage = ProviderUsageLookupResult.KeepLastGood(latestClaudeUsage, claudeResult);
                    PublishUsage(ProviderKeys.Claude, latestClaudeUsage);
                });
            }

            async Task RefreshClaudeHistoryAsync()
            {
                var claudeHistory = await Task.Run(() => new ClaudeUsageInsightsReader().ReadLatest(), cancellation.Token)
                    .ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    var merged = ProviderUsageInsightsLookupResult.KeepLastGood(
                        latestHistory.TryGetValue(ProviderKeys.Claude, out var previous) ? previous : null,
                        claudeHistory);
                    latestHistory[ProviderKeys.Claude] = merged;
                    HistoryUpdated?.Invoke(ProviderKeys.Claude, merged);
                });
            }

            async Task RefreshGrokLimitsAsync()
            {
                var grokResult = await grokUsageReader.ReadLatestAsync(cancellation.Token).ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestGrokUsage = ProviderUsageLookupResult.KeepLastGood(latestGrokUsage, grokResult);
                    PublishUsage(ProviderKeys.Grok, latestGrokUsage);
                });
            }

            async Task RefreshGrokHistoryAsync()
            {
                var grokHistory = await Task.Run(() => new GrokUsageInsightsReader().ReadLatest(), cancellation.Token)
                    .ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    var merged = ProviderUsageInsightsLookupResult.KeepLastGood(
                        latestHistory.TryGetValue(ProviderKeys.Grok, out var previous) ? previous : null,
                        grokHistory);
                    latestHistory[ProviderKeys.Grok] = merged;
                    HistoryUpdated?.Invoke(ProviderKeys.Grok, merged);
                });
            }

            async Task RefreshCursorLimitsAsync()
            {
                var cursorResult = await cursorUsageReader.ReadLatestAsync(cancellation.Token).ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestCursorUsage = ProviderUsageLookupResult.KeepLastGood(latestCursorUsage, cursorResult);
                    PublishUsage(ProviderKeys.Cursor, latestCursorUsage);
                });
            }

            async Task RefreshOpenCodeGoLimitsAsync()
            {
                var openCodeGoResult = await openCodeGoUsageReader.ReadLatestAsync(cancellation.Token)
                    .ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestOpenCodeGoUsage = ProviderUsageLookupResult.KeepLastGood(
                        latestOpenCodeGoUsage,
                        openCodeGoResult);
                    PublishUsage(ProviderKeys.OpenCodeGo, latestOpenCodeGoUsage);
                });
            }

            // A disabled tool is not polled at all - that is the point of the setting, and it
            // also removes its share of the refresh cost (notably the Codex app-server spawn).
            var settings = UiSettings.Load();
            var refreshTasks = new List<Task>();
            if (settings.CodexEnabled)
            {
                refreshTasks.Add(RefreshCodexLimitsAsync());
            }

            if (settings.ClaudeEnabled)
            {
                refreshTasks.Add(RefreshClaudeLimitsAsync());
            }

            if (settings.GrokEnabled)
            {
                refreshTasks.Add(RefreshGrokLimitsAsync());
            }

            if (settings.CursorEnabled)
            {
                refreshTasks.Add(RefreshCursorLimitsAsync());
            }

            if (settings.OpenCodeGoEnabled)
            {
                refreshTasks.Add(RefreshOpenCodeGoLimitsAsync());
            }

            if (includeHistory)
            {
                if (settings.CodexEnabled)
                {
                    refreshTasks.Add(RefreshCodexHistoryAsync());
                }

                if (settings.ClaudeEnabled)
                {
                    refreshTasks.Add(RefreshClaudeHistoryAsync());
                }

                if (settings.GrokEnabled)
                {
                    refreshTasks.Add(RefreshGrokHistoryAsync());
                }
            }

            try
            {
                await Task.WhenAll(refreshTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                // Every provider refresh reports its own failure as a result, so this only
                // catches an orchestration fault. It must NOT overwrite every provider with the
                // same error: one provider failing would blank the others' good data and make
                // the retention above pointless. Annotate without discarding.
                PostIfCurrent(() =>
                {
                    var message = $"Could not refresh usage limits: {exception.Message}";
                    foreach (var entry in CodexEntries)
                    {
                        var providerKey = ProviderKeys.Codex(entry.Id);
                        var previous = latestCodexUsage.TryGetValue(providerKey, out var existing) ? existing : null;
                        var annotated = ProviderUsageLookupResult.KeepLastGood(
                            previous,
                            new ProviderUsageLookupResult(null, message));
                        latestCodexUsage[providerKey] = annotated;
                        PublishUsage(providerKey, annotated);
                    }

                    latestClaudeUsage = ProviderUsageLookupResult.KeepLastGood(
                        latestClaudeUsage,
                        new ProviderUsageLookupResult(null, message));
                    PublishUsage(ProviderKeys.Claude, latestClaudeUsage);

                    latestGrokUsage = ProviderUsageLookupResult.KeepLastGood(
                        latestGrokUsage,
                        new ProviderUsageLookupResult(null, message));
                    PublishUsage(ProviderKeys.Grok, latestGrokUsage);

                    latestCursorUsage = ProviderUsageLookupResult.KeepLastGood(
                        latestCursorUsage,
                        new ProviderUsageLookupResult(null, message));
                    PublishUsage(ProviderKeys.Cursor, latestCursorUsage);

                    latestOpenCodeGoUsage = ProviderUsageLookupResult.KeepLastGood(
                        latestOpenCodeGoUsage,
                        new ProviderUsageLookupResult(null, message));
                    PublishUsage(ProviderKeys.OpenCodeGo, latestOpenCodeGoUsage);
                });
            }
            finally
            {
                post(() =>
                {
                    if (ReferenceEquals(refreshCancellation, cancellation))
                    {
                        refreshCancellation = null;
                    }

                    cancellation.Dispose();
                    SetRefreshing(delta: -1);
                });
            }
        }, CancellationToken.None);
    }

    private void SetRefreshing(int delta)
    {
        var before = Volatile.Read(ref inFlightRefreshes) > 0;
        var after = Interlocked.Add(ref inFlightRefreshes, delta) > 0;
        if (before != after)
        {
            RefreshingChanged?.Invoke(after);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timerRunning = false;
        refreshTimer.Dispose();
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = null;
    }
}
