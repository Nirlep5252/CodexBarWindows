namespace CodexBarWindows;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly CodexUsageReader usageReader = new();
    private readonly ClaudeUsageReader claudeUsageReader = new();
    private readonly GitHubReleaseUpdater releaseUpdater = new();
    private readonly Icon trayIcon = TrayIconFactory.Create();
    private readonly NotifyIcon notifyIcon;
    private readonly UsagePopupForm popup = new();
    private SettingsForm? settingsForm;
    private readonly System.Windows.Forms.Timer refreshTimer = new();
    private readonly System.Windows.Forms.Timer updateTimer = new();
    private readonly SynchronizationContext uiContext;
    private ToolStripMenuItem? checkForUpdatesItem;
    private ProviderUsageLookupResult latestCodexUsage = new(null, "Usage has not been loaded yet.");
    private ProviderUsageLookupResult latestClaudeUsage = new(null, "Usage has not been loaded yet.");
    private CancellationTokenSource? refreshCancellation;
    private int refreshGeneration;
    private int updateCheckInProgress;
    private bool disposed;

    public TrayApplicationContext()
    {
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = CreateContextMenu(),
            Icon = trayIcon,
            Text = "Codex rate limits",
            Visible = true
        };
        notifyIcon.MouseUp += OnTrayMouseUp;
        popup.SelectedProviderChanged += (_, provider) =>
        {
            if (!GetLatestUsage(provider).HasSnapshot)
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
        popup.UpdateUsage(UsageProvider.Codex, latestCodexUsage);
        popup.UpdateUsage(UsageProvider.Claude, latestClaudeUsage);
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

        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();

        var generation = Interlocked.Increment(ref refreshGeneration);
        var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;

        _ = Task.Run(async () =>
        {
            ProviderUsageLookupResult codexResult;
            ProviderUsageLookupResult claudeResult;

            try
            {
                var codexTask = Task.Run(
                    () =>
                    {
                        try
                        {
                            return usageReader.ReadLatest().ToProviderResult();
                        }
                        catch (Exception exception)
                        {
                            return new ProviderUsageLookupResult(
                                null,
                                $"Could not refresh Codex limits: {exception.Message}");
                        }
                    },
                    cancellation.Token);

                var claudeTask = claudeUsageReader.ReadLatestAsync(cancellation.Token);

                codexResult = await codexTask.ConfigureAwait(false);
                claudeResult = await claudeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            uiContext.Post(_ =>
            {
                if (disposed || cancellation.IsCancellationRequested || generation != refreshGeneration)
                {
                    if (ReferenceEquals(refreshCancellation, cancellation))
                    {
                        refreshCancellation = null;
                    }

                    cancellation.Dispose();
                    return;
                }

                latestCodexUsage = codexResult;
                latestClaudeUsage = claudeResult;

                popup.UpdateUsage(UsageProvider.Codex, latestCodexUsage);
                popup.UpdateUsage(UsageProvider.Claude, latestClaudeUsage);
                notifyIcon.Text = BuildTooltip(latestCodexUsage, latestClaudeUsage);
                if (ReferenceEquals(refreshCancellation, cancellation))
                {
                    refreshCancellation = null;
                }

                cancellation.Dispose();
            }, null);
        }, CancellationToken.None).ContinueWith(task =>
        {
            if (task.Exception is null)
            {
                return;
            }

            uiContext.Post(_ =>
            {
                if (disposed || cancellation.IsCancellationRequested || generation != refreshGeneration)
                {
                    return;
                }

                latestCodexUsage = new ProviderUsageLookupResult(
                    null,
                    $"Could not refresh usage limits: {task.Exception.GetBaseException().Message}");
                latestClaudeUsage = latestCodexUsage;
                popup.UpdateUsage(UsageProvider.Codex, latestCodexUsage);
                popup.UpdateUsage(UsageProvider.Claude, latestClaudeUsage);
                notifyIcon.Text = BuildTooltip(latestCodexUsage, latestClaudeUsage);
                if (ReferenceEquals(refreshCancellation, cancellation))
                {
                    refreshCancellation = null;
                }

                cancellation.Dispose();
            }, null);
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

    private ProviderUsageLookupResult GetLatestUsage(UsageProvider provider)
    {
        return provider == UsageProvider.Claude ? latestClaudeUsage : latestCodexUsage;
    }

    private static string BuildTooltip(ProviderUsageLookupResult codexUsage, ProviderUsageLookupResult claudeUsage)
    {
        if (codexUsage.Snapshot is null && claudeUsage.Snapshot is null)
        {
            return TrimTooltip("CodexBarWindows: no usage data found");
        }

        var codexText = codexUsage.Snapshot is { } codex
            ? $"Codex {codex.Primary.UsedPercent:0.#}%"
            : "Codex --";
        var claudeText = claudeUsage.Snapshot is { } claude
            ? $"Claude {claude.Primary.UsedPercent:0.#}%"
            : "Claude --";

        return TrimTooltip($"{codexText} 5h used, {claudeText} 5h used");
    }

    private static string TrimTooltip(string value)
    {
        return value.Length <= 63 ? value : value[..63];
    }

}
