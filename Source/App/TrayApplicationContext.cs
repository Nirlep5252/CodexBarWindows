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
    private readonly System.Windows.Forms.Timer refreshTimer = new();
    private readonly System.Windows.Forms.Timer updateTimer = new();
    private readonly SynchronizationContext uiContext;
    private ToolStripMenuItem? checkForUpdatesItem;
    private IReadOnlyList<CodexCliEntry> codexCliEntries;
    private readonly Dictionary<string, ProviderUsageLookupResult> latestCodexUsage = [];
    private readonly Dictionary<string, ProviderUsageInsightsLookupResult> latestHistory = [];
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
            var needsHistory = providerKey != UsagePopupForm.CursorProviderKey;
            if (!GetLatestUsage(providerKey).HasSnapshot || (needsHistory && !GetLatestHistory(providerKey).HasInsights))
            {
                BeginRefresh(showLoading: true);
            }
        };

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

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => ShowSettings();

        checkForUpdatesItem = new ToolStripMenuItem("Check for updates");
        checkForUpdatesItem.Click += (_, _) => BeginUpdateCheck(showResult: true);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();

        menu.Items.Add(settingsItem);
        menu.Items.Add(checkForUpdatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
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
            popup.UpdateProviderHistory(providerKey, GetLatestHistory(providerKey));
        }

        popup.UpdateUsage(UsagePopupForm.ClaudeProviderKey, latestClaudeUsage);
        popup.UpdateProviderHistory(UsagePopupForm.ClaudeProviderKey, GetLatestHistory(UsagePopupForm.ClaudeProviderKey));
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
            popup.SetProviderHistoryLoading(popup.SelectedProvider);
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
                                var reader = new CodexUsageReader(entry.BinaryPath);
                                return new KeyValuePair<string, ProviderUsageLookupResult>(
                                    UsagePopupForm.CodexProviderKey(entry.Id),
                                    reader.ReadLatest().ToProviderResult());
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
                        popup.UpdateProviderHistory(providerKey, codexHistory);
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
                    popup.UpdateProviderHistory(UsagePopupForm.ClaudeProviderKey, claudeHistory);
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
        codexCliEntries = CodexCliSettings.Load();
        foreach (var entry in codexCliEntries)
        {
            var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
            latestCodexUsage.TryAdd(providerKey, new ProviderUsageLookupResult(null, "Usage has not been loaded yet."));
            latestHistory.TryAdd(
                providerKey,
                new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet."));
        }

        popup.ConfigureCodexEntries(codexCliEntries);
        foreach (var entry in codexCliEntries)
        {
            var providerKey = UsagePopupForm.CodexProviderKey(entry.Id);
            popup.UpdateUsage(providerKey, GetLatestUsage(providerKey));
            popup.UpdateProviderHistory(providerKey, GetLatestHistory(providerKey));
        }

        latestHistory.TryAdd(UsagePopupForm.ClaudeProviderKey, new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet."));
        popup.UpdateUsage(UsagePopupForm.ClaudeProviderKey, latestClaudeUsage);
        popup.UpdateProviderHistory(UsagePopupForm.ClaudeProviderKey, GetLatestHistory(UsagePopupForm.ClaudeProviderKey));
        popup.UpdateUsage(UsagePopupForm.CursorProviderKey, latestCursorUsage);
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
                    ? $"{entry.Name} {snapshot.Primary.UsedPercent:0.#}%"
                    : $"{entry.Name} --";
            }));
        var claudeText = claudeUsage.Snapshot is { } claude
            ? $"Claude {claude.Primary.UsedPercent:0.#}%"
            : "Claude --";
        var cursorText = cursorUsage.Snapshot is { } cursor
            ? $"Cursor {cursor.Primary.UsedPercent:0.#}%"
            : "Cursor --";

        return TrimTooltip($"{codexText} 5h, {claudeText} 5h, {cursorText}");
    }

    private static string TrimTooltip(string value)
    {
        return value.Length <= 63 ? value : value[..63];
    }

}
