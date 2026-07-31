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
    /// <summary>
    /// Delay before the first automatic update check. The app is competing with the rest of the
    /// user's sign-in for disk and network at this point, and nothing here is urgent.
    /// </summary>
    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(10);

    /// <summary>Interval between automatic update checks. Matches the WinForms shell.</summary>
    private static readonly TimeSpan AutomaticUpdateCheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// How long the "installing" notice stays on screen before the process exits. The updater
    /// waits for THIS process to exit before running msiexec, so without a pause the user would
    /// see the flyout appear and vanish in the same frame with no idea why the app restarted.
    /// </summary>
    private static readonly TimeSpan InstallNoticeDwell = TimeSpan.FromSeconds(2.5);

    private readonly SingleInstanceGuard? singleInstance;

    private DispatcherQueue? queue;
    private UsageRefreshService? usage;
    private TaskbarIcon? trayIcon;
    private FlyoutWindow? flyout;
    private SettingsWindow? settingsWindow;
    private GraphsWindow? graphsWindow;
    private MenuFlyoutItem? checkForUpdatesItem;
    private DispatcherQueueTimer? updateTimer;
    private int updateCheckInProgress;

    public App(SingleInstanceGuard? singleInstance = null)
    {
        this.singleInstance = singleInstance;

        // Hooked BEFORE InitializeComponent, because XAML resource loading is itself a place
        // this can throw. See OnUnhandledException for why the app survives a UI-thread fault.
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        InitializeComponent();
    }

    // -------------------------------------------------------------- crash reporting

    /// <summary>
    /// The last line of defence on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This shell is UNPACKAGED, so the default outcome of a XAML-thread exception is a silent
    /// fail-fast: the tray icon vanishes mid-session with no dialog, no log and nothing in the
    /// event log the user can act on. That is the worst possible failure for a background app -
    /// the crash costs the user the app itself, and costs us the report.
    /// </para>
    /// <para>
    /// So the exception is written out in full and then <c>Handled</c> is set. The trade is
    /// deliberate: the alternative to a possibly-inconsistent window is NO APP AT ALL, and the
    /// state that matters here (settings, account list, cached usage) lives outside the visual
    /// tree and is re-read on the next refresh. This is a backstop, NOT a licence to leave
    /// throwing code in place - anything that lands here is a bug, and the log line is what makes
    /// it findable.
    /// </para>
    /// <para>
    /// It does NOT catch access violations from native code (Skia, the compositor); those bypass
    /// managed exception handling entirely. Preventing those is a matter of orderly teardown -
    /// see <c>GraphsWindow.Teardown</c>.
    /// </para>
    /// </remarks>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // BOTH are logged on purpose: WinUI's Exception is frequently a bare COMException whose
        // stack has already been lost across the XAML/CLR boundary, while Message still carries
        // the original type and text.
        DiagnosticLog.WriteCrash(
            "UNHANDLED (ui thread, handled - process kept alive): {0}{1}{2}",
            e.Message,
            Environment.NewLine,
            e.Exception?.ToString() ?? "<no exception object>");

        e.Handled = true;
    }

    /// <summary>
    /// A fault on a thread nobody owns. Nothing can be salvaged here - the runtime is already on
    /// its way down - so this exists purely so the crash leaves a trace.
    /// </summary>
    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e) =>
        DiagnosticLog.WriteCrash(
            "UNHANDLED (appdomain, terminating={0}): {1}{2}",
            e.IsTerminating,
            Environment.NewLine,
            e.ExceptionObject?.ToString() ?? "<no exception object>");

    /// <summary>
    /// A faulted Task whose exception nobody ever looked at. Harmless by default on modern .NET,
    /// but it is exactly how a background refresh failure would otherwise disappear.
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticLog.WriteCrash("UNOBSERVED task exception:{0}{1}", Environment.NewLine, e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// The running shell, for the windows that need to reach app-level commands (currently only
    /// the update check, which is owned here so the tray menu item, the settings button and the
    /// six-hourly timer all share one in-flight guard).
    /// </summary>
    public static App? Shell => Current as App;

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
        // The tray hover text. UsageTooltip.Build is shared with the WinForms shell, so both
        // produce byte-identical strings (and both get its 63-character shell clamp).
        usage.TooltipChanged += ApplyTrayTooltip;

        if (singleInstance is not null)
        {
            // Fired on a thread pool thread when a second instance starts and exits.
            singleInstance.ActivationRequested += () => queue.TryEnqueue(ShowFlyout);
        }

        CreateTrayIcon();
        DiagnosticLog.Write("shell ready (windowless)");

        // Pays LiveCharts' one-off Skia initialisation off-screen, so the first "Usage graphs"
        // open does not sit blank for half a second. See ChartPrewarm.
        ChartPrewarm.Start(queue);

        StartAutomaticUpdateChecks();
        MaybeAutoShow();
    }

    private void ApplyTrayTooltip(string text)
    {
        if (trayIcon is null)
        {
            return;
        }

        trayIcon.ToolTipText = text;
        DiagnosticLog.Write("tray tooltip: {0}", text);
    }

    private void CreateTrayIcon()
    {
        var graphsItem = CreateMenuItem("Usage graphs");
        graphsItem.Click += (_, _) => ShowGraphs();

        var settingsItem = CreateMenuItem("Settings");
        settingsItem.Click += (_, _) => ShowSettings();

        checkForUpdatesItem = CreateMenuItem("Check for updates");
        // Reported through the flyout: the tray menu is already gone by the time the HTTP call
        // comes back, so the outcome needs a surface that outlives it.
        checkForUpdatesItem.Click += (_, _) => CheckForUpdates(result =>
        {
            var window = EnsureFlyout();
            window.SetStatus(result.Message);
            window.ShowFlyout();
        });

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
            // Short and static on purpose. The shell BAKES the registration-time text into the
            // notification-area button's accessible name and then appends the live tooltip to
            // it, so a long placeholder here is read out in front of the usage figures forever
            // (verified through UI Automation: the button's Name is "<this> <ToolTipText>").
            ToolTipText = AppInfo.AppName,
            ContextFlyout = menu,
            ContextMenuMode = ContextMenuMode.SecondWindow,
            // The Codex glyph rendered for the current taskbar theme, not the boxed app icon —
            // the .ico has a light plate and reads as a bright tile beside other tray marks.
            Icon = TrayGlyph.Create(),
            LeftClickCommand = new RelayCommand(ToggleFlyout),
            NoLeftClickDelay = true
        };

        // false: do NOT opt the process into Efficiency Mode - background polling timers
        // (added in a later phase) must not be throttled.
        trayIcon.ForceCreate(enablesEfficiencyMode: false);

        WatchSystemThemeForTrayIcon();
    }

    private Windows.UI.ViewManagement.UISettings? trayThemeWatcher;
    private bool trayIconIsLight = TrayGlyph.IsLightSystemTheme();

    /// <summary>
    /// Re-renders the tray glyph when the user switches between a light and dark taskbar.
    /// </summary>
    /// <remarks>
    /// Held in a field: ColorValuesChanged fires on a background thread from a COM object that is
    /// collected the moment nothing roots it, which would silently stop the updates (the same
    /// failure mode already documented for the update timer). The handler filters on the taskbar
    /// theme specifically, because ColorValuesChanged also fires for accent-colour changes that
    /// do not affect a monochrome glyph.
    /// </remarks>
    private void WatchSystemThemeForTrayIcon()
    {
        trayThemeWatcher = new Windows.UI.ViewManagement.UISettings();
        trayThemeWatcher.ColorValuesChanged += (_, _) =>
        {
            var isLight = TrayGlyph.IsLightSystemTheme();
            if (isLight == trayIconIsLight)
            {
                return;
            }

            trayIconIsLight = isLight;
            queue?.TryEnqueue(() =>
            {
                if (trayIcon is null)
                {
                    return;
                }

                var previous = trayIcon.Icon;
                trayIcon.Icon = TrayGlyph.Create();
                previous?.Dispose();
                DiagnosticLog.Write("tray glyph rebuilt for {0} taskbar", isLight ? "light" : "dark");
            });
        };
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

    /// <param name="historyImport">
    /// Land on Settings ▸ Graphs, where the history import lives, instead of on whichever section
    /// the (cached, process-lifetime) window was left on. Set by the graphs window's "Import
    /// history" link, which asks for the surface and never starts the import itself.
    /// </param>
    private void ShowSettings(bool historyImport = false)
    {
        if (settingsWindow is not null)
        {
            if (historyImport)
            {
                settingsWindow.ShowHistoryImport();
            }
            else
            {
                settingsWindow.ShowAndFocus();
            }

            return;
        }

        var window = new SettingsWindow(usage!);
        settingsWindow = window;
        window.Closed += (_, _) => settingsWindow = null;
        // A sibling window losing focus is invisible to the flyout, so it re-arms the check.
        window.ActivationChanged += (_, _) => flyout?.ReArmDismissCheck();
        if (historyImport)
        {
            window.ShowHistoryImport();
        }
        else
        {
            window.ShowAndFocus();
        }
    }

    /// <summary>
    /// Opens the Usage graphs window. The window itself owns the history gate (it is the only
    /// surface that plots the 30-day scan) and the poll-timer registration, so this only has to
    /// create it once and hand it focus afterwards.
    /// </summary>
    private void ShowGraphs()
    {
        if (graphsWindow is not null)
        {
            graphsWindow.ShowAndFocus();
            return;
        }

        var window = new GraphsWindow(usage!);
        graphsWindow = window;
        window.Closed += (_, _) => graphsWindow = null;
        // A sibling window losing focus is invisible to the flyout, so it re-arms the check.
        window.ActivationChanged += (_, _) => flyout?.ReArmDismissCheck();
        // The timeline strip's "Import history" link. The backfill UI lives in Settings ▸ Graphs
        // and the shell is the only thing that can see both windows, so the wiring belongs here -
        // without it the link falls back to printing directions at the user.
        window.ImportHistoryRequested += (_, _) => ShowSettings(historyImport: true);
        window.ShowAndFocus();
    }

    // ------------------------------------------------------------------- updates

    /// <summary>True while an update check is in flight, so callers can disable their button.</summary>
    public bool IsCheckingForUpdates => Volatile.Read(ref updateCheckInProgress) == 1;

    /// <summary>Raised on the UI thread whenever <see cref="IsCheckingForUpdates"/> flips.</summary>
    public event Action<bool>? UpdateCheckStateChanged;

    /// <summary>
    /// Runs one update check against the GitHub releases feed and installs a newer MSI if there
    /// is one. Mirrors the WinForms shell, including the rule that only a build running from the
    /// installed location ever updates itself - a dev build reports that and does nothing.
    /// </summary>
    /// <param name="report">
    /// Called on the UI thread with the outcome, or <c>null</c> for a silent (automatic) check.
    /// An "installing" outcome is ALWAYS surfaced regardless, because it is about to restart the
    /// app and an unexplained restart is the one thing worse than a notification.
    /// </param>
    public void CheckForUpdates(Action<UpdateCheckResult>? report)
    {
        if (Interlocked.Exchange(ref updateCheckInProgress, 1) == 1)
        {
            report?.Invoke(UpdateCheckResult.Skipped("An update check is already running."));
            return;
        }

        SetUpdateCheckState(isChecking: true);

        _ = Task.Run(async () =>
        {
            UpdateCheckResult result;
            try
            {
                // ShellIdentity, not the defaults: while the two shells are installed side by
                // side this must look at its OWN install folder and download its OWN MSI.
                result = await ShellIdentity.CreateUpdater().CheckAndInstallLatestAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = UpdateCheckResult.Skipped($"Update check failed: {ex.Message}");
            }

            queue?.TryEnqueue(() =>
            {
                SetUpdateCheckState(isChecking: false);
                DiagnosticLog.Write("update check: {0} - {1}", result.Status, result.Message);

                if (result.Status == UpdateCheckStatus.Installing)
                {
                    AnnounceInstallAndExit(result);
                    return;
                }

                report?.Invoke(result);
            });
        });
    }

    /// <summary>
    /// Shows the "installing" notice, then exits so msiexec can replace the files. The updater
    /// has already queued a script that waits for this process, installs, and relaunches.
    /// </summary>
    private void AnnounceInstallAndExit(UpdateCheckResult result)
    {
        var window = EnsureFlyout();
        window.SetStatus($"{result.Message} The app will restart shortly.");
        window.ShowFlyout();

        if (queue is null)
        {
            ExitApp();
            return;
        }

        var exitTimer = queue.CreateTimer();
        exitTimer.Interval = InstallNoticeDwell;
        exitTimer.IsRepeating = false;
        exitTimer.Tick += (_, _) => ExitApp();
        exitTimer.Start();
    }

    /// <summary>
    /// Arms the automatic checks: one shortly after startup, then one every six hours. This is
    /// the ONLY thing this app does on a background timer - the usage poll is gated on a window
    /// being open (see <see cref="UsageRefreshService"/>), but an app that cannot reach its own
    /// updates unless someone opens a window would never update at all.
    /// </summary>
    private void StartAutomaticUpdateChecks()
    {
        if (queue is null)
        {
            return;
        }

        // ONE timer, held in a field, that re-arms itself at the long interval after the first
        // tick. A second local-variable timer for the startup check was tried first and its
        // 10-second tick NEVER ARRIVED: an unrooted DispatcherQueueTimer is collected out from
        // under its own Tick handler. The short-lived timers in MaybeAutoShow get away with it
        // only because 1.5 seconds is too soon for a collection to happen.
        updateTimer = queue.CreateTimer();
        updateTimer.Interval = FirstUpdateCheckDelay;
        updateTimer.IsRepeating = false;
        updateTimer.Tick += (timer, _) =>
        {
            CheckForUpdates(report: null);

            timer.Interval = AutomaticUpdateCheckInterval;
            timer.IsRepeating = true;
            timer.Start();
        };
        updateTimer.Start();
    }

    private void SetUpdateCheckState(bool isChecking)
    {
        updateCheckInProgress = isChecking ? 1 : 0;

        if (checkForUpdatesItem is not null)
        {
            checkForUpdatesItem.IsEnabled = !isChecking;
            checkForUpdatesItem.Text = isChecking ? "Checking for updates..." : "Check for updates";
        }

        UpdateCheckStateChanged?.Invoke(isChecking);
    }

    private void ExitApp()
    {
        DiagnosticLog.Write("exit requested");

        updateTimer?.Stop();
        updateTimer = null;

        ChartPrewarm.Stop();
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

        if (double.TryParse(
                Environment.GetEnvironmentVariable("CODEXBAR_WINUI_AUTOGRAPHS"),
                System.Globalization.CultureInfo.InvariantCulture,
                out var graphsDelay) && graphsDelay > 0)
        {
            var graphsTimer = queue.CreateTimer();
            graphsTimer.Interval = TimeSpan.FromSeconds(graphsDelay);
            graphsTimer.IsRepeating = false;
            graphsTimer.Tick += (_, _) => ShowGraphs();
            graphsTimer.Start();
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
