using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CodexBarWindows;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.ImageFilters;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBar.WinUI;

/// <summary>
/// The "Usage graphs" window: a TIME EXPLORER over one provider's local usage history - a timeline
/// strip that moves the period, four metrics about it, a stacked column chart whose columns can be
/// clicked, and a per-model breakdown in the flyout's row anatomy.
/// </summary>
/// <remarks>
/// <para>
/// History comes from <see cref="UsageLedger"/>, an append/merge store of TOKENS keyed by UTC hour
/// that the 30-day scan writes as a byproduct. That is what makes "past months" possible at all: the
/// scan itself discards everything older than its window, while the ledger keeps it and re-prices it
/// at read time, so a pricing correction retroactively fixes every month ever recorded.
/// </para>
/// <para>
/// THE HISTORY GATE IS STILL LOAD-BEARING. The 30-day rebuild parses every local session log, and
/// this window is the only reason to pay for it, so <see cref="UsageRefreshService.IncludeHistory"/>
/// is turned on while this window is showing and off again when it closes. Nothing here touches a
/// timer or a startup path; the ledger is read only while the window is open.
/// </para>
/// <para>
/// The ledger is preferred over the scan whenever it has anything to say about the period, because
/// it is the only source with hour buckets and per-bucket model breakdowns. When it has nothing yet
/// (a cold install, before the first merge lands) the period falls back to the scan's daily rows -
/// ONE source per period, never a sum of the two, so double counting is structurally impossible.
/// </para>
/// </remarks>
public sealed partial class GraphsWindow : Window
{
    /// <summary>Distinct stacked categories drawn before the rest are pooled into "other".</summary>
    private const int MaxDailySeries = 7;

    /// <summary>Model rows drawn before the rest collapse into the "+N more" row.</summary>
    private const int MaxModelRows = 8;

    private const string WindowId = "graphs";

    /// <summary>
    /// The smallest size the layout is designed to survive, in DIPs. Below this the chart has no
    /// plot area left once its axis labels and legend are drawn, so rather than let the user drag
    /// into a broken window the presenter refuses the size. Derived from the row budget: 12+12
    /// padding + header 32 + timeline 40 + metrics 56 + status 18 + six 10px gaps + the 150/128
    /// chart and row-card minimums = 508, rounded up for an open error line. Horizontally, the
    /// metric card's four cells plus the model row's fixed 118/64/76 columns need ~600 before the
    /// meter stops being a stub.
    /// <para>
    /// Raised from 520 to pay for the TODAY BAR (~34 DIP plus one 10px RowSpacing) and the
    /// breakdown card's pinned total footer (~22). Without the raise the user can drag the window
    /// to a size where the chart's plot area collapses, which is the exact condition the enforced
    /// minimum exists to prevent.
    /// </para>
    /// </summary>
    private const int MinimumWidthDips = 600;
    private const int MinimumHeightDips = 580;

    private readonly IntPtr hwnd;
    private readonly UsageRefreshService service;
    private readonly List<ProviderOption> providers = [];

    /// <summary>
    /// Times the open. The number that matters is "window created -> first chart frame", because
    /// until that frame lands the window is blank; it is logged once so the startup pre-warm can
    /// be shown to work rather than assumed to.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch openStopwatch = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// THE SERIES ARE BUILT ONCE AND THEN MUTATED. Assigning a new <c>List&lt;ISeries&gt;</c> to
    /// <c>Chart.Series</c> is indistinguishable from a first draw to LiveCharts - it tracks
    /// "already measured" per SERIES INSTANCE, so replaced instances have no cached point visuals
    /// and every geometry is re-created at its zero state and animated in. That is why a single
    /// refresh used to play the entrance animation two or three times.
    /// <para>
    /// So: one <see cref="ObservableCollection{T}"/> handed to the chart once, holding series that
    /// survive across renders, each backed by an <see cref="ObservableCollection{T}"/> of
    /// <c>DateTimePoint</c> (which implements <c>INotifyPropertyChanged</c>). Updating a point's
    /// value animates just that delta, which is the motion the user actually wants on a refresh.
    /// The list is only rebuilt when the SHAPE changes - a different category set, a different
    /// provider, or a different period - see <see cref="dailyShape"/>.
    /// </para>
    /// </summary>
    private readonly ObservableCollection<ISeries> dailySeries = [];
    private readonly List<DailySeriesSlot> dailySlots = [];

    /// <summary>
    /// The model rows, retained and mutated for exactly the reason <see cref="ModelRowModel"/>
    /// documents: rebuilding hands every <c>ProgressBar</c> a fresh 0 -&gt; N and replays the meter
    /// slide-in on every render.
    /// </summary>
    private readonly ObservableCollection<ModelRowModel> modelRows = [];

    private ChartPalette palette;
    private string selectedProviderKey = ProviderKeys.Codex("default");
    private string errorFullText = string.Empty;
    private string statusFullText = string.Empty;
    private bool suppressComboEvents;

    /// <summary>
    /// SelectorBar raises SelectionChanged while its items are being added, i.e. from inside the
    /// constructor - the same trap ProviderCombo needs <see cref="suppressComboEvents"/> for.
    /// </summary>
    private bool suppressGranularityEvents;

    private bool isOpen;
    private bool firstFrameLogged;

    /// <summary>
    /// Set by <see cref="Teardown"/> the moment this window starts closing, and checked by every
    /// handler before it touches XAML.
    /// </summary>
    /// <remarks>
    /// Same guard, and the same reason, as <c>SettingsWindow.isClosed</c>: a callback can outlive
    /// the window that armed it, and touching a closed WinUI window's content throws - which, on
    /// the UI thread of an unpackaged app, is a process kill rather than an error. This window has
    /// far more of those callbacks than settings does (a 30-day session-log scan, an animated Skia
    /// chart and a live theme feed), so the guard is checked at EVERY entry point rather than only
    /// at the one that was known to fire late.
    /// </remarks>
    private bool isClosed;

    /// <summary>
    /// Cancelled by <see cref="Teardown"/> BEFORE it clears the ledger's read cache. Every
    /// background ledger read this window starts carries this token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="isClosed"/> cannot do this job. It is written and read on the UI thread and it
    /// guards XAML touches; the thing that has to be ordered here is a THREAD-POOL read that
    /// re-populates a static cache. A plain bool checked before the I/O would only narrow the race
    /// (the check can pass a microsecond before the close), and the failure it narrows is not a
    /// stale pixel - it is every parsed shard staying resident for the life of a tray process that
    /// is supposed to cost nothing when idle, which is precisely what
    /// <see cref="UsageLedger.ReleaseReadCache"/> exists to prevent.
    /// </para>
    /// <para>
    /// Never disposed, deliberately: nothing registers a callback or touches WaitHandle on it, so it
    /// owns no unmanaged resource, and a background task is entitled to read
    /// <c>IsCancellationRequested</c> long after the window is gone. Disposing it would buy nothing
    /// and would put a use-after-dispose on the one path that must not throw.
    /// </para>
    /// </remarks>
    private readonly System.Threading.CancellationTokenSource lifetime = new();

    private string dailyShape = string.Empty;
    private string modelRowShape = string.Empty;

    private StackTotalTooltip? dailyTooltip;

    /// <summary>
    /// The selection band behind the drilled-into column. ONE retained section, mutated in place:
    /// LiveCharts' Fill is per SERIES, so a single day cannot be highlighted by recolouring, and
    /// adding or removing a section counts as a chart-shape change that re-measures the plot.
    /// </summary>
    private RectangularSection? selectionSection;

    /// <summary>
    /// The X-axis UNIT the axis currently installed on the chart was built with.
    /// </summary>
    /// <remarks>
    /// Written ONLY inside <see cref="ApplyDailyAxis"/>, beside the axis it describes. The selection
    /// band is positioned from half of THIS rather than from half a bucket's duration, because the
    /// two are not the same number in Year view (a 30-day unit against 28-31 day buckets) - and a
    /// copy of it written anywhere else would drift out of sync with the axis actually installed and
    /// misalign the band in exactly one granularity.
    /// </remarks>
    private TimeSpan axisUnit = TimeSpan.FromDays(1);

    /// <summary>
    /// The collision nudge each category label takes, computed ONCE per render for the whole period.
    /// </summary>
    /// <remarks>
    /// It used to be a per-pass counter, and the chart and the rows iterate in DIFFERENT orders
    /// (stack order vs cost-descending), so two labels that collided got the base colour and the
    /// nudged one swapped between the legend swatch and the row meter - and a row could change
    /// colour just by entering a drill-down. The map is keyed by label and built from the WHOLE
    /// period's label set in a fixed ordinal order, so a label's colour depends on the period and on
    /// nothing else about what is currently on screen.
    /// </remarks>
    private readonly Dictionary<string, int> nudgeSteps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Brushes by ARGB. <see cref="ModelRowModel"/>'s equality guard compares Brush REFERENCES, so
    /// allocating a fresh <c>SolidColorBrush</c> every render defeats it and re-raises
    /// PropertyChanged for a colour that never moved - which is a meter re-animation per refresh.
    /// </summary>
    private readonly Dictionary<uint, Microsoft.UI.Xaml.Media.SolidColorBrush> brushCache = [];

    // ---- the period the strip is pointing at --------------------------------------------------

    private UsageLedgerGranularity granularity = UsageLedgerGranularity.Month;

    /// <summary>Any date INSIDE the selected period. The bounds are derived, never stored.</summary>
    private DateOnly anchor = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// The local day the last render was drawn for, so the window can re-base itself across
    /// midnight WITHOUT owning a timer. A DispatcherTimer here would be a new always-on cost and an
    /// object that outlives the window; comparing dates inside <see cref="Render"/> costs nothing
    /// and cannot outlive anything.
    /// </summary>
    private DateOnly renderedDay = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Local start of the drilled-into bucket, or null for the whole period.</summary>
    private DateTime? selectedBucket;

    private UsageLedgerCoverage coverage = UsageLedgerCoverage.None;

    /// <summary>
    /// The scope <see cref="coverage"/> actually describes.
    /// </summary>
    /// <remarks>
    /// Load-bearing, not bookkeeping. <see cref="RefreshCoverage"/> resolves off the UI thread while
    /// its callers render IMMEDIATELY, so without this the frame drawn right after a provider switch
    /// paired the new provider's series with the OLD provider's coverage — a back arrow clamped to a
    /// floor from the other corpus, and an import affordance answering a question about the wrong
    /// history. Nulling coverage the moment the scope changes makes that frame merely UNKNOWN, which
    /// is true, instead of confidently wrong.
    /// </remarks>
    private UsageLedgerScope? coverageScope;

    /// <summary>
    /// Bumped by every <see cref="RefreshCoverage"/>. A load that lands after a newer one was
    /// started is dropped rather than applied, which is what keeps a slow read of the Codex shards
    /// from overwriting the Claude coverage the user just switched to.
    /// </summary>
    private int coverageGeneration;

    private PeriodView? period;

    /// <summary>
    /// Identity of the data currently ON the chart. Both windows re-render for events that only
    /// move a spinner - <c>RefreshingChanged</c> alone fires twice per refresh, and its (false)
    /// edge always arrives AFTER the data - so without this the final state is plotted twice.
    /// The text and the busy ring still follow every render; only the plot is gated.
    /// </summary>
    private ProviderUsageInsights? plottedInsights;
    private string plottedKey = string.Empty;
    private string plottedScopeKey = string.Empty;

    public GraphsWindow(UsageRefreshService service)
    {
        this.service = service;

        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        palette = ChartPalette.For(RootGrid, AppTheme.Settings.ChartColorOverrides);

        Title = $"Usage graphs - {AppInfo.AppName}";
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "CodexBarWindows.ico"));

        var scale = NativeWindow.ScaleFor(hwnd);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(900 * scale),
            (int)Math.Round(700 * scale)));

        // The window is resizable and the layout is designed down to a floor rather than an
        // arbitrary one, so the floor is enforced instead of documented: OverlappedPresenter feeds
        // these straight into WM_GETMINMAXINFO's ptMinTrackSize, which - like every other size on
        // AppWindow - is in physical pixels, hence the same DPI scale as the Resize above.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(MinimumWidthDips * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinimumHeightDips * scale);
        }

        // Every one of these is a NAMED handler, not a lambda, so Teardown can detach it. A
        // lambda subscribed here would be unremovable, which is how DailyChart.UpdateFinished
        // ended up still live while its own chart was being destroyed.
        RootGrid.ActualThemeChanged += OnRootActualThemeChanged;
        AppTheme.Changed += OnThemeChanged;
        Activated += OnActivated;

        // Closing, NOT just Closed: this fires while the HWND and the XAML island are still
        // alive, which is the only moment the chart can be shut down in an orderly way. By the
        // time Closed runs the window is already gone. See Teardown.
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) => Teardown();

        service.HistoryUpdated += OnHistoryUpdated;
        service.RefreshingChanged += OnRefreshingChanged;
        service.CodexEntriesChanged += ConfigureProviders;

        DailyChart.UpdateFinished += OnDailyChartUpdateFinished;
        DailyChart.DataPointerDown += OnDailyChartDataPointerDown;

        ModelRows.ItemsSource = modelRows;

        AppTheme.Apply(this, RootGrid, TintLayer);
        ConfigureGranularityBar();
        ConfigureCharts();
        ConfigureProviders();
    }

    /// <summary>
    /// Raised whenever this window's activation changes, so the flyout can re-test whether the
    /// foreground is still inside this process. See <see cref="FlyoutWindow.ReArmDismissCheck"/>.
    /// </summary>
    public event EventHandler? ActivationChanged;

    /// <summary>
    /// Raised by the timeline strip's "Import history" link. The backfill lives in Settings ▸
    /// Graphs; this window only asks for it to be opened, so the two surfaces stay independent.
    /// </summary>
    public event EventHandler? ImportHistoryRequested;

    public string SelectedProvider => selectedProviderKey;

    public void ShowAndFocus()
    {
        if (isClosed)
        {
            return;
        }

        AppWindow.Show(activateWindow: true);
        NativeWindow.ForceForeground(hwnd);

        if (!isOpen)
        {
            isOpen = true;

            // The gate: this is the only window that plots the history, so it is the only reason
            // to pay for the session-log scan - and the poll timer follows window visibility.
            service.IncludeHistory = true;
            service.SetWindowOpen(WindowId, true);
            service.Refresh();

            DiagnosticLog.Write("graphs shown includeHistory=true polling={0}", service.IsPolling);
        }

        Render();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (isClosed)
        {
            return;
        }

        ActivationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args) => OnActualThemeChanged();

    private void OnDailyChartUpdateFinished(IChartView chart)
    {
        // Fires from the chart's own draw loop, which keeps running until the canvas settles -
        // so it can and does land after the window has started closing.
        if (isClosed || firstFrameLogged)
        {
            return;
        }

        firstFrameLogged = true;
        DiagnosticLog.Write(
            "graphs first chart frame in {0} ms (prewarmed={1})",
            openStopwatch.ElapsedMilliseconds,
            ChartPrewarm.IsWarm);
    }

    private void OnAppWindowClosing(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args) => Teardown();

    /// <summary>
    /// Takes this window out of circulation: nothing may call back into it, the chart stops
    /// drawing, and the history gate is released. Idempotent, and run from BOTH
    /// <c>AppWindow.Closing</c> and <c>Window.Closed</c> so it happens exactly once however the
    /// window goes (the user's X, <c>Close()</c> from <c>App.ExitApp</c>, or a shutdown that
    /// skips Closing altogether).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ORDER MATTERS. <see cref="isClosed"/> is set FIRST, before a single unsubscribe, so
    /// that a handler already sitting on the dispatcher queue - or one raised synchronously by
    /// the teardown below - finds the window shut rather than half-detached.
    /// </para>
    /// <para>
    /// This is the fix for closing the window mid-scan taking the whole tray process with it. The
    /// scan is the reason it reproduced there and not elsewhere: while it runs, the spinner, the
    /// chart overlay and the entrance animations all keep the LiveCharts motion canvas
    /// INVALIDATED, which keeps its frame ticker subscribed to the global
    /// <c>CompositionTarget.Rendering</c> and a Skia frame in flight on every compositor tick. Let
    /// the native window be destroyed underneath that and the render loop paints into a surface
    /// that no longer exists - an access violation, not a managed exception, which is why there
    /// was no dialog and nothing in any log. Once the chart settles (the idle case) the ticker has
    /// already unsubscribed itself, which is exactly why closing an idle window looked fine.
    /// </para>
    /// </remarks>
    private void Teardown()
    {
        if (isClosed)
        {
            return;
        }

        isClosed = true;

        // BEFORE the unsubscribes and, above all, before ReleaseReadCache below - the whole ordering
        // argument in RefreshCoverage rests on the cancel happening first. Cancel() is idempotent, so
        // the second entry point (Closing then Closed) is a no-op like the rest of this method.
        lifetime.Cancel();

        AppWindow.Closing -= OnAppWindowClosing;
        Activated -= OnActivated;
        RootGrid.ActualThemeChanged -= OnRootActualThemeChanged;
        AppTheme.Changed -= OnThemeChanged;
        service.HistoryUpdated -= OnHistoryUpdated;
        service.RefreshingChanged -= OnRefreshingChanged;
        service.CodexEntriesChanged -= ConfigureProviders;
        DailyChart.UpdateFinished -= OnDailyChartUpdateFinished;
        DailyChart.DataPointerDown -= OnDailyChartDataPointerDown;

        QuiesceCharts();
        ReleaseHistoryGate();

        // This window is the ONLY reader of the ledger in the process, so its close is the moment
        // the parsed shards provably have no future reader. Holding tens of MB of deserialized
        // history in a tray icon that is supposed to do nothing when idle is exactly the cost this
        // app refuses to pay; the next open pays a parse it is already waiting through a scan for.
        // Last, and outside the try above, for the same reason ReleaseHistoryGate is.
        UsageLedger.ReleaseReadCache();
    }

    /// <summary>
    /// Shuts the Skia chart down while the window is still alive to shut it down IN.
    /// </summary>
    /// <remarks>
    /// Dropping the window's content raises <c>Unloaded</c> on the tree, which is the only signal
    /// LiveCharts has to run its own teardown: unload the chart core, dispose the motion canvas
    /// and - the part that matters - unhook its frame ticker from
    /// <c>CompositionTarget.Rendering</c>. Doing it here means that happens with the XAML island
    /// intact, instead of racing the destruction of the HWND. <c>ChartPrewarm.Stop</c> already
    /// tears its off-screen chart down the same way, for the same reason.
    /// <para>
    /// The tooltip is dropped first and by hand: it is the one chart-owned visual this window
    /// keeps a reference to across renders (<see cref="dailyTooltip"/>), so it would otherwise
    /// outlive the canvas it draws on.
    /// </para>
    /// </remarks>
    private void QuiesceCharts()
    {
        try
        {
            // The rings are ONLY spinning while a scan is in flight - which is the exact repro -
            // and each one is a composition-animated visual of its own. Stopped by hand rather
            // than left for the unload, so the window's animated content is quiet before anything
            // starts being destroyed.
            RefreshSpinner.IsActive = false;
            DailyOverlayRing.IsActive = false;
            ModelBusyRing.IsActive = false;

            DailyChart.Tooltip = null;
            dailyTooltip = null;

            Content = null;
        }
        catch (Exception exception)
        {
            // Teardown must never be the thing that kills the process - that is the bug being
            // fixed. Anything that does go wrong here is recorded rather than thrown.
            DiagnosticLog.WriteCrash("graphs chart teardown failed: {0}", exception);
        }
    }

    /// <summary>
    /// Gives back the history gate and the poll-timer registration.
    /// </summary>
    /// <remarks>
    /// LOAD-BEARING, and deliberately outside the try above: this is what returns the app to zero
    /// idle cost, so it must run whatever else happened during teardown. Closing mid-scan is
    /// precisely the case where forgetting it would leave the 30-day scan armed on a one-minute
    /// timer for a window nobody can see.
    /// </remarks>
    private void ReleaseHistoryGate()
    {
        if (!isOpen)
        {
            return;
        }

        isOpen = false;
        service.IncludeHistory = false;
        service.SetWindowOpen(WindowId, false);

        // The scan that is running RIGHT NOW is the one this window asked for, and nothing is
        // left to plot it. Cancelling is a no-op while the flyout is still open, so the flyout's
        // own numbers are never pulled out from under it.
        service.CancelRefreshIfUnwatched();

        DiagnosticLog.Write("graphs closed includeHistory=false polling={0}", service.IsPolling);
    }

    // ---------------------------------------------------------------- providers

    private sealed record ProviderOption(string Key, string Name);

    /// <summary>
    /// Rebuilds the picker: one entry per configured Codex CLI account plus Claude. Cursor is
    /// absent by design - it has no local session history to plot. A tool the user switched off
    /// is absent too, because the service never scans a disabled tool's logs, so its entry could
    /// only ever say "no data".
    /// </summary>
    private void ConfigureProviders()
    {
        if (isClosed)
        {
            return;
        }

        var settings = AppTheme.Settings;
        var options = new List<ProviderOption>();

        if (settings.IsProviderEnabled(UsageProvider.Codex))
        {
            options.AddRange(service.CodexEntries.Select(entry => new ProviderOption(ProviderKeys.Codex(entry.Id), entry.Name)));
            if (options.Count == 0)
            {
                options.Add(new ProviderOption(ProviderKeys.Codex("default"), "Codex"));
            }
        }

        if (settings.IsProviderEnabled(UsageProvider.Claude))
        {
            options.Add(new ProviderOption(ProviderKeys.Claude, "Claude"));
        }

        if (settings.IsProviderEnabled(UsageProvider.Grok))
        {
            options.Add(new ProviderOption(ProviderKeys.Grok, "Grok"));
        }

        if (options.Count == 0)
        {
            options.Add(new ProviderOption(ProviderKeys.Codex("default"), "Codex"));
        }

        providers.Clear();
        providers.AddRange(options);

        var restored = providers.FindIndex(option => option.Key == selectedProviderKey);
        suppressComboEvents = true;
        ProviderCombo.Items.Clear();
        foreach (var option in providers)
        {
            ProviderCombo.Items.Add(option.Name);
        }

        ProviderCombo.SelectedIndex = restored >= 0 ? restored : 0;
        suppressComboEvents = false;

        selectedProviderKey = providers[Math.Max(0, ProviderCombo.SelectedIndex)].Key;
        RefreshCoverage();
        Render();
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Clearing ProviderCombo.Items during teardown would raise this against a dead tree.
        if (suppressComboEvents || isClosed)
        {
            return;
        }

        var index = ProviderCombo.SelectedIndex;
        if (index < 0 || index >= providers.Count)
        {
            return;
        }

        selectedProviderKey = providers[index].Key;

        // The two corpora do not share a coverage floor (Codex reaches months further back than
        // Claude), so the back arrow has to be re-clamped per provider, and a bucket selected in
        // one provider means nothing in the other.
        selectedBucket = null;
        RefreshCoverage();
        InvalidatePeriod();
        Render();

        if (!service.GetHistory(selectedProviderKey).HasInsights)
        {
            service.Refresh();
        }
    }

    // ---------------------------------------------------------------- timeline strip

    private void ConfigureGranularityBar()
    {
        // The bar raises SelectionChanged while it is being initialised, which would re-enter
        // Render before the charts are configured and re-arm the history gate mid-construction.
        suppressGranularityEvents = true;
        GranularityBar.SelectedItem = GranularityMonth;
        suppressGranularityEvents = false;
    }

    /// <summary>
    /// Puts the SelectorBar back in step with <see cref="granularity"/> after something OTHER than
    /// the bar changed it (the today bar, a drill-down). Suppressed, for the reason
    /// <see cref="suppressGranularityEvents"/> exists: assigning SelectedItem raises
    /// SelectionChanged, which would re-enter the very transition that is mid-flight.
    /// </summary>
    private void SyncGranularityBar()
    {
        var item = granularity switch
        {
            UsageLedgerGranularity.Year => GranularityYear,
            UsageLedgerGranularity.Week => GranularityWeek,
            UsageLedgerGranularity.Day => GranularityDay,
            _ => GranularityMonth
        };

        if (ReferenceEquals(GranularityBar.SelectedItem, item))
        {
            return;
        }

        suppressGranularityEvents = true;
        GranularityBar.SelectedItem = item;
        suppressGranularityEvents = false;
    }

    private void OnGranularityChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (suppressGranularityEvents || isClosed)
        {
            return;
        }

        var next = (sender.SelectedItem?.Tag as string) switch
        {
            "year" => UsageLedgerGranularity.Year,
            "week" => UsageLedgerGranularity.Week,
            "day" => UsageLedgerGranularity.Day,
            _ => UsageLedgerGranularity.Month
        };

        if (next == granularity)
        {
            return;
        }

        ApplyGranularity(next, anchor);
    }

    /// <summary>
    /// Switches granularity, keeping the user where they were.
    /// </summary>
    /// <remarks>
    /// The ANCHOR is kept, so "July, switched to Week" lands on the week containing the anchor
    /// rather than resetting the user to today. Two clamps then apply, in this order: a period that
    /// would sit in the FUTURE comes back to today, and a period that would sit entirely BEFORE the
    /// coverage floor moves forward onto the floor - the second is what stops "Month ▸ Year" on an
    /// old anchor landing the user on an empty year with a dead back arrow.
    /// </remarks>
    private void ApplyGranularity(UsageLedgerGranularity next, DateOnly at)
    {
        granularity = next;
        anchor = at;

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (GraphsPeriod.Bounds(granularity, anchor).Start > today)
        {
            anchor = today;
        }

        if (CoverageStart(service.GetHistory(selectedProviderKey).Insights) is { } floor &&
            GraphsPeriod.Bounds(granularity, anchor).EndInclusive < floor &&
            floor <= today)
        {
            anchor = floor;
        }

        selectedBucket = null;
        SyncGranularityBar();
        ApplyDailyAxis();
        InvalidatePeriod();
        Render();
    }

    /// <summary>
    /// The today bar's click: "show me today in detail".
    /// </summary>
    /// <remarks>
    /// The ONE place in the window where clicking something changes the period - worth the
    /// exception because it is the shortcut every piece of the feedback asked for, and because the
    /// bar is a permanent, unambiguous target rather than a chart element whose meaning moves.
    /// </remarks>
    private void OnTodayClick(object sender, RoutedEventArgs e)
    {
        if (isClosed)
        {
            return;
        }

        ApplyGranularity(UsageLedgerGranularity.Day, DateOnly.FromDateTime(DateTime.Now));
    }

    private void OnPreviousPeriod(object sender, RoutedEventArgs e) => MovePeriod(-1);

    private void OnNextPeriod(object sender, RoutedEventArgs e) => MovePeriod(1);

    private void MovePeriod(int delta)
    {
        if (isClosed)
        {
            return;
        }

        anchor = GraphsPeriod.Shift(granularity, anchor, delta);

        // A bucket selected in the period we just left cannot survive the move: it is no longer on
        // the chart, so keeping it would leave the breakdown showing a day the user cannot see.
        selectedBucket = null;
        InvalidatePeriod();
        Render();
    }

    private void OnJumpToNow(object sender, RoutedEventArgs e)
    {
        if (isClosed)
        {
            return;
        }

        anchor = DateOnly.FromDateTime(DateTime.Now);
        selectedBucket = null;
        InvalidatePeriod();
        Render();
    }

    private void OnImportHistoryClick(object sender, RoutedEventArgs e)
    {
        if (isClosed)
        {
            return;
        }

        // The import writes to the LEDGER, so it can do nothing at all for a provider that has no
        // scope behind it. Saying so beats running an import that completes and changes nothing.
        if (LedgerScope is null)
        {
            RenderStatus(
                $"{providers.FirstOrDefault(option => option.Key == selectedProviderKey)?.Name ?? "This tool"}" +
                " history is the last 30 days of local sessions — there is nothing older to import",
                isStale: false);
            return;
        }

        if (ImportHistoryRequested is { } handler)
        {
            handler(this, EventArgs.Empty);
            return;
        }

        // Nothing is listening (the shell wires this up), so the link still has to say where the
        // import lives rather than silently doing nothing.
        RenderStatus("Import older sessions from Settings ▸ Graphs", isStale: false);
    }

    // ---------------------------------------------------------------- rendering

    private void OnHistoryUpdated(string providerKey, ProviderUsageInsightsLookupResult result)
    {
        if (isClosed || providerKey != selectedProviderKey)
        {
            return;
        }

        // The scan merges into the ledger as a byproduct, so a completed scan can have moved the
        // coverage floor - and therefore the back arrow.
        RefreshCoverage();
        InvalidatePeriod();
        Render();
    }

    private void OnRefreshingChanged(bool refreshing) => Render();

    /// <summary>
    /// Coverage is a full parse of every shard for the scope, so it is resolved on the events that
    /// can actually move it (provider change, completed scan) rather than per render — and OFF the
    /// UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "A full parse of every shard" is disk I/O plus JSON deserialization that grows with every
    /// year of imported history, and it used to run on the dispatcher on every history update, with
    /// <see cref="BuildPeriod"/> re-reading the same files immediately afterwards. With a few years
    /// imported that is tens of MB of JSON per refresh and per period change, and the window
    /// visibly stutters.
    /// </para>
    /// <para>
    /// <c>WarmCache</c> is what makes the render path cheap rather than merely LATER: it parses the
    /// scope's shards into the ledger's read cache on this thread-pool thread, so the
    /// <c>BuildPeriod</c> that follows finds every year already deserialized and does a dictionary
    /// lookup plus one stat per shard. The cache is invalidated by any merge, so this cannot serve
    /// a stale answer.
    /// </para>
    /// <para>
    /// The GENERATION counter is not optional: the user can switch provider (or close the window)
    /// while a load is in flight, and a late answer for the previous scope is wrong rather than
    /// merely stale. The dispatcher is captured here, on the UI thread and while the window is
    /// alive, because reading it from a closed window is itself unsafe — and the callback re-checks
    /// <see cref="isClosed"/> for the reason the teardown guards exist at all.
    /// </para>
    /// <para>
    /// SYNCHRONOUS PART FIRST. Every caller renders immediately after this returns, so the scope
    /// change has to take effect on THIS thread — see <see cref="coverageScope"/>. And the failure
    /// path enqueues <see cref="UsageLedgerCoverage.None"/> rather than returning: a coverage read
    /// that threw knows nothing, and leaving the previous answer on screen would pin the UI to
    /// another provider's history with no second chance to correct it.
    /// </para>
    /// </remarks>
    private void RefreshCoverage()
    {
        var scope = LedgerScope;

        // Before any render can observe it. A coverage record belongs to exactly one scope, so the
        // instant the scope moves the old one stops being stale and starts being WRONG.
        if (coverageScope != scope)
        {
            coverageScope = scope;
            coverage = UsageLedgerCoverage.None;
        }

        var generation = ++coverageGeneration;
        var dispatcher = DispatcherQueue;

        // Captured here, on the UI thread and while the window is alive, for the same reason the
        // dispatcher is.
        var alive = lifetime.Token;

        _ = Task.Run(() =>
        {
            // LIVENESS BEFORE THE I/O, not only before the XAML touch. A task queued just before a
            // close would otherwise parse every shard for a window nobody can see, and leave the
            // result resident.
            if (alive.IsCancellationRequested)
            {
                return;
            }

            UsageLedgerCoverage next;
            try
            {
                // A scan-only provider has no shards and therefore no coverage floor of its own;
                // CoverageStart falls back to the scan's own earliest day, which is the truth for it.
                if (scope is not { } ledgerScope)
                {
                    next = UsageLedgerCoverage.None;
                }
                else
                {
                    UsageLedger.WarmCache(ledgerScope);
                    next = UsageLedger.GetCoverage(ledgerScope);
                }
            }
            catch
            {
                // Neither call is supposed to throw (both swallow their own I/O failures), but this
                // runs unobserved on the thread pool - an escape here would be a process-level
                // unhandled exception, which is the one outcome a coverage read must not have.
                next = UsageLedgerCoverage.None;
            }

            // AND AFTER IT, because the check above only NARROWS the window - the close can land at
            // any point during the parse, and by then this task has already re-populated the cache
            // that Teardown just cleared. So a late task cleans up after itself, which is what makes
            // the ordering total rather than merely unlikely:
            //
            //   Teardown is Cancel() THEN ReleaseReadCache(), in that order, on the UI thread.
            //   If this check observes cancellation, we clear what we just parsed - and no reader
            //   remains to want it, since this window is the ledger's only reader.
            //   If it does not, the Cancel had not happened yet, so it happens AFTER our parse and
            //   its ReleaseReadCache - which follows it - therefore also happens after our parse
            //   and wipes it.
            //
            // Either way the cache is empty once both have run, whatever the interleaving. (The
            // dispatcher callback below re-reads the ledger through Render, but that runs on the UI
            // thread, serialised against Teardown, and its isClosed guard settles that case.)
            if (alive.IsCancellationRequested)
            {
                UsageLedger.ReleaseReadCache();
                return;
            }

            dispatcher.TryEnqueue(() =>
            {
                // Closed, or superseded by a later provider selection: either way this answer is
                // about a window or a scope that no longer exists.
                if (isClosed || generation != coverageGeneration)
                {
                    return;
                }

                coverage = next;
                InvalidatePeriod();
                Render();
            });
        });
    }

    /// <summary>
    /// The ledger scope behind the selected provider, or NULL when the provider has no ledger at
    /// all and its history comes only from the 30-day scan.
    /// </summary>
    /// <remarks>
    /// Grok is that provider. Null rather than a scope-that-happens-to-be-empty: the emptiness was
    /// load-bearing but unenforced, and a scope is also what the mapping below can get WRONG. When
    /// this was a two-way expression, Grok fell through to Claude and the picker painted Anthropic
    /// models under Grok - so every arm is now explicit and the absence of a ledger is a value.
    /// </remarks>
    private UsageLedgerScope? LedgerScope =>
        ProviderKeys.ProviderOf(selectedProviderKey) switch
        {
            UsageProvider.Claude => UsageLedgerScope.Claude,
            UsageProvider.Grok => null,
            _ => UsageLedgerScope.Codex
        };

    private void InvalidatePeriod()
    {
        period = null;
        plottedKey = string.Empty;
        plottedScopeKey = string.Empty;
    }

    private void Render()
    {
        // The single choke point every data path funnels through, so it carries the guard even
        // though its callers do too: a scan started for this window keeps running after the
        // window is gone, and its results are marshalled through the dispatcher - so a render
        // request can be queued BEFORE the close and delivered after it.
        if (isClosed)
        {
            return;
        }

        RebaseAcrossMidnight();

        var result = service.GetHistory(selectedProviderKey);
        var refreshing = service.IsRefreshing;

        // In place, as in the flyout: the refresh glyph becomes the ring, so nothing in the header
        // moves and there is no second spinner anywhere in the chrome.
        RefreshSpinner.IsActive = refreshing;
        RefreshSpinner.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
        RefreshGlyph.Visibility = refreshing ? Visibility.Collapsed : Visibility.Visible;

        if (result.Insights is not { } insights)
        {
            plottedInsights = null;
            period = null;

            // No numbers at all yet: either the first scan is running or it failed outright.
            RenderStatus(
                refreshing ? "Scanning local sessions…" : "No usage history loaded yet",
                isStale: false);
            RenderError(result.Error, ErrorKind.NoData);
            RenderTimeline(insights: null, hasData: false);
            RenderToday(insights: null, refreshing);
            SetAllMetrics(refreshing ? "…" : "--");
            dailySeries.Clear();
            dailySlots.Clear();
            dailyShape = string.Empty;
            ShowChartOverlay(
                refreshing ? "Scanning local sessions…" : "No history yet",
                busy: refreshing);
            ShowModelEmpty(
                refreshing ? "Scanning local sessions…" : "No model breakdown yet",
                refreshing ? string.Empty : "Refresh to scan your local session logs.",
                busy: refreshing);
            return;
        }

        if (period is null || !ReferenceEquals(plottedInsights, insights))
        {
            period = BuildPeriod(insights);
        }

        var source = string.IsNullOrWhiteSpace(insights.Source) ? "Local estimates" : insights.Source;
        var freshness = result.IsStale
            ? $"Showing data from {FormatObservedAt(insights.ObservedAt)} — the last refresh failed"
            : refreshing
                ? $"{source}  ·  refreshing…"
                : $"{source}  ·  updated {FormatObservedAt(insights.ObservedAt)}";

        // The pricing gap is stated rather than hidden: a row that cannot be priced still shows its
        // tokens, and a spend view that quietly under-reports is the one failure mode it must not
        // have. See LedgerPricing for which models the shared catalog cannot answer for.
        RenderStatus(
            period.HasIncompleteCost ? $"{freshness}  ·  some models could not be priced" : freshness,
            result.IsStale);
        RenderError(
            result.Error,
            result.IsStale ? ErrorKind.Stale : ErrorKind.Incomplete);

        RenderTimeline(insights, hasData: true);
        RenderToday(insights, refreshing);
        RenderMetrics(period);

        // The heading follows the period rather than only the granularity, because a Day period the
        // ledger cannot answer for is plotted from the scan and therefore is NOT hourly.
        ChartHeadingText.Text = period.HourlyDetailMissing
            ? "Estimated spend (hourly detail not recorded yet)"
            : $"Estimated spend by {GraphsPeriod.BucketNoun(granularity)}";

        // The chart is the expensive, animated part, and most renders bring numbers that are
        // already on screen (the refreshing flag alone accounts for two renders per refresh, both
        // carrying the same insights object). Re-plotting is what produced the double entrance
        // animation, so a render that changes nothing stops before it.
        var key = PlotKey(insights);
        var scopeKey = key + "\u0001" + (selectedBucket?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "*");
        var replot = key != plottedKey;
        var rescope = scopeKey != plottedScopeKey;

        plottedInsights = insights;
        plottedKey = key;
        plottedScopeKey = scopeKey;

        if (replot || rescope)
        {
            // BEFORE either consumer, and from the whole period rather than from whatever is about
            // to be drawn, so the chart and the rows agree on every label's colour.
            RebuildNudgeSteps(period);
        }

        if (replot)
        {
            RenderDailyChart(period);
        }

        if (replot || rescope)
        {
            RenderModelRows(period);
            UpdateSelectionBand();
        }

        if (!replot && !rescope)
        {
            return;
        }

        // The settings page has no way to reach this data itself - the history gate means it may
        // never have been built in that session - so the labels are remembered as they are drawn.
        // Taken from the slots and the rows, not from the insights, so the catalog holds exactly
        // the strings ForCategory is keyed on. "other" is excluded: it is a pool, not a model, and
        // so is the overflow row.
        ChartCategoryCatalog.Merge(
            dailySlots.Select(slot => slot.ColorKey)
                .Concat(modelRows.Where(row => !row.IsOverflow).Select(row => row.Key))
                .OfType<string>()
                .Where(label => !string.Equals(label, "other", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(label => (label, drawnColors.GetValueOrDefault(label))));
    }

    /// <summary>
    /// Moves the window onto the new day when it has been left open across local midnight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="anchor"/> is initialised once and was never re-based, while "today" is recomputed
    /// per render - so a window left open overnight silently relabelled its Day view "Yesterday" and
    /// grew a jump-to-now button, with no data change to explain either.
    /// </para>
    /// <para>
    /// The anchor is only dragged along when it WAS today and the user is in Day view; anywhere else
    /// the anchor is a place the user chose to be and moving it would be the window navigating
    /// itself. Everything else is invalidation.
    /// </para>
    /// </remarks>
    private void RebaseAcrossMidnight()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == renderedDay)
        {
            return;
        }

        if (granularity == UsageLedgerGranularity.Day && anchor == renderedDay)
        {
            anchor = today;
            selectedBucket = null;
        }

        renderedDay = today;
        InvalidatePeriod();
    }

    // ---------------------------------------------------------------- today

    /// <summary>
    /// The permanent "what have I spent today" line, INDEPENDENT of granularity, anchor and
    /// selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The redesign replaced two fixed summary cards with a period-scoped metric row and left today
    /// reachable only by switching to Day view and being anchored on today - three interactions and
    /// a mode change for the single most-wanted number in a usage tray app. This bar is the fixed
    /// reference point that makes the rest of the window safe to wander around in, so it is NEVER
    /// greyed, hidden or annotated when the browsed period does not contain today; its stability is
    /// the whole point.
    /// </para>
    /// <para>
    /// ZERO IDLE COST is unaffected: this is a read of the cache <see cref="RefreshCoverage"/>
    /// already warmed, on the render path, behind the same window-open history gate as everything
    /// else. No second Task.Run, no timer, and nothing here contributes to the plot or shape keys -
    /// a today refresh must never rebuild the chart.
    /// </para>
    /// </remarks>
    private void RenderToday(ProviderUsageInsights? insights, bool refreshing)
    {
        if (insights is null)
        {
            TodayValueText.Text = refreshing ? "…" : "--";
            TodayDetailText.Text = refreshing ? "Scanning local sessions…" : string.Empty;
            TodayCompareText.Text = string.Empty;
            ToolTipService.SetToolTip(TodayCard, "Show today in detail");
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var (cost, tokens, models) = ReadDay(today, insights);
        var (previousCost, _, _) = ReadDay(today.AddDays(-1), insights);

        TodayValueText.Text = FormatUsd(cost);

        var detail = tokens > 0 || cost > 0
            ? $"{FormatTokens(tokens)}  ·  {models} {Plural("model", models)}"
            // $0.00 and not "—": zero IS the answer to "what did I spend today", and the user asked
            // for the answer rather than for the absence of one.
            : "No usage recorded today";
        TodayDetailText.Text = detail;

        var compare = previousCost > 0
            ? $"vs yesterday {FormatSignedPercent(cost, previousCost)}"
            : cost > 0
                ? "vs yesterday  ·  new"
                : string.Empty;
        TodayCompareText.Text = compare;

        ToolTipService.SetToolTip(
            TodayCard,
            string.Join(
                Environment.NewLine,
                $"Today  ·  {FormatUsd(cost)}",
                detail,
                string.IsNullOrEmpty(compare) ? $"Yesterday  ·  {FormatUsd(previousCost)}" : compare,
                $"Local estimates  ·  updated {FormatObservedAt(insights.ObservedAt)}"));
    }

    /// <summary>
    /// One local day's spend, tokens and model count, under the SAME one-source rule
    /// <see cref="BuildPeriod"/> uses: the ledger when it can price the day, otherwise the scan's
    /// row for it - never a sum of the two. Today is always inside the 30-day scan window, so the
    /// scan can answer even on a cold install.
    /// </summary>
    private (decimal Cost, long Tokens, int Models) ReadDay(DateOnly day, ProviderUsageInsights insights)
    {
        var scope = LedgerScope;
        var series = QueryRange(scope, day, day, UsageLedgerGranularity.Day, PricingFor(scope));
        var buckets = (IReadOnlyList<UsageLedgerBucket>)series.Buckets;

        if (!series.HasPriceableData && BuildFallbackBuckets(buckets, insights) is { } fallback)
        {
            buckets = fallback;
        }

        var bucket = buckets.Count > 0 ? buckets[0] : null;
        if (bucket is null)
        {
            return (0m, 0, 0);
        }

        return (
            bucket.EstimatedCostUsd,
            bucket.TotalTokens,
            bucket.Models.Count(model => model.EstimatedCostUsd > 0 || model.TotalTokens > 0));
    }

    /// <summary>
    /// Identity of the PLOTTED data: provider, period and the insights instance the ledger was read
    /// alongside. A refresh that lands the same numbers on the same period changes nothing.
    /// </summary>
    private string PlotKey(ProviderUsageInsights insights) =>
        string.Join(
            '\u0001',
            selectedProviderKey,
            granularity.ToString(),
            anchor.DayNumber.ToString(CultureInfo.InvariantCulture),
            insights.ObservedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));

    // ---------------------------------------------------------------- period data

    /// <summary>
    /// Everything the four metrics, the chart and the rows need about one period, resolved once per
    /// data change rather than per render.
    /// </summary>
    /// <param name="ElapsedFraction">
    /// Elapsed buckets counting the in-progress one FRACTIONALLY - the divisor for "average per
    /// bucket" and the base of the projection. See <see cref="GraphsPeriod.Elapsed"/>.
    /// </param>
    /// <param name="ElapsedBuckets">The same span in whole buckets, for the "over N days" line.</param>
    /// <param name="CoverageInsidePeriod">
    /// True when recording BEGAN inside this period, i.e. the period is partial for a reason no
    /// amount of waiting will fix. Suppresses the projection: extrapolating a full year from the
    /// five months that happen to be recorded is a number with no meaning.
    /// </param>
    /// <param name="HourlyDetailMissing">
    /// True when a Day period had to be answered by the scan, which reports days and cannot split
    /// one into hours. The chart then carries ONE column for the whole day and says so, rather than
    /// fabricating a distribution or going blank.
    /// </param>
    private sealed record PeriodView(
        UsageLedgerGranularity Granularity,
        UsageLedgerGranularity Bucket,
        DateOnly Start,
        DateOnly EndInclusive,
        IReadOnlyList<UsageLedgerBucket> Buckets,
        IReadOnlyList<ProviderModelUsage> Models,
        decimal Cost,
        decimal FastCost,
        long Tokens,
        bool HasIncompleteCost,
        bool HasData,
        decimal PreviousCost,
        bool PreviousCovered,
        double ElapsedFraction,
        int ElapsedBuckets,
        bool CurrentBucketInProgress,
        int TotalUnits,
        bool IsCurrent,
        bool CoverageInsidePeriod,
        bool HourlyDetailMissing);

    private PeriodView BuildPeriod(ProviderUsageInsights insights)
    {
        var (start, end) = GraphsPeriod.Bounds(granularity, anchor);
        var bucket = GraphsPeriod.BucketOf(granularity);
        var scope = LedgerScope;
        var pricing = PricingFor(scope);

        var series = QueryRange(scope, start, end, bucket, pricing);
        var buckets = (IReadOnlyList<UsageLedgerBucket>)series.Buckets;
        var models = series.Models;
        var cost = series.TotalEstimatedCostUsd;
        var fast = series.TotalFastEstimatedCostUsd;
        var tokens = series.TotalTokens;
        var incomplete = series.HasIncompleteCost;
        var hasData = buckets.Any(item => item.TotalTokens > 0 || item.EstimatedCostUsd > 0);

        // ONE source per period. The ledger wins whenever it has anything ACCOUNTABLE for the
        // period, because it is the only source with hour buckets and a per-bucket model split; the
        // scan's daily rows stand in only while the ledger is still cold (its merge lands
        // asynchronously after the first scan of a fresh install).
        //
        // "Accountable" and not "has data": a period whose ledger holds nothing but a model this
        // build cannot price is all tokens and no money, and gating on tokens alone let that period
        // suppress the scan fallback - which DOES carry the right cost - and report the user's spend
        // as $0.00 for as long as the model stayed unknown. Priceable data is what makes the
        // ledger's money an answer; free usage (a model whose published rate is 0.00) counts as
        // priceable, so genuinely-zero spend keeps the ledger and is not mistaken for this.
        //
        // HOUR BUCKETS TAKE THE FALLBACK TOO, which is the one case this deliberately excluded. The
        // exclusion made Day view - the only place "today" existed - render "No usage recorded yet"
        // on a cold ledger while Month view showed real numbers from the same scan, i.e. exactly the
        // state a new user is in when they first ask what they spent today. The scan cannot split a
        // day into hours, so the whole day becomes ONE column and the heading says so; nothing is
        // fabricated, and BuildFallbackBuckets still REPLACES rather than adds, so no double count.
        var hourlyDetailMissing = false;
        if (!hasData || !series.HasPriceableData)
        {
            var fallback = bucket == UsageLedgerGranularity.Hour
                ? BuildWholeDayFallback(buckets, insights)
                : BuildFallbackBuckets(buckets, insights);
            if (fallback is not null)
            {
                hourlyDetailMissing = bucket == UsageLedgerGranularity.Hour;
                buckets = fallback;
                models = AggregateModels(buckets, insights, start, end);
                cost = buckets.Sum(item => item.EstimatedCostUsd);
                fast = buckets.Sum(item => item.FastEstimatedCostUsd);
                tokens = buckets.Sum(item => item.TotalTokens);
                // The fallback only fills slots the scan can answer for; slots outside its 30-day
                // window keep the LEDGER's bucket, so the period can still contain an unpriceable
                // model even after the swap. Both sources therefore get a vote on "partial".
                incomplete = insights.HasIncompleteCost || buckets.Any(item => item.HasIncompleteCost);
                hasData = buckets.Any(item => item.TotalTokens > 0 || item.EstimatedCostUsd > 0);
            }
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var now = DateTimeOffset.Now;
        var isCurrent = start <= today && today <= end;
        var floor = CoverageStart(insights);

        var elapsed = GraphsPeriod.Elapsed(
            buckets.Select(item => (item.StartLocal, item.EndLocalExclusive)).ToArray(),
            now,
            floor is { } floorDay ? new DateTimeOffset(floorDay.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(floorDay.ToDateTime(TimeOnly.MinValue))) : null);

        var previousAnchor = GraphsPeriod.Shift(granularity, anchor, -1);
        var (previousStart, previousEnd) = GraphsPeriod.Bounds(granularity, previousAnchor);
        var previousSeries = QueryRange(scope, previousStart, previousEnd, bucket, pricing);
        var previousBuckets = (IReadOnlyList<UsageLedgerBucket>)previousSeries.Buckets;
        if (!previousBuckets.Any(item => item.EstimatedCostUsd > 0))
        {
            // The SAME gate as the current period's, deliberately: change one site and the Day
            // view's "vs previous" cell disagrees with its own chart about where the numbers came
            // from.
            previousBuckets = (bucket == UsageLedgerGranularity.Hour
                ? BuildWholeDayFallback(previousBuckets, insights)
                : BuildFallbackBuckets(previousBuckets, insights)) ?? previousBuckets;
        }

        // LIKE FOR LIKE: three days of July against all of June would show a permanent, meaningless
        // -90%, so a period still running is compared against the same number of elapsed buckets of
        // the one before it. WHOLE buckets here - "the same point in June" is a day boundary, and a
        // fractional take is not a thing you can slice a bucket list with.
        var previousCost = isCurrent
            ? previousBuckets.Take(elapsed.Buckets).Sum(item => item.EstimatedCostUsd)
            : previousBuckets.Sum(item => item.EstimatedCostUsd);

        return new PeriodView(
            granularity,
            bucket,
            start,
            end,
            buckets,
            models,
            cost,
            fast,
            tokens,
            incomplete,
            hasData,
            previousCost,
            PreviousCovered: floor is not { } coverageFloor || previousEnd >= coverageFloor,
            elapsed.Fraction,
            elapsed.Buckets,
            elapsed.CurrentInProgress,
            buckets.Count,
            isCurrent,
            CoverageInsidePeriod: floor is { } inside && inside > start,
            hourlyDetailMissing);
    }

    /// <summary>
    /// The whole period as ONE bucket filled from the scan - the Day view's fallback when the ledger
    /// has no hours to give.
    /// </summary>
    /// <remarks>
    /// Built by handing <see cref="BuildFallbackBuckets"/> a single slot spanning the ledger's own
    /// bucket bounds, so the matching rule, the category grouping and above all the "REPLACES, never
    /// adds" contract are the scan fallback's and not a second implementation of them.
    /// </remarks>
    private static IReadOnlyList<UsageLedgerBucket>? BuildWholeDayFallback(
        IReadOnlyList<UsageLedgerBucket> bounds,
        ProviderUsageInsights insights)
    {
        if (bounds.Count == 0)
        {
            return null;
        }

        var span = new UsageLedgerBucket(
            bounds[0].StartLocal,
            bounds[^1].EndLocalExclusive,
            0, 0, 0, 0, 0m, 0m, 0, [], [], false);

        return BuildFallbackBuckets([span], insights);
    }

    /// <summary>Pricing for a scope, or null for a scan-only provider that has none.</summary>
    private static UsageLedgerPricing? PricingFor(UsageLedgerScope? scope) =>
        scope is { } ledgerScope ? LedgerPricing.For(ledgerScope) : null;

    private static UsageLedgerSeries QueryRange(
        UsageLedgerScope? scope,
        DateOnly start,
        DateOnly endInclusive,
        UsageLedgerGranularity bucket,
        UsageLedgerPricing? pricing)
    {
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(start.ToDateTime(TimeOnly.MinValue)));
        var toDate = endInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var to = new DateTimeOffset(toDate, TimeZoneInfo.Local.GetUtcOffset(toDate));

        // A scan-only provider gets the bucket partition and nothing else, so every downstream
        // "the ledger had nothing priceable, use the scan" branch takes itself without a scope
        // having to exist and stay empty for it.
        return scope is { } ledgerScope
            ? UsageLedger.Query(ledgerScope, from, to, bucket, TimeZoneInfo.Local, pricing)
            : UsageLedger.EmptyRange(from, to, bucket, TimeZoneInfo.Local);
    }

    /// <summary>
    /// Fills the ledger's (empty but correctly bounded) buckets from the scan's daily rows.
    /// </summary>
    /// <remarks>
    /// Reusing the ledger's bucket bounds rather than deriving a second set is what keeps the two
    /// sources interchangeable: the chart, the metrics and the drill-down all address a bucket by
    /// its local start, whichever source filled it. Per-model TOKENS are unknowable here - the scan
    /// only reports a per-day category/cost split - so the rows fall back to cost only.
    /// </remarks>
    private static IReadOnlyList<UsageLedgerBucket>? BuildFallbackBuckets(
        IReadOnlyList<UsageLedgerBucket> bounds,
        ProviderUsageInsights insights)
    {
        if (bounds.Count == 0 || insights.Daily.Count == 0)
        {
            return null;
        }

        var filled = new List<UsageLedgerBucket>(bounds.Count);
        var any = false;

        foreach (var slot in bounds)
        {
            var days = insights.Daily
                .Where(day =>
                {
                    var at = day.Day.ToDateTime(TimeOnly.MinValue);
                    return at >= slot.StartLocal.DateTime && at < slot.EndLocalExclusive.DateTime;
                })
                .ToArray();

            if (days.Length == 0)
            {
                filled.Add(slot);
                continue;
            }

            any = true;
            var categories = days
                .SelectMany(day => day.Categories)
                .Where(category => category.EstimatedCostUsd > 0)
                .GroupBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ProviderSpendCategory(group.Key, group.Sum(category => category.EstimatedCostUsd)))
                .OrderBy(category => category.Label.Contains(" fast", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            filled.Add(new UsageLedgerBucket(
                slot.StartLocal,
                slot.EndLocalExclusive,
                days.Sum(day => day.InputTokens),
                days.Sum(day => day.CachedInputTokens),
                days.Sum(day => day.CacheCreationTokens),
                days.Sum(day => day.OutputTokens),
                days.Sum(day => day.EstimatedCostUsd),
                days.Sum(day => day.FastEstimatedCostUsd),
                Requests: 0,
                categories
                    .Select(category => new ProviderModelUsage(
                        category.Label,
                        0,
                        0,
                        0,
                        0,
                        category.EstimatedCostUsd,
                        category.Label.Contains(" fast", StringComparison.OrdinalIgnoreCase) ? category.EstimatedCostUsd : 0))
                    .ToArray(),
                categories,
                days.Any(day => day.HasIncompleteCost)));
        }

        return any ? filled : null;
    }

    /// <summary>
    /// The period's model breakdown when the buckets came from the scan. The scan's own
    /// <c>Models</c> list carries tokens and is used whenever the period contains its whole window;
    /// otherwise the split is rebuilt from the per-day categories, which is cost only.
    /// </summary>
    private static IReadOnlyList<ProviderModelUsage> AggregateModels(
        IReadOnlyList<UsageLedgerBucket> buckets,
        ProviderUsageInsights insights,
        DateOnly start,
        DateOnly endInclusive)
    {
        var covered = insights.Daily.Count > 0 &&
            insights.Daily.Min(day => day.Day) >= start &&
            insights.Daily.Max(day => day.Day) <= endInclusive;

        if (covered)
        {
            return insights.Models;
        }

        return buckets
            .SelectMany(bucket => bucket.Models)
            .GroupBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProviderModelUsage(
                group.Key,
                group.Sum(model => model.InputTokens),
                group.Sum(model => model.CachedInputTokens),
                group.Sum(model => model.CacheCreationTokens),
                group.Sum(model => model.OutputTokens),
                group.Sum(model => model.EstimatedCostUsd),
                group.Sum(model => model.FastEstimatedCostUsd),
                group.Any(model => model.HasIncompleteCost)))
            .OrderByDescending(model => model.EstimatedCostUsd)
            .ToArray();
    }

    /// <summary>
    /// The earliest day any source can answer for - the back arrow's floor. The two providers do
    /// not share one, and the ledger can reach months further back than the scan does.
    /// </summary>
    private DateOnly? CoverageStart(ProviderUsageInsights? insights)
    {
        DateOnly? floor = coverage.FirstUsageUtc is { } first
            ? DateOnly.FromDateTime(first.ToLocalTime().DateTime)
            : coverage.FirstRecordedDay;

        if (insights is { Daily.Count: > 0 })
        {
            var scanned = insights.Daily.Min(day => day.Day);
            floor = floor is { } value && value <= scanned ? value : scanned;
        }

        return floor;
    }

    // ---------------------------------------------------------------- timeline

    private void RenderTimeline(ProviderUsageInsights? insights, bool hasData)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var (start, end) = GraphsPeriod.Bounds(granularity, anchor);

        PeriodLabelText.Text = GraphsPeriod.Label(granularity, anchor, today);
        ToolTipService.SetToolTip(PeriodLabelText, GraphsPeriod.RangeTooltip(granularity, anchor));

        // The ledger only reaches back as far as it has been fed, and nothing has fed it beyond the
        // 30-day scan window until an import runs - so the offer to import is what the disabled
        // back arrow points at.
        var importable = coverage.FirstRecordedDay is not { } recorded || recorded > today.AddDays(-45);
        ImportHistoryLink.Visibility = importable ? Visibility.Visible : Visibility.Collapsed;

        var floor = CoverageStart(insights);
        var noun = GraphsPeriod.Noun(granularity);
        var canGoBack = hasData && GraphsPeriod.CanGoBack(granularity, anchor, floor);
        var isCurrent = start <= today && today <= end;

        PrevPeriodButton.IsEnabled = canGoBack;
        ToolTipService.SetToolTip(
            PrevPeriodButton,
            canGoBack
                ? $"Previous {noun}"
                : importable
                    // The noun follows the granularity: the arrow the user just found disabled is
                    // the YEAR arrow when they are in Year view, and offering them "earlier months"
                    // answers a question they did not ask.
                    ? $"Import history in Settings to see earlier {Plural(noun, 2)}"
                    : "No earlier data");

        NextPeriodButton.IsEnabled = !isCurrent;
        ToolTipService.SetToolTip(
            NextPeriodButton,
            isCurrent ? "Already at the current period" : $"Next {noun}");

        JumpToNowButton.Visibility = isCurrent ? Visibility.Collapsed : Visibility.Visible;

        // The heading is set from the PERIOD in Render - it depends on which source answered, not
        // only on the granularity - but a render with no insights never builds one, so the
        // granularity's own wording is the floor.
        ChartHeadingText.Text = $"Estimated spend by {GraphsPeriod.BucketNoun(granularity)}";
    }

    // ---------------------------------------------------------------- metrics

    private void SetAllMetrics(string value)
    {
        SetMetric(TotalValueText, TotalDetailText, value, string.Empty);
        SetMetric(DeltaValueText, DeltaDetailText, value, string.Empty);
        SetMetric(AverageValueText, AverageDetailText, value, string.Empty);
        SetMetric(OutlookValueText, OutlookDetailText, value, string.Empty);
    }

    private void RenderMetrics(PeriodView view)
    {
        SetMetric(
            TotalValueText,
            TotalDetailText,
            FormatUsd(view.Cost),
            MetricDetail(view.Tokens, view.FastCost));

        DeltaLabelText.Text = $"vs previous {GraphsPeriod.Noun(view.Granularity)}";
        RenderDelta(view);

        var bucketNoun = GraphsPeriod.BucketNoun(view.Granularity);
        AverageLabelText.Text = $"Average / {bucketNoun}";
        SetMetric(
            AverageValueText,
            AverageDetailText,
            // FRACTIONAL elapsed: at 09:00 today is a fifth of a day, and counting it whole diluted
            // the average by the four fifths that have not happened yet. The DETAIL still counts
            // whole buckets, because "over 5.4 days" is not something a person says - it names the
            // in-progress one instead.
            FormatUsd(view.Cost / (decimal)view.ElapsedFraction),
            view.CurrentBucketInProgress
                ? $"over {view.ElapsedBuckets} {Plural(bucketNoun, view.ElapsedBuckets)}  ·  this {bucketNoun} in progress"
                : $"over {view.ElapsedBuckets} {Plural(bucketNoun, view.ElapsedBuckets)}");

        RenderOutlook(view);
    }

    private void RenderDelta(PeriodView view)
    {
        var previousLabel = GraphsPeriod.PreviousShortLabel(view.Granularity, anchor);

        if (!view.PreviousCovered)
        {
            SetMetric(DeltaValueText, DeltaDetailText, "—", "No earlier data");
            return;
        }

        if (view.PreviousCost <= 0)
        {
            SetMetric(
                DeltaValueText,
                DeltaDetailText,
                view.Cost > 0 ? "New" : "—",
                view.Cost > 0 ? $"Nothing recorded in {previousLabel}" : "No spend either period");
            return;
        }

        var difference = view.Cost - view.PreviousCost;
        var sign = difference >= 0 ? "+" : "-";
        var detail = view.IsCurrent
            ? $"{sign}{FormatUsd(Math.Abs(difference))} vs same point in {previousLabel}"
            : $"{sign}{FormatUsd(Math.Abs(difference))} vs {previousLabel}";

        // Deliberately NOT coloured. Spending more than last month is a fact, not an error, and a
        // red number here would read as a failure the user has to act on.
        SetMetric(DeltaValueText, DeltaDetailText, FormatSignedPercent(view.Cost, view.PreviousCost), detail);
    }

    private void RenderOutlook(PeriodView view)
    {
        if (!view.HasData)
        {
            OutlookLabelText.Text = view.IsCurrent ? "Projected" : $"Peak {GraphsPeriod.BucketNoun(view.Granularity)}";
            SetMetric(OutlookValueText, OutlookDetailText, "—", string.Empty);
            return;
        }

        // The third clause is what keeps Year honest: with the coverage floor INSIDE the period the
        // elapsed span is short because recording started late, not because the period is young, so
        // scaling it up to a full year invents eight months of history. The cell falls through to
        // the peak instead, which a partial period genuinely has.
        if (view.IsCurrent && view.ElapsedFraction < view.TotalUnits && !view.CoverageInsidePeriod)
        {
            OutlookLabelText.Text = "Projected";
            SetMetric(
                OutlookValueText,
                OutlookDetailText,
                FormatUsd(view.Cost / (decimal)view.ElapsedFraction * view.TotalUnits),
                GraphsPeriod.ProjectionTarget(view.Granularity, anchor));
            return;
        }

        // A complete period has nothing left to project, so the slot carries the fact a completed
        // period actually has: where its spending peaked.
        var peak = view.Buckets.OrderByDescending(bucket => bucket.EstimatedCostUsd).First();
        OutlookLabelText.Text = $"Peak {GraphsPeriod.BucketNoun(view.Granularity)}";
        SetMetric(
            OutlookValueText,
            OutlookDetailText,
            FormatUsd(peak.EstimatedCostUsd),
            GraphsPeriod.BucketLabel(
                view.Granularity,
                peak.StartLocal.DateTime,
                peak.EndLocalExclusive - peak.StartLocal));
    }

    private static string Plural(string noun, int count) => count == 1 ? noun : noun + "s";

    /// <summary>
    /// The one status line at the bottom of the window: freshness and source, nothing else. The
    /// glyph appears only when the numbers are stale, which is the flyout's rule - an icon that is
    /// always there stops meaning anything.
    /// </summary>
    private void RenderStatus(string text, bool isStale)
    {
        statusFullText = text;
        StatusText.Text = text;
        ToolTipService.SetToolTip(StatusText, text);
        StatusIcon.Visibility = isStale ? Visibility.Visible : Visibility.Collapsed;

        if (isStale)
        {
            // Built from the palette, NOT from Application.Current.Resources: a brush pulled out
            // of the app resources resolves against the APP theme and renders wrong the moment
            // the user forces the opposite theme. The palette follows the element's ActualTheme
            // and is rebuilt on ActualThemeChanged.
            var warning = BrushFrom(palette.Warning);
            StatusText.Foreground = warning;
            StatusIcon.Foreground = warning;
        }
        else
        {
            // Cleared so TertiaryCaptionStyle's {ThemeResource} setter takes over again - which is
            // why the muted colour is on the style rather than inline.
            StatusText.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    /// <summary>
    /// A brush from a palette colour. The palette is resolved against this window's ActualTheme
    /// and rebuilt on every theme change, so unlike a brush read out of the app resources these
    /// are correct under a forced theme and follow a live system flip.
    /// </summary>
    /// <remarks>
    /// CACHED by ARGB, because <see cref="ModelRowModel"/>'s change guard compares Brush references:
    /// a fresh instance for an unchanged colour raises PropertyChanged and re-animates the meter on
    /// every render. The cache is keyed on the colour rather than on the label, so it survives a
    /// palette rebuild and never returns the wrong theme's brush.
    /// </remarks>
    private Microsoft.UI.Xaml.Media.SolidColorBrush BrushFrom(SKColor color)
    {
        var key = (uint)color;
        if (brushCache.TryGetValue(key, out var brush))
        {
            return brush;
        }

        brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
        brushCache[key] = brush;
        return brush;
    }

    /// <summary>What the message accompanying an error result actually means for the numbers.</summary>
    private enum ErrorKind
    {
        /// <summary>Nothing is on screen - the lookup produced no insights at all.</summary>
        NoData,

        /// <summary>Older insights are being shown because the last refresh failed.</summary>
        Stale,

        /// <summary>Fresh insights, but the reader wants to qualify them.</summary>
        Incomplete
    }

    /// <summary>
    /// Shows an error with its FULL text - wrapped, never ellipsised, repeated in the tooltip,
    /// selectable and copyable. Inline caption text rather than an InfoBar, matching the flyout:
    /// the banner shoved the charts down every time it opened, and its heading is not worth a
    /// whole row when it can lead the sentence.
    /// </summary>
    private void RenderError(string? error, ErrorKind kind)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            errorFullText = string.Empty;
            ErrorPanel.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(ErrorText, null);
            return;
        }

        var heading = kind switch
        {
            ErrorKind.NoData => "Usage history unavailable",
            ErrorKind.Stale => "Usage history may be out of date",
            _ => "Usage history is incomplete"
        };

        errorFullText = $"{heading}  ·  {error}";
        ErrorText.Text = errorFullText;
        ToolTipService.SetToolTip(ErrorText, errorFullText);

        // Numbers on screen plus a failed refresh is a warning, not a dead end; nothing at all
        // is the error case. Same severity split the InfoBar carried, same two palette colours the
        // flyout uses for it.
        var brush = BrushFrom(kind == ErrorKind.NoData ? palette.Danger : palette.Warning);
        ErrorText.Foreground = brush;
        ErrorIcon.Foreground = brush;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void OnCopyError(object sender, RoutedEventArgs e) => CopyToClipboard(errorFullText);

    /// <summary>
    /// Ctrl+C on the focused error row. The row is the only diagnostic this window gives, and
    /// after the InfoBar went its focusable action button went with it - leaving Copy on a
    /// right-click flyout, which a keyboard user cannot reach at all.
    /// </summary>
    private void OnCopyErrorAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CopyToClipboard(errorFullText);
    }

    private void OnCopyStatus(object sender, RoutedEventArgs e) => CopyToClipboard(statusFullText);

    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => service.Refresh();

    /// <summary>
    /// The figure and the detail beside it. The detail ellipsises when the strip is narrow, so it
    /// always keeps its full text on the tooltip.
    /// </summary>
    private static void SetMetric(TextBlock valueText, TextBlock detailText, string value, string detail)
    {
        valueText.Text = value;
        detailText.Text = detail;
        ToolTipService.SetToolTip(detailText, string.IsNullOrEmpty(detail) ? null : detail);
    }

    // ---------------------------------------------------------------- daily chart

    /// <summary>
    /// The chart: one stacked column per bucket, split by spend category. The category set, the
    /// stack order (regular categories first, then the "fast" ones, alphabetical within each) and
    /// the colours are the WinForms rules; LiveCharts draws them.
    /// </summary>
    private void RenderDailyChart(PeriodView view)
    {
        var buckets = view.Buckets;
        if (buckets.Count == 0 || buckets.All(bucket => bucket.EstimatedCostUsd <= 0))
        {
            dailySeries.Clear();
            dailySlots.Clear();
            dailyShape = string.Empty;
            ShowChartOverlay(
                view.HasData
                    ? "No spend recorded in this period"
                    : plottedInsights?.HasUsage == true
                        ? $"No spend recorded in {GraphsPeriod.Label(granularity, anchor, DateOnly.FromDateTime(DateTime.Now))}"
                        : "No usage recorded yet",
                busy: false);
            return;
        }

        HideChartOverlay();

        // Every distinct category, biggest spender first, with the tail pooled so the legend
        // stays readable on an account that has used a dozen models.
        var totals = buckets
            .SelectMany(BucketSpendCategories)
            .GroupBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Label = group.Key, Total = group.Sum(category => category.EstimatedCostUsd) })
            .OrderByDescending(item => item.Total)
            .ToArray();

        var kept = totals.Take(MaxDailySeries).Select(item => item.Label).ToArray();
        var pooled = totals.Skip(MaxDailySeries).Select(item => item.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Stack order matches the WinForms bar: regular categories at the bottom, "fast" above,
        // alphabetical within each group, with the pooled remainder on top.
        var ordered = kept
            .OrderBy(label => label.Contains(" fast", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The plan is the SHAPE of the chart: which series exist, what they are called, and where
        // each one's numbers come from. Two renders with the same plan reuse the same series
        // objects and only move their values.
        var plan = new List<DailyPlanEntry>();

        foreach (var label in ordered)
        {
            var captured = label;
            plan.Add(new DailyPlanEntry(
                captured,
                ShortSpendLabel(captured),
                bucket => BucketSpendCategories(bucket)
                    .Where(category => string.Equals(category.Label, captured, StringComparison.OrdinalIgnoreCase))
                    .Sum(category => category.EstimatedCostUsd)));
        }

        if (pooled.Count > 0)
        {
            plan.Add(new DailyPlanEntry(
                "other",
                $"other ({pooled.Count})",
                bucket => BucketSpendCategories(bucket)
                    .Where(category => pooled.Contains(category.Label))
                    .Sum(category => category.EstimatedCostUsd)));
        }

        // No category split at all (a provider that only reports a bucket total). ColorKey is null
        // so it takes the accent directly, as it always has, rather than being hashed as a label.
        if (plan.Count == 0)
        {
            plan.Add(new DailyPlanEntry(null, "Estimated spend", bucket => bucket.EstimatedCostUsd));
        }

        // Keyed on the RAW colour key as well as the legend name: ShortSpendLabel truncates, so
        // two different models can share a name while needing different colours. The period is part
        // of the key because moving the window moves every point's date.
        var shape = ShapeKey(plan.Select(entry => $"{entry.ColorKey ?? "*"}={entry.Name}"));
        if (shape != dailyShape)
        {
            RebuildDailySeries(plan, shape);
        }

        UpdateDailyValues(plan, buckets);
        ApplyDailyColors();
    }

    /// <summary>One stacked series' identity: its colour key, its legend name and its selector.</summary>
    private sealed record DailyPlanEntry(string? ColorKey, string Name, Func<UsageLedgerBucket, decimal> Selector);

    private sealed record DailySeriesSlot(
        string? ColorKey,
        StackedColumnSeries<DateTimePoint> Series,
        ObservableCollection<DateTimePoint> Values);

    private void RebuildDailySeries(IReadOnlyList<DailyPlanEntry> plan, string shape)
    {
        dailySeries.Clear();
        dailySlots.Clear();
        dailyShape = shape;

        foreach (var entry in plan)
        {
            var values = new ObservableCollection<DateTimePoint>();
            var series = new StackedColumnSeries<DateTimePoint>
            {
                Name = entry.Name,
                Values = values,
                Stroke = null,
                Rx = 2,
                Ry = 2,
                Padding = 2,
                // 44, not 26. The selection band is a full axis unit wide, and at seven columns
                // (Week) a unit is ~110 DIP - so a 26 DIP bar left the band four times wider than
                // the thing it was highlighting even once it was correctly centred. Raising the cap
                // lets a column fill its slot at low column counts, which makes bar and band agree
                // at every granularity. Set HERE, at rebuild time: it is a property on a retained
                // series, and re-assigning it per render would be another live mutation.
                MaxBarWidth = 44,
                // The tooltip already prints the series name in its own column; repeating it here
                // showed every row twice.
                YToolTipLabelFormatter = point => FormatUsd((decimal)point.Coordinate.PrimaryValue)
            };

            dailySlots.Add(new DailySeriesSlot(entry.ColorKey, series, values));
            dailySeries.Add(series);
        }
    }

    private void UpdateDailyValues(IReadOnlyList<DailyPlanEntry> plan, IReadOnlyList<UsageLedgerBucket> buckets)
    {
        for (var index = 0; index < dailySlots.Count && index < plan.Count; index++)
        {
            var values = dailySlots[index].Values;
            var selector = plan[index].Selector;

            // Every series carries one entry PER BUCKET, nulls included: LiveCharts stacks by
            // entity index, so a series that skipped its empty buckets would stack against the
            // wrong dates.
            //
            // Points are only mutated in place while the BUCKET WINDOW ITSELF is unchanged - that
            // is the refresh case, and reusing the points is what lets the chart animate the value
            // transition instead of replaying its entrance. Once the dates shift (an arrow press,
            // or the window left open across local midnight) the points are rebuilt: LiveCharts
            // caches a ChartPoint per point instance, and CoreColumnSeries skips EMPTY points
            // before refreshing either the hover area or the stacked total. A reused point that
            // goes from a value to null would keep the previous bucket's hover rectangle and
            // stacked total, which surfaces as a phantom $0.00 row - and a wrong "Total" - in the
            // tooltip for that column.
            var aligned = values.Count == buckets.Count;
            for (var slot = 0; aligned && slot < buckets.Count; slot++)
            {
                aligned = values[slot].DateTime == buckets[slot].StartLocal.DateTime;
            }

            if (!aligned)
            {
                values.Clear();
                foreach (var bucket in buckets)
                {
                    values.Add(new DateTimePoint(bucket.StartLocal.DateTime, Amount(selector, bucket)));
                }

                continue;
            }

            for (var slot = 0; slot < buckets.Count; slot++)
            {
                var point = values[slot];
                var amount = Amount(selector, buckets[slot]);

                // Assigned only when it actually moved: DateTimePoint raises PropertyChanged on
                // every set, and an unchanged value re-entering the chart is a redundant update.
                if (point.Value != amount)
                {
                    point.Value = amount;
                }
            }
        }

        static double? Amount(Func<UsageLedgerBucket, decimal> selector, UsageLedgerBucket bucket)
        {
            var value = selector(bucket);
            return value > 0 ? (double)value : null;
        }
    }

    private void ApplyDailyColors()
    {
        // Cleared per pass, not accumulated: a stale entry from an earlier render would outlive a
        // theme flip or a colour edit and keep the settings swatch showing a colour nothing is
        // drawn in any more. The chart runs before the model rows, so clearing here reseeds both.
        drawnColors.Clear();

        foreach (var slot in dailySlots)
        {
            var color = slot.ColorKey is null ? palette.Accent : CategoryColor(slot.ColorKey);
            ApplyFill(slot.Series, color);

            RecordDrawnColor(slot.ColorKey, color);
        }
    }

    /// <summary>
    /// Assigns every label in the PERIOD its collision nudge, once, in a fixed order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The union is deliberately wider than what is on screen: every category of every bucket and
    /// every model of every bucket, not just the seven plotted series and the eight visible rows.
    /// That is what makes a label's colour survive a drill-down - the map does not change when the
    /// scope narrows - and what makes the chart's legend and the row meters agree, since both read
    /// the same map instead of each counting collisions in its own iteration order.
    /// </para>
    /// <para>
    /// An OVERRIDDEN label still claims its slot (so an automatic colour that lands on the same hex
    /// is separated from it) but takes no nudge itself, exactly as before: a picked colour is drawn
    /// as picked or the settings page is lying.
    /// </para>
    /// </remarks>
    private void RebuildNudgeSteps(PeriodView view)
    {
        nudgeSteps.Clear();

        var used = new Dictionary<uint, int>();
        var labels = view.Buckets
            .SelectMany(BucketSpendCategories)
            .Select(category => category.Label)
            .Concat(view.Buckets.SelectMany(bucket => bucket.Models).Select(model => model.Model))
            .Concat(view.Models.Select(model => model.Model))
            .Where(label => !string.IsNullOrEmpty(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Ordinal, so the map is the same on every machine and in every session.
            .OrderBy(label => label, StringComparer.Ordinal);

        foreach (var label in labels)
        {
            var key = (uint)palette.ForCategory(label);
            var step = used.TryGetValue(key, out var seen) ? seen : 0;
            used[key] = step + 1;
            nudgeSteps[label] = palette.IsOverridden(label) ? 0 : step;
        }
    }

    /// <summary>
    /// Remembers the hex a category was drawn in, for the settings swatch.
    /// </summary>
    /// <remarks>
    /// OVERRIDDEN categories are deliberately NOT recorded. The settings page uses this only to
    /// preview the AUTOMATIC colour of a row the user has not set, so recording an override would
    /// mean that resetting a colour previews the very colour that was just discarded - the swatch
    /// would never go back to the automatic one until the graphs window was reopened.
    /// </remarks>
    private void RecordDrawnColor(string? colorKey, SKColor color)
    {
        if (colorKey is { } key && !palette.IsOverridden(key))
        {
            drawnColors[key] = ChartPalette.ToHex(color);
        }
    }

    /// <summary>
    /// The hex each category was last drawn in, fed to <see cref="ChartCategoryCatalog"/>.
    /// </summary>
    private readonly Dictionary<string, string> drawnColors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The category colour, nudged when a colour is already taken. The WinForms rules map
    /// several labels onto the same accent, which is invisible in a legend but reads as one
    /// merged block when the segments touch inside a stacked bar.
    /// </summary>
    private SKColor CategoryColor(string label)
    {
        var color = palette.ForCategory(label);

        // A colour the user picked is drawn EXACTLY as picked - it still claims its slot in
        // RebuildNudgeSteps, so an automatic colour that lands on the same value is separated from
        // it, but nudging the pick itself would mean the chart never shows the hex the settings page
        // promises.
        return palette.IsOverridden(label)
            ? color
            : ChartPalette.Nudge(color, nudgeSteps.GetValueOrDefault(label), palette.IsDark);
    }

    /// <summary>
    /// Recolours a retained series. A new <c>SolidColorPaint</c> is what makes the chart notice
    /// (mutating a live paint's colour raises nothing), and swapping a PAINT leaves the series
    /// instance - and therefore its cached point visuals - alone, so a colour change repaints
    /// without replaying the entrance animation.
    /// </summary>
    private static void ApplyFill(IStrokedAndFilled series, SKColor color)
    {
        if (series.Fill is SolidColorPaint existing && existing.Color == color)
        {
            return;
        }

        series.Fill = new SolidColorPaint(color);
    }

    // ---------------------------------------------------------------- drill-down

    /// <summary>
    /// Retargets the model rows to the clicked column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FindingStrategy</c> is <c>CompareOnlyX</c>, so a click anywhere in a stacked column
    /// resolves to every segment of that column and the first point is enough to identify it.
    /// Clicking the already selected column clears the selection, which is the cheapest exit.
    /// </para>
    /// <para>
    /// The METRIC ROW and the chart data deliberately do NOT follow: a bar click is a lens on the
    /// breakdown, not a change of period. If the metrics moved too, the strip and the metrics would
    /// disagree about what "this period" means.
    /// </para>
    /// <para>
    /// Chart points are not focusable, so the KEYBOARD reaches the same selection through
    /// <see cref="OnChartKeyDown"/> on the chart card rather than through the chart itself.
    /// </para>
    /// </remarks>
    private void OnDailyChartDataPointerDown(IChartView chart, IEnumerable<ChartPoint> points)
    {
        if (isClosed || period is null)
        {
            return;
        }

        if (points.FirstOrDefault() is not { } point)
        {
            return;
        }

        var at = new DateTime((long)point.Coordinate.SecondaryValue);
        var match = period.Buckets.FirstOrDefault(bucket =>
            at >= bucket.StartLocal.DateTime && at < bucket.EndLocalExclusive.DateTime);
        if (match is null)
        {
            return;
        }

        selectedBucket = selectedBucket == match.StartLocal.DateTime ? null : match.StartLocal.DateTime;
        Render();
    }

    /// <summary>
    /// Keyboard selection: Left/Right move one bucket, Home/End jump to the ends, Enter drills in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure index arithmetic over <c>period.Buckets</c> - the chart is told nothing and needs to
    /// support nothing. With no selection, Left/Right start from the LAST bucket that has data
    /// rather than from the edge of the period, because the useful end of a month is the end you
    /// are living in.
    /// </para>
    /// <para>
    /// Named, and on the chart CARD: the handler fires from inside the content
    /// <see cref="QuiesceCharts"/> sets to null, so it checks <see cref="isClosed"/> first like
    /// every other entry point here.
    /// </para>
    /// </remarks>
    private void OnChartKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (isClosed || period is null || period.Buckets.Count == 0)
        {
            return;
        }

        var buckets = period.Buckets;
        var current = selectedBucket is { } start
            ? IndexOfBucket(buckets, start)
            : -1;

        int next;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left:
                next = current < 0 ? LastBucketWithData(buckets) : Math.Max(0, current - 1);
                break;

            case Windows.System.VirtualKey.Right:
                next = current < 0 ? LastBucketWithData(buckets) : Math.Min(buckets.Count - 1, current + 1);
                break;

            case Windows.System.VirtualKey.Home:
                next = 0;
                break;

            case Windows.System.VirtualKey.End:
                next = buckets.Count - 1;
                break;

            case Windows.System.VirtualKey.Enter:
                if (selectedBucket is { } drill)
                {
                    e.Handled = true;
                    DrillInto(drill);
                }

                return;

            default:
                return;
        }

        e.Handled = true;
        selectedBucket = buckets[next].StartLocal.DateTime;
        Render();
    }

    private static int IndexOfBucket(IReadOnlyList<UsageLedgerBucket> buckets, DateTime start)
    {
        for (var index = 0; index < buckets.Count; index++)
        {
            if (buckets[index].StartLocal.DateTime == start)
            {
                return index;
            }
        }

        return -1;
    }

    private static int LastBucketWithData(IReadOnlyList<UsageLedgerBucket> buckets)
    {
        for (var index = buckets.Count - 1; index >= 0; index--)
        {
            if (buckets[index].TotalTokens > 0 || buckets[index].EstimatedCostUsd > 0)
            {
                return index;
            }
        }

        return buckets.Count - 1;
    }

    /// <summary>
    /// Double-click (or Enter on a keyboard selection): the clicked COLUMN becomes the period.
    /// </summary>
    /// <remarks>
    /// Year ▸ the clicked month, Month/Week ▸ the clicked day, and Day does not step further. This
    /// is the "move through time without thinking" affordance - single click keeps its old meaning
    /// (retarget the breakdown only), so nothing is taken away. Granularity and anchor move
    /// together, which is a legitimate series rebuild, and the selection is cleared in the same step
    /// so the finer view opens UN-drilled rather than inheriting a bucket that no longer exists.
    /// </remarks>
    private void DrillInto(DateTime bucketStart)
    {
        if (isClosed || !GraphsPeriod.CanStepFiner(granularity))
        {
            return;
        }

        ApplyGranularity(GraphsPeriod.Finer(granularity), DateOnly.FromDateTime(bucketStart));
    }

    /// <summary>
    /// The pointer half of the drill-down. The bucket comes from <see cref="selectedBucket"/>
    /// because the first click of a double-click has already selected it through
    /// <c>DataPointerDown</c> - which is also why a double-click on an already-selected column
    /// (whose first click cleared it) does nothing rather than drilling somewhere unexpected.
    /// </summary>
    private void OnChartDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (isClosed || selectedBucket is not { } start)
        {
            return;
        }

        e.Handled = true;
        DrillInto(start);
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        if (isClosed || selectedBucket is null)
        {
            return;
        }

        selectedBucket = null;
        Render();
    }

    /// <summary>
    /// Esc clears a drill-down and does nothing else.
    /// </summary>
    /// <remarks>
    /// <c>Handled</c> is set ONLY when there was a selection to clear, so an Esc with nothing
    /// selected keeps travelling - but it must never end up closing this window, which is why the
    /// accelerator does not fall through to any dismiss path.
    /// </remarks>
    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (isClosed || selectedBucket is null)
        {
            return;
        }

        args.Handled = true;
        selectedBucket = null;
        Render();
    }

    /// <summary>
    /// Moves the selection band. The section is RETAINED and mutated: adding or removing one counts
    /// as a chart-shape change, so an empty selection is drawn as a transparent band instead.
    /// </summary>
    private void UpdateSelectionBand()
    {
        if (selectionSection is null)
        {
            return;
        }

        if (selectedBucket is not { } start || period is null)
        {
            selectionSection.Fill = new SolidColorPaint(SKColors.Transparent);
            selectionSection.Stroke = new SolidColorPaint(SKColors.Transparent);
            return;
        }

        var bucket = period.Buckets.FirstOrDefault(item => item.StartLocal.DateTime == start);
        if (bucket is null)
        {
            selectionSection.Fill = new SolidColorPaint(SKColors.Transparent);
            selectionSection.Stroke = new SolidColorPaint(SKColors.Transparent);
            return;
        }

        // CENTRED ON THE BUCKET'S START, not spanning its bounds.
        //
        // The band used to be drawn in BUCKET-EDGE coordinates (Xi = start, Xj = end) while the bar
        // is drawn CENTRED on the same start: LiveCharts' column series measures the axis unit in
        // pixels, halves it, and puts the rect's left edge at secondary - unitWidth/2. So a bucket
        // starting at T has its bar over [T - u/2, T + u/2] and had its band over [T, T + u] - half
        // a column to the right, which is why the highlight sat under the NEIGHBOURING bar.
        //
        // Half the AXIS unit, never half the bucket: in Year view the unit is 30 days and the
        // buckets are 28-31, so the two disagree by up to 5% of a column.
        var half = axisUnit.Ticks / 2;
        var center = bucket.StartLocal.DateTime.Ticks;
        selectionSection.Xi = center - half;
        selectionSection.Xj = center + half;

        var accent = palette.Accent;
        selectionSection.Fill = new SolidColorPaint(
            new SKColor(accent.Red, accent.Green, accent.Blue, (byte)(palette.IsDark ? 0x38 : 0x24)));
        selectionSection.Stroke = new SolidColorPaint(
            new SKColor(accent.Red, accent.Green, accent.Blue, 0x66))
        {
            StrokeThickness = 1
        };
    }

    // ---------------------------------------------------------------- model rows

    /// <summary>
    /// The per-model breakdown, in the flyout's row anatomy: label, inline meter, cost right-aligned
    /// and tokens secondary. Cost first, because this window is about money; tokens stay visible
    /// because they are the only thing a model with no published price can be judged by.
    /// </summary>
    private void RenderModelRows(PeriodView view)
    {
        var scopeBucket = selectedBucket is { } start
            ? view.Buckets.FirstOrDefault(bucket => bucket.StartLocal.DateTime == start)
            : null;

        // A selection that no longer exists in the data (a refresh dropped the bucket) is not a
        // reason to show an empty card - the period is what survives.
        if (selectedBucket is not null && scopeBucket is null)
        {
            selectedBucket = null;
        }

        var models = (scopeBucket?.Models ?? view.Models)
            .Where(model => model.EstimatedCostUsd > 0 || model.TotalTokens > 0)
            .OrderByDescending(model => model.EstimatedCostUsd)
            .ThenByDescending(model => model.TotalTokens)
            .ToArray();

        var scopeLabel = selectedBucket is { } selected
            ? GraphsPeriod.BucketLabel(
                view.Granularity,
                selected,
                scopeBucket is null ? null : scopeBucket.EndLocalExclusive - scopeBucket.StartLocal)
            : GraphsPeriod.Label(view.Granularity, anchor, DateOnly.FromDateTime(DateTime.Now));

        ModelScopeChip.Visibility = selectedBucket is null ? Visibility.Collapsed : Visibility.Visible;
        ModelScopeText.Visibility = selectedBucket is null ? Visibility.Visible : Visibility.Collapsed;
        ModelScopeChipText.Text = scopeLabel;
        ModelScopeText.Text = scopeLabel;
        ToolTipService.SetToolTip(ModelScopeText, scopeLabel);

        if (models.Length == 0)
        {
            modelRows.Clear();
            modelRowShape = string.Empty;
            // Never a $0.00 total over an empty-state panel: the footer is a summary of rows, and
            // with no rows it is a claim about nothing.
            ModelTotalPanel.Visibility = Visibility.Collapsed;
            if (selectedBucket is not null)
            {
                ShowModelEmpty(
                    $"No per-model costs for {scopeLabel}",
                    "The bucket's total is still counted above.",
                    busy: false);
            }
            else if (view.HasData)
            {
                ShowModelEmpty("Nothing recorded in this period", "Use the arrows to move to a period with data.", busy: false);
            }
            else
            {
                ShowModelEmpty(
                    plottedInsights?.HasUsage == true ? "Nothing recorded in this period" : "No usage recorded yet",
                    plottedInsights?.HasUsage == true
                        ? "Use the arrows to move to a period with data."
                        : "CodexBar records usage as you use this tool.",
                    busy: false);
            }

            return;
        }

        HideModelEmpty();

        var visible = models.Take(MaxModelRows).ToArray();
        var dropped = models.Skip(MaxModelRows).ToArray();

        var keys = visible.Select(model => model.Model).ToList();
        if (dropped.Length > 0)
        {
            keys.Add("\0overflow");
        }

        // Rebuilt ONLY when the ordered model SET changes, for the reason ModelRowModel documents.
        var shape = ShapeKey(keys) + "\u0001" + scopeLabel;
        if (shape != modelRowShape)
        {
            modelRowShape = shape;
            modelRows.Clear();
            foreach (var model in visible)
            {
                modelRows.Add(new ModelRowModel(model.Model, isOverflow: false));
            }

            if (dropped.Length > 0)
            {
                modelRows.Add(new ModelRowModel("\0overflow", isOverflow: true));
            }
        }

        // Meters scale to the TOP SPENDER, not to the period total: once spend spreads across six
        // models every row would be a sliver, and the chart above already carries absolute
        // magnitude. Share-of-total lives on the row tooltip instead.
        var top = Math.Max(visible[0].EstimatedCostUsd, dropped.Sum(model => model.EstimatedCostUsd));
        var total = models.Sum(model => model.EstimatedCostUsd);
        var trackBrush = BrushFrom(palette.Track);

        for (var index = 0; index < visible.Length && index < modelRows.Count; index++)
        {
            var model = visible[index];
            var row = modelRows[index];
            var color = CategoryColor(model.Model);

            row.Name = FriendlyModelLabel(model.Model);
            row.MeterValue = MeterValue(model.EstimatedCostUsd, top);
            row.ColorBrush = BrushFrom(color);
            row.TrackBrush = trackBrush;
            row.CostText = model.EstimatedCostUsd > 0 || !model.HasIncompleteCost ? FormatUsd(model.EstimatedCostUsd) : "—";
            row.TokensText = FormatTokensCompact(model.TotalTokens);
            row.DetailText = ModelRowDetail(model, total, scopeLabel);

            // TryAdd semantics WITHIN a pass: the chart and the rows nudge independently, so a
            // label drawn in both can carry two hexes and the CHART is the one the settings swatch
            // should match. ApplyDailyColors clears the map first, so this fills in only the models
            // that appear solely in the breakdown - without which the settings colour list would
            // silently lose every breakdown-only model when the row chart was deleted.
            if (!drawnColors.ContainsKey(model.Model))
            {
                RecordDrawnColor(model.Model, color);
            }
        }

        if (dropped.Length > 0 && modelRows.Count == visible.Length + 1)
        {
            var overflow = modelRows[^1];
            var droppedCost = dropped.Sum(model => model.EstimatedCostUsd);

            overflow.Name = $"+{dropped.Length} more";
            overflow.MeterValue = MeterValue(droppedCost, top);
            overflow.TrackBrush = trackBrush;

            // Neutral, and NOT a category colour: the row stands for a list, so giving it a model's
            // hue would claim it is one.
            overflow.ColorBrush = BrushFrom(new SKColor(
                palette.SecondaryText.Red,
                palette.SecondaryText.Green,
                palette.SecondaryText.Blue,
                0x8C));
            overflow.CostText = FormatUsd(droppedCost);
            overflow.TokensText = FormatTokensCompact(dropped.Sum(model => model.TotalTokens));
            overflow.DetailText = string.Join(
                Environment.NewLine,
                dropped.Take(20)
                    .Select(model => $"{FriendlyModelLabel(model.Model)}  ·  {FormatUsd(model.EstimatedCostUsd)}")
                    .Concat(dropped.Length > 20 ? new[] { "…" } : Array.Empty<string>()));
        }

        RenderModelTotal(view, models, scopeBucket, scopeLabel);
    }

    /// <summary>
    /// Scales a row's meter to the TOP SPENDER, with a floor under anything non-zero.
    /// </summary>
    /// <remarks>
    /// Scaling to the top spender rather than to the total is deliberate (the chart above carries
    /// absolute magnitude; six models against a total makes every row a sliver). The FLOOR is the
    /// fix: a model at 0.4% of the top spender drew a sub-pixel indicator, i.e. nothing at all, next
    /// to a printed cost - a row that says "$0.02" and shows no meter reads as a rendering bug.
    /// </remarks>
    private static double MeterValue(decimal cost, decimal top)
    {
        if (cost <= 0 || top <= 0)
        {
            return 0;
        }

        return Math.Max(4, (double)(cost / top) * 100);
    }

    /// <summary>
    /// The pinned total under the rows: cost and tokens for whatever the panel is scoped to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the ROWS' own column grid (118 / * / 64 / 76) and outside the ScrollViewer, for the reason
    /// <see cref="StackTotalTooltip"/> was written: a total whose figures do not sit in the same
    /// columns as the figures above it has to be read twice, and a total that scrolls away is not a
    /// total. The chart's tooltip already grew this row; the panel simply never got it.
    /// </para>
    /// <para>
    /// Summed over ALL models in scope, never over the eight visible ones - the "+N more" row exists
    /// precisely because those two differ.
    /// </para>
    /// </remarks>
    private void RenderModelTotal(
        PeriodView view,
        IReadOnlyList<ProviderModelUsage> models,
        UsageLedgerBucket? scopeBucket,
        string scopeLabel)
    {
        ModelTotalPanel.Visibility = Visibility.Visible;

        var cost = models.Sum(model => model.EstimatedCostUsd);
        var incomplete = models.Any(model => model.HasIncompleteCost);

        // Tokens for a drilled-into bucket live on the BUCKET (the scan fallback cannot attribute
        // tokens per model at all), and for the whole period on the period. Summing the rows would
        // report zero for a scan-sourced period that has real tokens.
        var tokens = scopeBucket?.TotalTokens ?? view.Tokens;

        ModelTotalCostText.Text = cost > 0 || !incomplete ? FormatUsd(cost) : "—";
        ModelTotalTokensText.Text = FormatTokensCompact(tokens);
        ToolTipService.SetToolTip(
            ModelTotalPanel,
            incomplete
                ? $"{FormatUsd(cost)}  ·  {FormatTokens(tokens)} in {scopeLabel}{Environment.NewLine}Some models could not be priced."
                : $"{FormatUsd(cost)}  ·  {FormatTokens(tokens)} in {scopeLabel}");
    }

    private static string ModelRowDetail(ProviderModelUsage model, decimal total, string scopeLabel)
    {
        var fast = model.FastEstimatedCostUsd > 0 ? $"  ·  fast {FormatUsd(model.FastEstimatedCostUsd)}" : string.Empty;
        var share = total > 0 ? $"  ·  {Math.Round(model.EstimatedCostUsd / total * 100)}% of {scopeLabel}" : string.Empty;
        var detail =
            $"{model.Model}{Environment.NewLine}" +
            $"Estimated {FormatUsd(model.EstimatedCostUsd)}{fast}{share}{Environment.NewLine}" +
            $"{FormatTokens(model.TotalTokens)} total, {FormatTokens(model.OutputTokens)} output";

        return model.HasIncompleteCost
            ? detail + Environment.NewLine + "Cost is incomplete for part of this period."
            : detail;
    }

    private void ShowModelEmpty(string title, string detail, bool busy)
    {
        modelRows.Clear();
        modelRowShape = string.Empty;
        ModelTotalPanel.Visibility = Visibility.Collapsed;
        ModelEmptyPanel.Visibility = Visibility.Visible;
        ModelEmptyTitle.Text = title;
        ModelEmptyDetail.Text = detail;
        ModelEmptyGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ModelBusyRing.IsActive = busy;
        ModelBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideModelEmpty()
    {
        ModelEmptyPanel.Visibility = Visibility.Collapsed;
        ModelBusyRing.IsActive = false;
        ModelBusyRing.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Identity of a SET. Two renders that produce the same key reuse the same objects; anything
    /// else is a genuinely different chart and is rebuilt. The provider and the period are part of
    /// it because two accounts can happen to use the same models, and moving the period moves every
    /// point's date.
    /// </summary>
    private string ShapeKey(IEnumerable<string> parts) =>
        selectedProviderKey + "\u0001" + granularity + "\u0001" + anchor.DayNumber + "\u0001" +
        string.Join('\u0002', parts);

    // ---------------------------------------------------------------- chart chrome

    /// <summary>
    /// Axes, legend, sections and tooltip chrome. Every paint is set explicitly from
    /// <see cref="palette"/>: LiveCharts' own default theme is a fixed light one, so an unstyled
    /// chart renders near-black text on a dark window.
    /// </summary>
    private void ConfigureCharts()
    {
        ApplyDailyAxis();

        DailyChart.LegendPosition = LegendPosition.Bottom;
        DailyChart.LegendTextPaint = new SolidColorPaint(palette.SecondaryText);
        DailyChart.LegendTextSize = 11;
        DailyChart.LegendBackgroundPaint = new SolidColorPaint(SKColors.Transparent);
        DailyChart.TooltipPosition = TooltipPosition.Top;
        // The whole bucket's stack in one tooltip, which is what the GDI+ chart's hover text showed
        // - and what makes a click anywhere in a column resolve to the whole column.
        DailyChart.FindingStrategy = FindingStrategy.CompareOnlyX;
        ApplyTooltipPaints(DailyChart);

        // The tooltip carries the bucket's TOTAL under a rule; see StackTotalTooltip for why that
        // is a subclass of the stock one rather than a formatter or a carrier series.
        dailyTooltip ??= new StackTotalTooltip(value => FormatUsd((decimal)value));
        dailyTooltip.RuleColor = new SKColor(palette.Text.Red, palette.Text.Green, palette.Text.Blue, 0x45);
        DailyChart.Tooltip = dailyTooltip;

        // Assigned ONCE and mutated thereafter - see the dailySeries and selectionSection fields.
        DailyChart.Series = dailySeries;

        if (selectionSection is null)
        {
            selectionSection = new RectangularSection
            {
                Fill = new SolidColorPaint(SKColors.Transparent),
                Stroke = new SolidColorPaint(SKColors.Transparent)
            };
            DailyChart.Sections = [selectionSection];
        }

        UpdateSelectionBand();
    }

    /// <summary>
    /// The X axis for the current granularity. Rebuilt on a granularity change because the unit and
    /// the labeler both move; it is an AXIS, not a series, so nothing cached about the bars is lost.
    /// </summary>
    private void ApplyDailyAxis()
    {
        var labels = new SolidColorPaint(palette.SecondaryText);
        var separators = new SolidColorPaint(palette.Separator) { StrokeThickness = 1 };

        // The unit each granularity's axis is built with, remembered for the selection band. Kept in
        // ONE place with the axis it describes - see the axisUnit field.
        axisUnit = granularity switch
        {
            UsageLedgerGranularity.Year => TimeSpan.FromDays(30),
            UsageLedgerGranularity.Day => TimeSpan.FromHours(1),
            _ => TimeSpan.FromDays(1)
        };

        var axis = granularity switch
        {
            // A 30-day unit against calendar months of 28-31 days is a ~5% mismatch, which is
            // invisible in the column spacing and is corrected for in the selection band. The STEP
            // is the lever that matters here: without forcing it, twelve "MMM" labels are decimated
            // to four at the 600 DIP minimum width and the year reads as a quarter chart.
            UsageLedgerGranularity.Year => new DateTimeAxis(TimeSpan.FromDays(30), date => date.ToString("MMM", CultureInfo.CurrentCulture))
            {
                MinStep = TimeSpan.FromDays(30).Ticks,
                ForceStepToMin = true
            },
            UsageLedgerGranularity.Week => new DateTimeAxis(TimeSpan.FromDays(1), date => date.ToString("ddd d", CultureInfo.CurrentCulture)),
            UsageLedgerGranularity.Day => new DateTimeAxis(TimeSpan.FromHours(1), date => date.ToString("h tt", CultureInfo.CurrentCulture))
            {
                // 24 hourly labels at the 600 DIP minimum width collide, and the 11px text size and
                // the absence of rotation are the window's ramp rather than a chart preference - so
                // the STEP is the lever.
                MinStep = TimeSpan.FromHours(3).Ticks,
                ForceStepToMin = true
            },
            _ => new DateTimeAxis(TimeSpan.FromDays(1), date => date.ToString("MMM d", CultureInfo.CurrentCulture))
        };

        axis.LabelsPaint = labels;
        axis.TextSize = 11;
        axis.SeparatorsPaint = null;
        axis.TicksPaint = null;

        DailyChart.XAxes = [axis];
        DailyChart.YAxes =
        [
            new Axis
            {
                Labeler = value => FormatAxisUsd((decimal)value),
                LabelsPaint = labels,
                TextSize = 11,
                MinLimit = 0,
                SeparatorsPaint = separators
            }
        ];
    }

    private void ApplyTooltipPaints(LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart)
    {
        chart.TooltipTextPaint = new SolidColorPaint(palette.Text);
        chart.TooltipBackgroundPaint = new SolidColorPaint(palette.TooltipBackground)
        {
            // The tooltip is painted INSIDE the chart canvas, directly over the bars; without a
            // shadow it reads as text floating on the plot rather than as a raised surface.
            ImageFilter = new DropShadow(0f, 2f, 5f, 5f, new SKColor(0, 0, 0, 110))
        };
        chart.TooltipTextSize = 12;
    }

    /// <summary>
    /// Fades the chart out behind a message rather than collapsing it: a collapsed chart would
    /// have to re-create its Skia canvas when data arrives, which is the exact stall the startup
    /// pre-warm exists to avoid.
    /// </summary>
    private void ShowChartOverlay(string message, bool busy)
    {
        DailyChart.Opacity = 0;
        DailyOverlay.Visibility = Visibility.Visible;
        DailyOverlayText.Text = message;
        DailyOverlayRing.IsActive = busy;
        DailyOverlayRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideChartOverlay()
    {
        DailyChart.Opacity = 1;
        DailyOverlay.Visibility = Visibility.Collapsed;
        DailyOverlayRing.IsActive = false;
    }

    // ---------------------------------------------------------------- theme

    /// <summary>
    /// Any settings save lands here, INCLUDING a chart colour edit. That is the only path such an
    /// edit has: <c>ActualThemeChanged</c> below fires for a light/dark flip, which a colour
    /// change is not, so without re-applying the palette here an open graphs window would keep
    /// the old colours until it was reopened.
    /// </summary>
    /// <remarks>
    /// Colours only - a colour is a SHAPE-PRESERVING change, so the series objects and their
    /// cached geometries survive and the entrance animation does not replay.
    /// </remarks>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // AppTheme.Changed is a STATIC event raised for every window at once, so a save that
        // lands in the same dispatcher turn as this window's close would otherwise re-theme a
        // window whose content has already been dropped.
        if (isClosed)
        {
            return;
        }

        AppTheme.Apply(this, RootGrid, TintLayer);

        palette = ChartPalette.For(RootGrid, AppTheme.Settings.ChartColorOverrides);

        // The map is keyed on colours the OLD palette produced, so it has to be rebuilt before
        // anything reads it - otherwise a colour edit can leave two labels nudged against a
        // collision that no longer exists.
        if (period is not null)
        {
            RebuildNudgeSteps(period);
        }

        ApplyDailyColors();
        UpdateSelectionBand();

        // The rows carry palette brushes rather than {ThemeResource} ones (a category colour has no
        // theme resource), so they are re-assigned - through the equality-guarded setters, so the
        // meters do not re-animate when a colour did not actually move.
        if (period is not null)
        {
            RenderModelRows(period);
        }
    }

    /// <summary>
    /// Chart paints are Skia colours, not <c>{ThemeResource}</c>s, so nothing re-resolves them
    /// for free: the palette and every paint built from it are rebuilt here.
    /// </summary>
    private void OnActualThemeChanged()
    {
        // Dropping the window's content in QuiesceCharts re-parents RootGrid, which is itself an
        // ActualTheme change - so this fires DURING teardown unless it is gated.
        if (isClosed)
        {
            return;
        }

        AppTheme.ApplyTint(RootGrid, TintLayer);
        palette = ChartPalette.For(RootGrid, AppTheme.Settings.ChartColorOverrides);
        ConfigureCharts();

        // ConfigureCharts replaced the axes and every paint, so the gate below has to let this
        // render through even though the numbers did not move.
        plottedKey = string.Empty;
        plottedScopeKey = string.Empty;
        Render();
    }

    // ---------------------------------------------------------------- formatting

    /// <summary>
    /// The categories that make up one bucket's column, in stack order. Providers that report no
    /// category split still get a regular/fast pair so the bar means the same thing everywhere.
    /// </summary>
    private static IReadOnlyList<ProviderSpendCategory> BucketSpendCategories(UsageLedgerBucket bucket)
    {
        if (bucket.Categories.Count > 0)
        {
            return bucket.Categories
                .Where(category => category.EstimatedCostUsd > 0)
                .OrderBy(category => category.Label.Contains(" fast", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var categories = new List<ProviderSpendCategory>();
        if (bucket.RegularEstimatedCostUsd > 0)
        {
            categories.Add(new ProviderSpendCategory("regular", bucket.RegularEstimatedCostUsd));
        }

        if (bucket.FastEstimatedCostUsd > 0)
        {
            categories.Add(new ProviderSpendCategory("fast", bucket.FastEstimatedCostUsd));
        }

        return categories;
    }

    private static string MetricDetail(long tokens, decimal fastCost)
    {
        var text = FormatTokens(tokens);
        return fastCost > 0 ? $"{text}  ·  fast {FormatUsd(fastCost)}" : text;
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000_000)
        {
            return $"{tokens / 1_000_000_000d:0.##}B tokens";
        }

        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000d:0.#}M tokens";
        }

        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000d:0.#}K tokens";
        }

        return $"{tokens} tokens";
    }

    /// <summary>The model row's token column: no unit word, because the column is 76 DIPs wide.</summary>
    private static string FormatTokensCompact(long tokens)
    {
        if (tokens >= 1_000_000_000)
        {
            return $"{tokens / 1_000_000_000d:0.#}B";
        }

        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000d:0.#}M";
        }

        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000d:0}K";
        }

        return tokens.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The delta metric's figure. A change too small to round to a percent still has to read as a
    /// change, and a 40x month has to read as one number rather than as five digits.
    /// </summary>
    private static string FormatSignedPercent(decimal current, decimal previous)
    {
        if (previous <= 0)
        {
            return "—";
        }

        var percent = (current - previous) / previous * 100m;
        if (percent == 0)
        {
            return "0%";
        }

        var sign = percent > 0 ? "+" : "-";
        var magnitude = Math.Abs(percent);

        if (magnitude < 1)
        {
            return $"{sign}<1%";
        }

        return magnitude > 999 ? $"{sign}>999%" : $"{sign}{Math.Round(magnitude)}%";
    }

    private static string FormatUsd(decimal value)
    {
        if (value <= 0)
        {
            return "$0.00";
        }

        return value < 0.01m ? "<$0.01" : $"${value:0.00}";
    }

    /// <summary>Compact axis-label currency: "$4", "$2.50", "$0.05".</summary>
    private static string FormatAxisUsd(decimal value) => $"${value:0.##}";

    private static string FormatObservedAt(DateTimeOffset observedAt)
    {
        var local = observedAt.ToLocalTime();
        var age = DateTimeOffset.Now - local;

        if (age.TotalSeconds < 90)
        {
            return "just now";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{Math.Floor(age.TotalMinutes)} min ago";
        }

        return local.ToString("ddd, dd MMM h:mm tt");
    }

    private static string FriendlyModelLabel(string label)
    {
        var isFast = label.EndsWith(" fast", StringComparison.OrdinalIgnoreCase);
        var normalized = isFast ? label[..^5] : label;
        normalized = normalized.Replace("gpt-", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("claude-", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("-codex", string.Empty, StringComparison.OrdinalIgnoreCase);
        return isFast ? normalized + " fast" : normalized;
    }

    private static string ShortSpendLabel(string label)
    {
        var normalized = FriendlyModelLabel(label);
        return normalized.Length <= 14 ? normalized : normalized[..13] + "…";
    }
}
