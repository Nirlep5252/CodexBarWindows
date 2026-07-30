using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CodexBarWindows;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBar.WinUI;

/// <summary>
/// The WinUI 3 application shell: a tray icon, a flyout, and secondary windows. It starts
/// WINDOWLESS - nothing is created until the user asks for it.
/// </summary>
public partial class App : Application
{
    private readonly SingleInstanceGuard? singleInstance;

    private DispatcherQueue? queue;
    private UsageRefreshService? usage;
    private TaskbarIcon? trayIcon;
    private FlyoutWindow? flyout;
    private SettingsWindow? settingsWindow;
    private ToolWindow? graphsWindow;
    private MenuFlyoutItem? checkForUpdatesItem;
    private int updateCheckInProgress;

    public App(SingleInstanceGuard? singleInstance = null)
    {
        this.singleInstance = singleInstance;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // MUST be set before any window exists: the default (OnLastWindowClose) would kill a
        // tray app the first time its flyout closes.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        queue = DispatcherQueue.GetForCurrentThread();
        AppTheme.Initialize(queue);

        // Everything the service touches is mutated on the UI thread, so it marshals through
        // the dispatcher rather than owning a lock.
        usage = new UsageRefreshService(action => queue.TryEnqueue(() => action()));
        usage.TooltipChanged += text =>
        {
            if (trayIcon is not null)
            {
                trayIcon.ToolTipText = text;
            }
        };

        if (singleInstance is not null)
        {
            // Fired on a thread pool thread when a second instance starts and exits.
            singleInstance.ActivationRequested += () => queue.TryEnqueue(ShowFlyout);
        }

        CreateTrayIcon();
        DiagnosticLog.Write("shell ready (windowless)");

        MaybeAutoShow();
    }

    private void CreateTrayIcon()
    {
        var graphsItem = CreateMenuItem("Usage graphs");
        graphsItem.Click += (_, _) => ShowGraphs();

        var settingsItem = CreateMenuItem("Settings");
        settingsItem.Click += (_, _) => ShowSettings();

        checkForUpdatesItem = CreateMenuItem("Check for updates");
        checkForUpdatesItem.Click += (_, _) => BeginUpdateCheck();

        var exitItem = CreateMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        var menu = new MenuFlyout();
        menu.Items.Add(graphsItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(checkForUpdatesItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        trayIcon = new TaskbarIcon
        {
            ToolTipText = AppInfo.AppName,
            ContextFlyout = menu,
            ContextMenuMode = ContextMenuMode.SecondWindow,
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "CodexBarWindows.ico")),
            LeftClickCommand = new RelayCommand(ToggleFlyout),
            NoLeftClickDelay = true
        };

        // false: do NOT opt the process into Efficiency Mode - background polling timers
        // (added in a later phase) must not be throttled.
        trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>
    /// The tray menu is hosted on H.NotifyIcon's tiny helper window, whose presenter measures
    /// too narrow and clips the longer labels; an explicit item width settles the layout.
    /// </summary>
    private static MenuFlyoutItem CreateMenuItem(string text) => new() { Text = text, MinWidth = 200 };

    private FlyoutWindow EnsureFlyout()
    {
        if (flyout is not null)
        {
            return flyout;
        }

        flyout = new FlyoutWindow(usage!);
        flyout.GraphsRequested += (_, _) => ShowGraphs();
        return flyout;
    }

    private void ToggleFlyout() => EnsureFlyout().Toggle();

    private void ShowFlyout() => EnsureFlyout().ShowFlyout();

    private void ShowSettings()
    {
        if (settingsWindow is not null)
        {
            settingsWindow.ShowAndFocus();
            return;
        }

        var window = new SettingsWindow(usage!);
        settingsWindow = window;
        window.Closed += (_, _) => settingsWindow = null;
        // A sibling window losing focus is invisible to the flyout, so it re-arms the check.
        window.ActivationChanged += (_, _) => flyout?.ReArmDismissCheck();
        window.ShowAndFocus();
    }

    private void ShowGraphs()
    {
        graphsWindow = ShowToolWindow(
            graphsWindow,
            window => graphsWindow = window,
            "Usage graphs",
            "Usage graphs",
            "Charts are built in a later phase. LiveCharts needs a pre-warm pass at startup, which lands with the charts themselves.",
            widthDip: 900,
            heightDip: 600);

        // The graphs window shows usage too, so it counts for the visibility gate; and it is
        // the only surface that plots the 30-day history, so it is the only reason to pay for
        // the session-log scan.
        if (usage is not null)
        {
            usage.IncludeHistory = true;
            usage.SetWindowOpen(GraphsWindowId, true);
            usage.Refresh();
        }
    }

    private const string GraphsWindowId = "graphs";

    private ToolWindow ShowToolWindow(
        ToolWindow? existing,
        Action<ToolWindow?> assign,
        string title,
        string heading,
        string body,
        int widthDip,
        int heightDip)
    {
        if (existing is not null)
        {
            existing.ShowAndFocus();
            return existing;
        }

        var window = new ToolWindow(title, heading, body, widthDip, heightDip);
        window.Closed += (_, _) =>
        {
            // Identity is checked BEFORE assign clears the field it would be compared against.
            var wasGraphs = ReferenceEquals(window, graphsWindow);
            assign(null);
            if (usage is not null && wasGraphs)
            {
                usage.IncludeHistory = false;
                usage.SetWindowOpen(GraphsWindowId, false);
            }
        };
        // A sibling window losing focus is invisible to the flyout, so it re-arms the check.
        window.ActivationChanged += (_, _) => flyout?.ReArmDismissCheck();
        window.ShowAndFocus();
        return window;
    }

    private void BeginUpdateCheck()
    {
        if (Interlocked.Exchange(ref updateCheckInProgress, 1) == 1)
        {
            return;
        }

        SetUpdateMenuState(isChecking: true);

        _ = Task.Run(async () =>
        {
            UpdateCheckResult result;
            try
            {
                result = await new GitHubReleaseUpdater().CheckAndInstallLatestAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = UpdateCheckResult.Skipped($"Update check failed: {ex.Message}");
            }

            queue?.TryEnqueue(() =>
            {
                updateCheckInProgress = 0;
                SetUpdateMenuState(isChecking: false);

                var window = EnsureFlyout();
                window.SetStatus(result.Message);
                window.ShowFlyout();

                if (result.Status == UpdateCheckStatus.Installing)
                {
                    ExitApp();
                }
            });
        });
    }

    private void SetUpdateMenuState(bool isChecking)
    {
        if (checkForUpdatesItem is null)
        {
            return;
        }

        checkForUpdatesItem.IsEnabled = !isChecking;
        checkForUpdatesItem.Text = isChecking ? "Checking for updates..." : "Check for updates";
    }

    private void ExitApp()
    {
        DiagnosticLog.Write("exit requested");

        settingsWindow?.Close();
        graphsWindow?.Close();
        flyout?.ShutDown();
        flyout = null;

        trayIcon?.Dispose();
        trayIcon = null;

        usage?.Dispose();
        usage = null;

        AppTheme.Shutdown();
        Exit();
    }

    /// <summary>
    /// Verification hook: <c>CODEXBAR_WINUI_AUTOSHOW=1</c> opens the flyout shortly after
    /// startup exactly as a tray left-click would, so the shell can be screenshotted and its
    /// focus behaviour driven from a script. Optionally
    /// <c>CODEXBAR_WINUI_AUTOEXIT=&lt;seconds&gt;</c> shuts the app down again afterwards.
    /// </summary>
    private void MaybeAutoShow()
    {
        if (queue is null)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_WINUI_AUTOSHOW") == "1")
        {
            var showTimer = queue.CreateTimer();
            showTimer.Interval = TimeSpan.FromSeconds(1.5);
            showTimer.IsRepeating = false;
            showTimer.Tick += (_, _) => ShowFlyout();
            showTimer.Start();
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_WINUI_AUTOSETTINGS") == "1")
        {
            var settingsTimer = queue.CreateTimer();
            settingsTimer.Interval = TimeSpan.FromSeconds(1.5);
            settingsTimer.IsRepeating = false;
            settingsTimer.Tick += (_, _) => ShowSettings();
            settingsTimer.Start();
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("CODEXBAR_WINUI_AUTOEXIT"), out var seconds) && seconds > 0)
        {
            var exitTimer = queue.CreateTimer();
            exitTimer.Interval = TimeSpan.FromSeconds(seconds);
            exitTimer.IsRepeating = false;
            exitTimer.Tick += (_, _) => ExitApp();
            exitTimer.Start();
        }
    }
}

internal sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => action();
}
