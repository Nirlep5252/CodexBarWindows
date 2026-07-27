namespace CodexBarWindows;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ClaudeUsageReader claudeUsageReader = new();
    private readonly CursorUsageReader cursorUsageReader = new();
    private readonly GitHubReleaseUpdater releaseUpdater = new();
    private readonly Icon trayIcon = TrayIconFactory.Create();
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
    private ProviderUsageLookupResult latestCursorUsage = new(null, "Usage has not been loaded yet.");
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
        popup.ResetCreditRedeemRequested += (_, request) => BeginResetCreditRedeem(request);

        refreshTimer.Interval = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
        refreshTimer.Tick += (_, _) => BeginRefresh(showLoading: false);
        refreshTimer.Start();

        updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
        updateTimer.Tick += (_, _) => BeginUpdateCheck();
        updateTimer.Start();

        BeginRefresh(showLoading: false);
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => BeginUpdateCheck(), TaskScheduler.Default);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            disposed = true;
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
        popup.UpdateUsage(UsagePopupForm.CursorProviderKey, latestCursorUsage);
        popup.ShowNear(anchor);

        if (refresh)
        {
            BeginRefresh(showLoading: true);
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
                var codexResults = (await Task.WhenAll(codexTasks).ConfigureAwait(false))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                PostIfCurrent(() =>
                {
                    foreach (var pair in codexResults)
                    {
                        latestCodexUsage[pair.Key] = pair.Value;
                        popup.UpdateUsage(pair.Key, pair.Value);
                    }

                    notifyIcon.Text = BuildTooltip(codexCliEntries, latestCodexUsage, latestClaudeUsage, latestCursorUsage);
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
                        latestHistory[providerKey] = codexHistory;
                        graphsForm?.UpdateHistory(providerKey, codexHistory);
                    }
                });
            }

            async Task RefreshClaudeLimitsAsync()
            {
                var claudeResult = await claudeUsageReader.ReadLatestAsync(cancellation.Token).ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestClaudeUsage = claudeResult;
                    popup.UpdateUsage(UsagePopupForm.ClaudeProviderKey, latestClaudeUsage);
                    notifyIcon.Text = BuildTooltip(codexCliEntries, latestCodexUsage, latestClaudeUsage, latestCursorUsage);
                });
            }

            async Task RefreshClaudeHistoryAsync()
            {
                var claudeHistory = await Task.Run(() => new ClaudeUsageInsightsReader().ReadLatest(), cancellation.Token)
                    .ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestHistory[UsagePopupForm.ClaudeProviderKey] = claudeHistory;
                    graphsForm?.UpdateHistory(UsagePopupForm.ClaudeProviderKey, claudeHistory);
                });
            }

            async Task RefreshCursorLimitsAsync()
            {
                var cursorResult = await cursorUsageReader.ReadLatestAsync(cancellation.Token).ConfigureAwait(false);
                PostIfCurrent(() =>
                {
                    latestCursorUsage = cursorResult;
                    popup.UpdateUsage(UsagePopupForm.CursorProviderKey, latestCursorUsage);
                    notifyIcon.Text = BuildTooltip(codexCliEntries, latestCodexUsage, latestClaudeUsage, latestCursorUsage);
                });
            }

            var refreshTasks = new[]
            {
                RefreshCodexLimitsAsync(),
                RefreshCodexHistoryAsync(),
                RefreshClaudeLimitsAsync(),
                RefreshClaudeHistoryAsync(),
                RefreshCursorLimitsAsync(),
            };

            try
            {
                await Task.WhenAll(refreshTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                PostIfCurrent(() =>
                {
                    var failed = new ProviderUsageLookupResult(
                        null,
                        $"Could not refresh usage limits: {exception.Message}");
                    foreach (var entry in codexCliEntries)
                    {
                        var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
                        latestCodexUsage[providerKey] = failed;
                        popup.UpdateUsage(providerKey, failed);
                    }

                    latestClaudeUsage = failed;
                    popup.UpdateUsage(UsagePopupForm.ClaudeProviderKey, latestClaudeUsage);
                    latestCursorUsage = failed;
                    popup.UpdateUsage(UsagePopupForm.CursorProviderKey, latestCursorUsage);
                    notifyIcon.Text = BuildTooltip(codexCliEntries, latestCodexUsage, latestClaudeUsage, latestCursorUsage);
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
        graphsForm.FormClosed += (_, _) => graphsForm = null;
        PushHistoryToGraphs();
        graphsForm.Show();
        graphsForm.Activate();

        if (!GetLatestHistory(graphsForm.SelectedProvider).HasInsights)
        {
            graphsForm.SetLoading(graphsForm.SelectedProvider);
            BeginRefresh(showLoading: false);
        }
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
        settingsForm.FormClosed += (_, _) => settingsForm = null;
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

        if (providerKey == UsagePopupForm.CursorProviderKey)
        {
            return latestCursorUsage;
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
        popup.UpdateUsage(UsagePopupForm.ClaudeProviderKey, latestClaudeUsage);
        popup.UpdateUsage(UsagePopupForm.CursorProviderKey, latestCursorUsage);

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
        ProviderUsageLookupResult cursorUsage)
    {
        if (codexUsage.Values.All(result => result.Snapshot is null) && claudeUsage.Snapshot is null && cursorUsage.Snapshot is null)
        {
            return TrimTooltip("CodexBarWindows: no usage data found");
        }

        var codexText = string.Join(
            ", ",
            codexEntries.Take(2).Select(entry =>
            {
                var result = codexUsage.TryGetValue(UsagePopupForm.CodexProviderKey(entry.Id), out var value)
                    ? value
                    : null;
                return result?.Snapshot is { } snapshot
                    ? $"{entry.Name} {snapshot.Primary.UsedPercent:0.#}% {ShortWindow(snapshot.Primary.WindowMinutes)}"
                    : $"{entry.Name} --";
            }));
        var claudeText = claudeUsage.Snapshot is { } claude
            ? $"Claude {claude.Primary.UsedPercent:0.#}% {ShortWindow(claude.Primary.WindowMinutes)}"
            : "Claude --";
        var cursorText = cursorUsage.Snapshot is { } cursor
            ? $"Cursor {cursor.Primary.UsedPercent:0.#}%"
            : "Cursor --";

        return TrimTooltip($"{codexText}, {claudeText}, {cursorText}");
    }

    internal static string ShortWindow(int windowMinutes)
    {
        if (windowMinutes >= 1440 && windowMinutes % 1440 == 0)
        {
            return $"{windowMinutes / 1440}d";
        }

        return windowMinutes >= 60 && windowMinutes % 60 == 0
            ? $"{windowMinutes / 60}h"
            : $"{windowMinutes}m";
    }

    private static string TrimTooltip(string value)
    {
        return value.Length <= 63 ? value : value[..63];
    }

}
