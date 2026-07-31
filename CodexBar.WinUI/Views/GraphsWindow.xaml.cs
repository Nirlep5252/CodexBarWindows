using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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
    /// </summary>
    private const int MinimumWidthDips = 600;
    private const int MinimumHeightDips = 520;

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

    private string dailyShape = string.Empty;
    private string modelRowShape = string.Empty;

    private StackTotalTooltip? dailyTooltip;

    /// <summary>
    /// The selection band behind the drilled-into column. ONE retained section, mutated in place:
    /// LiveCharts' Fill is per SERIES, so a single day cannot be highlighted by recolouring, and
    /// adding or removing a section counts as a chart-shape change that re-measures the plot.
    /// </summary>
    private RectangularSection? selectionSection;

    // ---- the period the strip is pointing at --------------------------------------------------

    /// <summary>Year is fully wired but has no visible control; see the SelectorBar in the XAML.</summary>
    private UsageLedgerGranularity granularity = UsageLedgerGranularity.Month;

    /// <summary>Any date INSIDE the selected period. The bounds are derived, never stored.</summary>
    private DateOnly anchor = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Local start of the drilled-into bucket, or null for the whole period.</summary>
    private DateTime? selectedBucket;

    private UsageLedgerCoverage coverage = UsageLedgerCoverage.None;
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

        granularity = next;

        // The ANCHOR is kept, so "July, switched to Week" lands on the week containing the anchor
        // rather than resetting the user to today. A period that would now sit in the future is
        // clamped back to the current one.
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (GraphsPeriod.Bounds(granularity, anchor).Start > today)
        {
            anchor = today;
        }

        selectedBucket = null;
        ApplyDailyAxis();
        InvalidatePeriod();
        Render();
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
    /// can actually move it (provider change, completed scan) rather than per render.
    /// </summary>
    private void RefreshCoverage() => coverage = UsageLedger.GetCoverage(LedgerScope);

    private UsageLedgerScope LedgerScope =>
        ProviderKeys.ProviderOf(selectedProviderKey) == UsageProvider.Claude
            ? UsageLedgerScope.Claude
            : UsageLedgerScope.Codex;

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
        RenderMetrics(period);

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
        int ElapsedUnits,
        int TotalUnits,
        bool IsCurrent);

    private PeriodView BuildPeriod(ProviderUsageInsights insights)
    {
        var (start, end) = GraphsPeriod.Bounds(granularity, anchor);
        var bucket = GraphsPeriod.BucketOf(granularity);
        var scope = LedgerScope;
        var pricing = LedgerPricing.For(scope);

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
        if ((!hasData || !series.HasPriceableData) && bucket != UsageLedgerGranularity.Hour)
        {
            var fallback = BuildFallbackBuckets(buckets, insights);
            if (fallback is not null)
            {
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

        // Averaging over the calendar period would read as a 60% drop for the first month recorded:
        // a month whose recording began on the 20th has 12 days of data, not 31. Elapsed buckets are
        // therefore clamped to the coverage floor as well as to now.
        var elapsed = buckets.Count(item =>
            item.StartLocal <= now &&
            (floor is not { } value || item.EndLocalExclusive > value.ToDateTime(TimeOnly.MinValue)));
        elapsed = Math.Max(1, elapsed);

        var previousAnchor = GraphsPeriod.Shift(granularity, anchor, -1);
        var (previousStart, previousEnd) = GraphsPeriod.Bounds(granularity, previousAnchor);
        var previousSeries = QueryRange(scope, previousStart, previousEnd, bucket, pricing);
        var previousBuckets = (IReadOnlyList<UsageLedgerBucket>)previousSeries.Buckets;
        if (!previousBuckets.Any(item => item.EstimatedCostUsd > 0) && bucket != UsageLedgerGranularity.Hour)
        {
            previousBuckets = BuildFallbackBuckets(previousBuckets, insights) ?? previousBuckets;
        }

        // LIKE FOR LIKE: three days of July against all of June would show a permanent, meaningless
        // -90%, so a period still running is compared against the same number of elapsed buckets of
        // the one before it.
        var previousCost = isCurrent
            ? previousBuckets.Take(elapsed).Sum(item => item.EstimatedCostUsd)
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
            elapsed,
            buckets.Count,
            isCurrent);
    }

    private static UsageLedgerSeries QueryRange(
        UsageLedgerScope scope,
        DateOnly start,
        DateOnly endInclusive,
        UsageLedgerGranularity bucket,
        UsageLedgerPricing pricing)
    {
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(start.ToDateTime(TimeOnly.MinValue)));
        var toDate = endInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var to = new DateTimeOffset(toDate, TimeZoneInfo.Local.GetUtcOffset(toDate));
        return UsageLedger.Query(scope, from, to, bucket, TimeZoneInfo.Local, pricing);
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
        var canGoBack = hasData && (floor is not { } value || GraphsPeriod.Bounds(granularity, GraphsPeriod.Shift(granularity, anchor, -1)).EndInclusive >= value);
        var isCurrent = start <= today && today <= end;

        PrevPeriodButton.IsEnabled = canGoBack;
        ToolTipService.SetToolTip(
            PrevPeriodButton,
            canGoBack
                ? $"Previous {GraphsPeriod.Noun(granularity)}"
                : importable
                    ? "Import history in Settings to see earlier months"
                    : "No earlier data");

        NextPeriodButton.IsEnabled = !isCurrent;
        ToolTipService.SetToolTip(
            NextPeriodButton,
            isCurrent ? "Already at the current period" : $"Next {GraphsPeriod.Noun(granularity)}");

        JumpToNowButton.Visibility = isCurrent ? Visibility.Collapsed : Visibility.Visible;

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

        AverageLabelText.Text = $"Average / {GraphsPeriod.BucketNoun(view.Granularity)}";
        SetMetric(
            AverageValueText,
            AverageDetailText,
            FormatUsd(view.Cost / view.ElapsedUnits),
            $"over {view.ElapsedUnits} {Plural(GraphsPeriod.BucketNoun(view.Granularity), view.ElapsedUnits)}");

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

        if (view.IsCurrent && view.ElapsedUnits < view.TotalUnits)
        {
            OutlookLabelText.Text = "Projected";
            SetMetric(
                OutlookValueText,
                OutlookDetailText,
                FormatUsd(view.Cost / view.ElapsedUnits * view.TotalUnits),
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
            GraphsPeriod.BucketLabel(view.Granularity, peak.StartLocal.DateTime));
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
    private static Microsoft.UI.Xaml.Media.SolidColorBrush BrushFrom(SKColor color) =>
        new(Windows.UI.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));

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
                MaxBarWidth = 26,
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

        var used = new Dictionary<uint, int>();
        foreach (var slot in dailySlots)
        {
            var color = slot.ColorKey is null ? palette.Accent : CategoryColor(slot.ColorKey, used);
            ApplyFill(slot.Series, color);

            RecordDrawnColor(slot.ColorKey, color);
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
    private SKColor CategoryColor(string label, Dictionary<uint, int> used)
    {
        var color = palette.ForCategory(label);
        var key = (uint)color;
        var step = used.TryGetValue(key, out var seen) ? seen : 0;
        used[key] = step + 1;

        // A colour the user picked is drawn EXACTLY as picked - it still claims its slot, so an
        // automatic colour that lands on the same value is separated from it, but nudging the
        // pick itself would mean the chart never shows the hex the settings page promises.
        return palette.IsOverridden(label) ? color : ChartPalette.Nudge(color, step, palette.IsDark);
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
    /// ACCEPTED GAP: this is pointer-only, because chart points are not focusable. The same data is
    /// reachable from the keyboard by switching the granularity to Day and paging with the arrows.
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

        // The DateTimeAxis works in TICKS, which is also what a DateTimePoint's secondary value is.
        selectionSection.Xi = bucket.StartLocal.DateTime.Ticks;
        selectionSection.Xj = bucket.EndLocalExclusive.DateTime.Ticks;

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
            ? GraphsPeriod.BucketLabel(view.Granularity, selected)
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

        var used = new Dictionary<uint, int>();
        for (var index = 0; index < visible.Length && index < modelRows.Count; index++)
        {
            var model = visible[index];
            var row = modelRows[index];
            var color = CategoryColor(model.Model, used);

            row.Name = FriendlyModelLabel(model.Model);
            row.MeterValue = top > 0 ? (double)(model.EstimatedCostUsd / top) * 100 : 0;
            row.ColorBrush = BrushFrom(color);
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
            overflow.MeterValue = top > 0 ? (double)(droppedCost / top) * 100 : 0;

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

        var axis = granularity switch
        {
            UsageLedgerGranularity.Year => new DateTimeAxis(TimeSpan.FromDays(30), date => date.ToString("MMM", CultureInfo.CurrentCulture)),
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
