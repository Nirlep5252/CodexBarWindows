namespace CodexBarWindows;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ClaudeUsageReader claudeUsageReader = new();
    private readonly GrokUsageReader grokUsageReader = new();
    private readonly CursorUsageReader cursorUsageReader = new();
    private readonly OpenCodeGoUsageReader openCodeGoUsageReader = new();
    private readonly GitHubReleaseUpdater releaseUpdater = new();
    private Icon trayIcon = TrayIconFactory.Create();
    // Program.Main primes FluentTheme.VibesActive before this context is built, so the tray
    // icon and this flag start in agreement.
    private bool trayIconVibes = FluentTheme.VibesActive;
    private readonly NotifyIcon notifyIcon;
    private readonly UsagePopupForm popup = new();
    private SettingsForm? settingsForm;
    private UsageGraphsForm? graphsForm;
    private readonly System.Windows.Forms.Timer refreshTimer = new();
    private readonly System.Windows.Forms.Timer updateTimer = new();
    private readonly SynchronizationContext uiContext;
    private ToolStripMenuItem? checkForUpdatesItem;
    private IReadOnlyList<CodexCliEntry> codexCliEntries;
    private readonly Dictionary<string, ProviderUsageLookupResult> latestCodexUsage = [];
    private readonly Dictionary<string, ProviderUsageInsightsLookupResult> latestHistory = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CodexRateLimitStabilizer> codexStabilizers = [];
    private ProviderUsageLookupResult latestClaudeUsage = new(null, "Usage has not been loaded yet.");
    private ProviderUsageLookupResult latestGrokUsage = new(null, "Usage has not been loaded yet.");
    private ProviderUsageLookupResult latestCursorUsage = new(null, "Usage has not been loaded yet.");
    private ProviderUsageLookupResult latestOpenCodeGoUsage = new(null, "Usage has not been loaded yet.");
    private CancellationTokenSource? refreshCancellation;
    private int refreshGeneration;
    private int updateCheckInProgress;
    private bool disposed;

    public TrayApplicationContext()
    {
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        codexCliEntries = CodexCliSettings.Load();
        foreach (var entry in codexCliEntries)
        {
            var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
            latestCodexUsage[providerKey] = new ProviderUsageLookupResult(null, "Usage has not been loaded yet.");
            latestHistory[providerKey] = new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet.");
            codexStabilizers[providerKey] = new CodexRateLimitStabilizer();
        }

        latestHistory[UsagePopupForm.ClaudeProviderKey] = new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet.");
        latestHistory[UsagePopupForm.GrokProviderKey] = new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet.");
        popup.ConfigureCodexEntries(codexCliEntries);
        notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = CreateContextMenu(),
            Icon = trayIcon,
            Text = "Codex rate limits",
            Visible = true
        };
        notifyIcon.MouseUp += OnTrayMouseUp;
        popup.SelectedProviderChanged += (_, providerKey) =>
        {
            if (!GetLatestUsage(providerKey).HasSnapshot)
            {
                BeginRefresh(showLoading: true);
            }
        };
        popup.UsageGraphsRequested += (_, _) => ShowUsageGraphs();
        popup.SettingsRequested += (_, _) => ShowSettings();
        popup.ResetCreditRedeemRequested += (_, request) => BeginResetCreditRedeem(request);
        popup.VisibleChanged += (_, _) => UpdateRefreshTimerState();

        // Usage is only recalculated while a window is showing it: the timer runs while the
        // popup or graphs window is open and stops when both close. Nothing refreshes in the
        // background; opening the popup or graphs triggers the first refresh.
        refreshTimer.Interval = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
        refreshTimer.Tick += (_, _) => BeginRefresh(showLoading: false);

        updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
        updateTimer.Tick += (_, _) => BeginUpdateCheck();
        updateTimer.Start();

        UiSettings.Changed += OnUiSettingsChanged;

        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => BeginUpdateCheck(), TaskScheduler.Default);
    }

    private void OnUiSettingsChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        // The tray glyph is vibe-gradient-tinted while vibes are on; rebuild it so toggling
        // the setting restyles the taskbar too. TrayIconFactory.Create() varies only with
        // FluentTheme.VibesActive (the system light/dark theme never raises this event), so
        // rebuilding on every broadcast meant a PNG reload, a 64px render and a 4096-pixel
        // recolour loop on each tick of a tint-slider drag. Only vibes changes matter.
        var vibes = FluentTheme.VibesActive;
        if (vibes == trayIconVibes)
        {
            return;
        }

        trayIconVibes = vibes;
        var refreshed = TrayIconFactory.Create();
        notifyIcon.Icon = refreshed;
        trayIcon.Dispose();
        trayIcon = refreshed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            disposed = true;
            UiSettings.Changed -= OnUiSettingsChanged;
            refreshCancellation?.Cancel();
            refreshCancellation?.Dispose();
            refreshTimer.Dispose();
            updateTimer.Dispose();
            settingsForm?.Dispose();
            graphsForm?.Dispose();
            popup.Dispose();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        var graphsItem = new ToolStripMenuItem("Usage graphs");
        graphsItem.Click += (_, _) => ShowUsageGraphs();

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => ShowSettings();

        checkForUpdatesItem = new ToolStripMenuItem("Check for updates");
        checkForUpdatesItem.Click += (_, _) => BeginUpdateCheck(showResult: true);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();

        menu.Items.Add(graphsItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(checkForUpdatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    public void NotifyAlreadyRunning()
    {
        uiContext.Post(_ =>
        {
            if (disposed)
            {
                return;
            }

            notifyIcon.ShowBalloonTip(
                4000,
                AppInfo.AppName,
                $"{AppInfo.AppName} is already running. Use the tray icon to view usage.",
                ToolTipIcon.Info);
        }, null);
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (popup.Visible)
        {
            popup.Hide();
            return;
        }

        ShowPopup(Cursor.Position, refresh: true);
    }

    private void ShowPopup(bool refresh)
    {
        ShowPopup(Cursor.Position, refresh);
    }

    private void ShowPopup(Point anchor, bool refresh)
    {
        foreach (var entry in codexCliEntries)
        {
            var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
            popup.UpdateUsage(providerKey, GetLatestUsage(providerKey));
        }

        popup.UpdateUsage(UsagePopupForm.ClaudeProviderKey, latestClaudeUsage);
        popup.UpdateUsage(UsagePopupForm.GrokProviderKey, latestGrokUsage);
        popup.UpdateUsage(UsagePopupForm.CursorProviderKey, latestCursorUsage);
        popup.UpdateUsage(UsagePopupForm.OpenCodeGoProviderKey, latestOpenCodeGoUsage);
        popup.ShowNear(anchor);

        if (refresh)
        {
            BeginRefresh(showLoading: true);
        }
    }

    private void UpdateRefreshTimerState()
    {
        var anyUsageWindowOpen = popup.Visible || graphsForm is { IsDisposed: false, Visible: true };
        if (anyUsageWindowOpen && !refreshTimer.Enabled)
        {
            refreshTimer.Start();
        }
        else if (!anyUsageWindowOpen && refreshTimer.Enabled)
        {
            refreshTimer.Stop();
        }
    }

    private void BeginRefresh(bool showLoading)
    {
        if (showLoading)
        {
            popup.SetLoading(popup.SelectedProvider);
        }

        if (graphsForm is { IsDisposed: false } activeGraphs)
        {
            activeGraphs.SetLoading(activeGraphs.SelectedProvider);
        }

        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();

        // The 30-day history rebuild parses local session logs; only the graphs window shows
        // it, so the scan is skipped entirely unless that window is open.
        var includeHistory = graphsForm is { IsDisposed: false, Visible: true };
        var generation = Interlocked.Increment(ref refreshGeneration);
        var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;

        _ = Task.Run(async () =>
        {
            void PostIfCurrent(Action update)
            {
                uiContext.Post(_ =>
                {
                    if (disposed || cancellation.IsCancellationRequested || generation != refreshGeneration)
                    {
                        return;
                    }

                    update();
                }, null);
            }

            async Task RefreshCodexLimitsAsync()
            {
                var codexTasks = codexCliEntries
                    .Select(entry => Task.Run(
                        () =>
                        {
                            try
                            {
                                var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
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
                                    UsagePopupForm.CodexProviderKey(entry.Id),
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
                        popup.UpdateUsage(pair.Key, merged);
                    }

                    notifyIcon.Text = BuildTooltip(codexCliEntries, latestCodexUsage, latestClaudeUsage, latestGrokUsage, latestCursorUsage, latestOpenCodeGoUsage);
                });
            }

            async Task RefreshCodexHistoryAsync()
            {
                var codexHistory = await Task.Run(() => new CodexUsageInsightsReader().ReadLatest(), cancellation.Token)
                    .ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    foreach (var entry in codexCliEntries)
                    {
                        var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
                        var merged = ProviderUsageInsightsLookupResult.KeepLastGood(
                            latestHistory.TryGetValue(providerKey, out var previous) ? previous : null,
                            codexHistory);
                        latestHistory[providerKey] = merged;
                        graphsForm?.UpdateHistory(providerKey, merged);
                    }
                });
            }

            async Task RefreshClaudeLimitsAsync()
            {
                var claudeResult = await claudeUsageReader.ReadLatestAsync(cancellation.Token).ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestClaudeUsage = ProviderUsageLookupResult.KeepLastGood(latestClaudeUsage, claudeResult);
                    popup.UpdateUsage(UsagePopupForm.ClaudeProviderKey, latestClaudeUsage);
                    notifyIcon.Text = BuildTooltip(codexCliEntries, latestCodexUsage, latestClaudeUsage, latestGrokUsage, latestCursorUsage, latestOpenCodeGoUsage);
                });
            }

            async Task RefreshClaudeHistoryAsync()
            {
                var claudeHistory = await Task.Run(() => new ClaudeUsageInsightsReader().ReadLatest(), cancellation.Token)
                    .ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    var merged = ProviderUsageInsightsLookupResult.KeepLastGood(
                        latestHistory.TryGetValue(UsagePopupForm.ClaudeProviderKey, out var previous) ? previous : null,
                        claudeHistory);
                    latestHistory[UsagePopupForm.ClaudeProviderKey] = merged;
                    graphsForm?.UpdateHistory(UsagePopupForm.ClaudeProviderKey, merged);
                });
            }

            async Task RefreshGrokLimitsAsync()
            {
                var grokResult = await grokUsageReader.ReadLatestAsync(cancellation.Token).ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestGrokUsage = ProviderUsageLookupResult.KeepLastGood(latestGrokUsage, grokResult);
                    popup.UpdateUsage(UsagePopupForm.GrokProviderKey, latestGrokUsage);
                    notifyIcon.Text = BuildTooltip(codexCliEntries, latestCodexUsage, latestClaudeUsage, latestGrokUsage, latestCursorUsage, latestOpenCodeGoUsage);
                });
            }

            async Task RefreshGrokHistoryAsync()
            {
                var grokHistory = await Task.Run(() => new GrokUsageInsightsReader().ReadLatest(), cancellation.Token)
                    .ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    var merged = ProviderUsageInsightsLookupResult.KeepLastGood(
                        latestHistory.TryGetValue(UsagePopupForm.GrokProviderKey, out var previous) ? previous : null,
                        grokHistory);
                    latestHistory[UsagePopupForm.GrokProviderKey] = merged;
                    graphsForm?.UpdateHistory(UsagePopupForm.GrokProviderKey, merged);
                });
            }

            async Task RefreshCursorLimitsAsync()
            {
                var cursorResult = await cursorUsageReader.ReadLatestAsync(cancellation.Token).ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestCursorUsage = ProviderUsageLookupResult.KeepLastGood(latestCursorUsage, cursorResult);
                    popup.UpdateUsage(UsagePopupForm.CursorProviderKey, latestCursorUsage);
                    notifyIcon.Text = BuildTooltip(codexCliEntries, latestCodexUsage, latestClaudeUsage, latestGrokUsage, latestCursorUsage, latestOpenCodeGoUsage);
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
                    popup.UpdateUsage(UsagePopupForm.OpenCodeGoProviderKey, latestOpenCodeGoUsage);
                    notifyIcon.Text = BuildTooltip(
                        codexCliEntries,
                        latestCodexUsage,
                        latestClaudeUsage,
                        latestGrokUsage,
                        latestCursorUsage,
                        latestOpenCodeGoUsage);
                });
            }

            // A disabled tool is not polled at all — that is the point of the setting, and it
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
                // catches an orchestration fault. It used to overwrite EVERY provider with the
                // same error, so one provider failing blanked the others' good data — the
                // retention above would then be pointless. Annotate without discarding.
                PostIfCurrent(() =>
                {
                    var message = $"Could not refresh usage limits: {exception.Message}";
                    foreach (var entry in codexCliEntries)
                    {
                        var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
                        var previous = latestCodexUsage.TryGetValue(providerKey, out var existing) ? existing : null;
                        var annotated = ProviderUsageLookupResult.KeepLastGood(
                            previous,
                            new ProviderUsageLookupResult(null, message));
                        latestCodexUsage[providerKey] = annotated;
                        popup.UpdateUsage(providerKey, annotated);
                    }

                    latestClaudeUsage = ProviderUsageLookupResult.KeepLastGood(
                        latestClaudeUsage,
                        new ProviderUsageLookupResult(null, message));
                    popup.UpdateUsage(UsagePopupForm.ClaudeProviderKey, latestClaudeUsage);
                    latestGrokUsage = ProviderUsageLookupResult.KeepLastGood(
                        latestGrokUsage,
                        new ProviderUsageLookupResult(null, message));
                    popup.UpdateUsage(UsagePopupForm.GrokProviderKey, latestGrokUsage);
                    latestCursorUsage = ProviderUsageLookupResult.KeepLastGood(
                        latestCursorUsage,
                        new ProviderUsageLookupResult(null, message));
                    popup.UpdateUsage(UsagePopupForm.CursorProviderKey, latestCursorUsage);
                    latestOpenCodeGoUsage = ProviderUsageLookupResult.KeepLastGood(
                        latestOpenCodeGoUsage,
                        new ProviderUsageLookupResult(null, message));
                    popup.UpdateUsage(UsagePopupForm.OpenCodeGoProviderKey, latestOpenCodeGoUsage);
                    notifyIcon.Text = BuildTooltip(
                        codexCliEntries,
                        latestCodexUsage,
                        latestClaudeUsage,
                        latestGrokUsage,
                        latestCursorUsage,
                        latestOpenCodeGoUsage);
                });
            }
            finally
            {
                uiContext.Post(_ =>
                {
                    if (ReferenceEquals(refreshCancellation, cancellation))
                    {
                        refreshCancellation = null;
                    }

                    cancellation.Dispose();
                }, null);
            }
        }, CancellationToken.None);
    }

    private void ShowUsageGraphs()
    {
        if (graphsForm is { IsDisposed: false })
        {
            PushHistoryToGraphs();
            graphsForm.Show();
            if (graphsForm.WindowState == FormWindowState.Minimized)
            {
                graphsForm.WindowState = FormWindowState.Normal;
            }

            graphsForm.Activate();
            UpdateRefreshTimerState();
            BeginRefresh(showLoading: false);
            return;
        }

        graphsForm = new UsageGraphsForm();
        graphsForm.ConfigureCodexEntries(codexCliEntries);
        graphsForm.SelectedProviderChanged += (_, providerKey) =>
        {
            if (!GetLatestHistory(providerKey).HasInsights)
            {
                BeginRefresh(showLoading: false);
            }
        };
        graphsForm.VisibleChanged += (_, _) => UpdateRefreshTimerState();
        // The popup deliberately stays open when this window takes focus, which also makes it
        // deaf to further focus changes. Re-arm its dismissal check from here.
        graphsForm.Deactivate += (_, _) => popup.HideIfFocusLeftProcess();
        graphsForm.FormClosed += (_, _) =>
        {
            graphsForm = null;
            UpdateRefreshTimerState();
        };
        PushHistoryToGraphs();
        graphsForm.Show();
        graphsForm.Activate();
        UpdateRefreshTimerState();

        if (!GetLatestHistory(graphsForm.SelectedProvider).HasInsights)
        {
            graphsForm.SetLoading(graphsForm.SelectedProvider);
        }

        // History is never refreshed in the background, so opening the window always fetches.
        BeginRefresh(showLoading: false);
    }

    private void PushHistoryToGraphs()
    {
        if (graphsForm is not { IsDisposed: false } form)
        {
            return;
        }

        foreach (var entry in codexCliEntries)
        {
            var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
            form.UpdateHistory(providerKey, GetLatestHistory(providerKey));
        }

        form.UpdateHistory(UsagePopupForm.ClaudeProviderKey, GetLatestHistory(UsagePopupForm.ClaudeProviderKey));
        form.UpdateHistory(UsagePopupForm.GrokProviderKey, GetLatestHistory(UsagePopupForm.GrokProviderKey));
    }

    private void ShowSettings()
    {
        if (settingsForm is { IsDisposed: false })
        {
            settingsForm.Show();
            settingsForm.Activate();
            return;
        }

        settingsForm = new SettingsForm();
        settingsForm.CodexCliEntriesChanged += (_, _) => ReloadCodexCliEntries();
        settingsForm.CursorSettingsChanged += (_, _) => BeginRefresh(showLoading: true);
        settingsForm.OpenCodeGoSettingsChanged += (_, _) => BeginRefresh(showLoading: true);
        settingsForm.FormClosed += (_, _) => settingsForm = null;
        // See the graphs window: re-arm the popup's dismissal check when focus leaves here.
        settingsForm.Deactivate += (_, _) => popup.HideIfFocusLeftProcess();
        settingsForm.Show();
        settingsForm.Activate();
    }

    private void BeginUpdateCheck(bool showResult = false)
    {
        if (Interlocked.Exchange(ref updateCheckInProgress, 1) == 1)
        {
            if (showResult)
            {
                notifyIcon.ShowBalloonTip(
                    3000,
                    "CodexBarWindows update",
                    "An update check is already running.",
                    ToolTipIcon.Info);
            }

            return;
        }

        SetUpdateMenuState(isChecking: true);

        _ = Task.Run(async () =>
        {
            UpdateCheckResult result;
            try
            {
                result = await releaseUpdater.CheckAndInstallLatestAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = UpdateCheckResult.Skipped($"Update check failed: {ex.Message}");
            }

            uiContext.Post(_ =>
            {
                updateCheckInProgress = 0;
                SetUpdateMenuState(isChecking: false);
                if (disposed)
                {
                    return;
                }

                if (result.Status == UpdateCheckStatus.Installing)
                {
                    notifyIcon.ShowBalloonTip(
                        5000,
                        "CodexBarWindows update",
                        $"Installing version {result.Version}. The app will restart shortly.",
                        ToolTipIcon.Info);

                    ExitThread();
                    return;
                }

                if (!showResult)
                {
                    return;
                }

                notifyIcon.ShowBalloonTip(
                    4000,
                    "CodexBarWindows update",
                    result.Message,
                    result.Status == UpdateCheckStatus.UpToDate ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }, null);
        });
    }

    private void SetUpdateMenuState(bool isChecking)
    {
        if (checkForUpdatesItem is null)
        {
            return;
        }

        checkForUpdatesItem.Enabled = !isChecking;
        checkForUpdatesItem.Text = isChecking ? "Checking for updates..." : "Check for updates";
    }

    /// <summary>
    /// Spends one banked reset credit for the Codex account that owns <paramref name="request"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not tied to <see cref="refreshCancellation"/>: a consume that has reached the
    /// backend cannot be recalled, so cancelling it would only lose the outcome, not the charge.
    /// </remarks>
    private void BeginResetCreditRedeem(CodexResetRedeemRequest request)
    {
        var entry = codexCliEntries.FirstOrDefault(
            candidate => UsagePopupForm.CodexProviderKey(candidate.Id) == request.ProviderKey);
        if (entry is null)
        {
            popup.SetResetCreditMessage(request.ProviderKey, "That Codex account is no longer configured.");
            return;
        }

        // The credit id came from this entry's own snapshot, and the redeemer runs this
        // entry's own binary, so the charge cannot land on another configured account.
        var redeemer = new CodexResetCreditRedeemer(entry.Id, entry.BinaryPath);
        var creditId = request.Credit.Id;

        _ = Task.Run(() => redeemer.Redeem(creditId)).ContinueWith(
            task =>
            {
                var result = task.IsFaulted
                    ? new CodexResetRedeemResult(
                        CodexResetOutcome.Failed,
                        task.Exception?.GetBaseException().Message)
                    : task.Result;

                uiContext.Post(
                    _ =>
                    {
                        if (disposed)
                        {
                            return;
                        }

                        popup.SetResetCreditMessage(
                            request.ProviderKey,
                            CodexResetCreditRedeemer.DescribeOutcome(result),
                            clearOnNextSnapshot: result.ChangedUsage);

                        if (result.ChangedUsage &&
                            codexStabilizers.TryGetValue(request.ProviderKey, out var stabilizer))
                        {
                            // Usage drops and the reset time moves, which the stabilizer reads
                            // as a conflict; without this the pre-reset numbers would stick.
                            stabilizer.InvalidateAcceptedSnapshot();
                        }

                        BeginRefresh(showLoading: false);
                    },
                    null);
            },
            TaskScheduler.Default);
    }

    private ProviderUsageLookupResult GetLatestUsage(string providerKey)
    {
        if (providerKey == UsagePopupForm.ClaudeProviderKey)
        {
            return latestClaudeUsage;
        }

        if (providerKey == UsagePopupForm.GrokProviderKey)
        {
            return latestGrokUsage;
        }

        if (providerKey == UsagePopupForm.CursorProviderKey)
        {
            return latestCursorUsage;
        }

        if (providerKey == UsagePopupForm.OpenCodeGoProviderKey)
        {
            return latestOpenCodeGoUsage;
        }

        return latestCodexUsage.TryGetValue(providerKey, out var result)
            ? result
            : new ProviderUsageLookupResult(null, "Usage has not been loaded yet.");
    }

    private ProviderUsageInsightsLookupResult GetLatestHistory(string providerKey)
    {
        return latestHistory.TryGetValue(providerKey, out var result)
            ? result
            : new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet.");
    }

    private void ReloadCodexCliEntries()
    {
        var previousPaths = codexCliEntries.ToDictionary(
            entry => UsagePopupForm.CodexProviderKey(entry.Id),
            entry => entry.BinaryPath ?? string.Empty);
        codexCliEntries = CodexCliSettings.Load();
        var activeProviderKeys = codexCliEntries
            .Select(entry => UsagePopupForm.CodexProviderKey(entry.Id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in codexCliEntries)
        {
            var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
            var binaryPath = entry.BinaryPath ?? string.Empty;
            var binaryChanged = previousPaths.TryGetValue(providerKey, out var previousPath) &&
                !string.Equals(previousPath, binaryPath, StringComparison.OrdinalIgnoreCase);
            if (binaryChanged)
            {
                latestCodexUsage[providerKey] = new ProviderUsageLookupResult(null, "Usage has not been loaded yet.");
                codexStabilizers[providerKey] = new CodexRateLimitStabilizer();
            }
            else
            {
                latestCodexUsage.TryAdd(providerKey, new ProviderUsageLookupResult(null, "Usage has not been loaded yet."));
                codexStabilizers.GetOrAdd(providerKey, _ => new CodexRateLimitStabilizer());
            }

            latestHistory.TryAdd(
                providerKey,
                new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet."));
        }

        foreach (var providerKey in codexStabilizers.Keys.Where(key => !activeProviderKeys.Contains(key)))
        {
            codexStabilizers.TryRemove(providerKey, out _);
            latestCodexUsage.Remove(providerKey);
            latestHistory.Remove(providerKey);
        }

        popup.ConfigureCodexEntries(codexCliEntries);
        foreach (var entry in codexCliEntries)
        {
            var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
            popup.UpdateUsage(providerKey, GetLatestUsage(providerKey));
        }

        latestHistory.TryAdd(UsagePopupForm.ClaudeProviderKey, new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet."));
        latestHistory.TryAdd(UsagePopupForm.GrokProviderKey, new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet."));
        popup.UpdateUsage(UsagePopupForm.ClaudeProviderKey, latestClaudeUsage);
        popup.UpdateUsage(UsagePopupForm.GrokProviderKey, latestGrokUsage);
        popup.UpdateUsage(UsagePopupForm.CursorProviderKey, latestCursorUsage);
        popup.UpdateUsage(UsagePopupForm.OpenCodeGoProviderKey, latestOpenCodeGoUsage);

        if (graphsForm is { IsDisposed: false } activeGraphs)
        {
            activeGraphs.ConfigureCodexEntries(codexCliEntries);
            PushHistoryToGraphs();
        }

        BeginRefresh(showLoading: false);
    }

    private static string BuildTooltip(
        IReadOnlyList<CodexCliEntry> codexEntries,
        IReadOnlyDictionary<string, ProviderUsageLookupResult> codexUsage,
        ProviderUsageLookupResult claudeUsage,
        ProviderUsageLookupResult grokUsage,
        ProviderUsageLookupResult cursorUsage,
        ProviderUsageLookupResult openCodeGoUsage)
    {
        return UsageTooltip.Build(codexEntries, codexUsage, claudeUsage, grokUsage, cursorUsage, openCodeGoUsage, UiSettings.Load());
    }

    /// <summary>
    /// Kept as a thin forwarder: the implementation moved to <see cref="UsageTooltip"/> in
    /// CodexBar.Core so the WinUI shell builds the identical tray tooltip.
    /// </summary>
    internal static string ShortWindow(int windowMinutes) => UsageTooltip.ShortWindow(windowMinutes);

}
