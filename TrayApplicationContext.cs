namespace CodexBarWindows;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly CodexUsageReader usageReader = new();
    private readonly Icon trayIcon = TrayIconFactory.Create();
    private readonly NotifyIcon notifyIcon;
    private readonly UsagePopupForm popup = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new();
    private readonly SynchronizationContext uiContext;
    private UsageLookupResult latestUsage = new(null, "Usage has not been loaded yet.");
    private CancellationTokenSource? refreshCancellation;
    private int refreshGeneration;
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

        refreshTimer.Interval = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
        refreshTimer.Tick += (_, _) => BeginRefresh(showLoading: false);
        refreshTimer.Start();

        BeginRefresh(showLoading: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            disposed = true;
            refreshCancellation?.Cancel();
            refreshCancellation?.Dispose();
            refreshTimer.Dispose();
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

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();

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
        popup.UpdateUsage(latestUsage);
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
            popup.SetLoading(latestUsage);
        }

        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();

        var generation = Interlocked.Increment(ref refreshGeneration);
        var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;

        _ = Task.Run(usageReader.ReadLatest, cancellation.Token).ContinueWith(task =>
        {
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

                latestUsage = task.IsCanceled
                    ? latestUsage
                    : task.Exception is null
                    ? task.Result
                    : new UsageLookupResult(null, $"Could not refresh Codex limits: {task.Exception.GetBaseException().Message}");

                popup.UpdateUsage(latestUsage);
                notifyIcon.Text = BuildTooltip(latestUsage);
                if (ReferenceEquals(refreshCancellation, cancellation))
                {
                    refreshCancellation = null;
                }

                cancellation.Dispose();
            }, null);
        }, CancellationToken.None);
    }

    private static string BuildTooltip(UsageLookupResult usage)
    {
        if (usage.Snapshot is not { } snapshot)
        {
            return TrimTooltip("Codex rate limits: no data found");
        }

        return TrimTooltip(
            $"Codex limits: 5h {snapshot.FiveHour.UsedPercent:0.#}% used, weekly {snapshot.Weekly.UsedPercent:0.#}% used");
    }

    private static string TrimTooltip(string value)
    {
        return value.Length <= 63 ? value : value[..63];
    }

}
