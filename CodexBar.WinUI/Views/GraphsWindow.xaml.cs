using System;
using System.Collections.Generic;
using System.Linq;
using CodexBarWindows;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.ImageFilters;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBar.WinUI;

/// <summary>
/// The "Usage graphs" window: local 30-day usage history for one provider at a time - Today /
/// last-30-days summary cards, a stacked daily estimated-spend column chart, and a per-model
/// spend breakdown.
/// </summary>
/// <remarks>
/// <para>
/// Port of the WinForms <c>UsageGraphsForm</c>. The charts are LiveCharts2 rather than GDI+, so
/// gridlines, axis labels, legends, hover highlighting, tooltips and entrance animations are the
/// library's job now; what is ported deliberately is the DATA SHAPING - which categories stack,
/// in what order, and in which colours (see <see cref="ChartPalette.ForCategory"/>).
/// </para>
/// <para>
/// THE HISTORY GATE IS LOAD-BEARING. The 30-day rebuild parses every local session log, and this
/// is the only surface that plots it, so <see cref="UsageRefreshService.IncludeHistory"/> is
/// turned on while this window is showing and off again when it closes. That is why the app
/// costs nothing when idle.
/// </para>
/// </remarks>
public sealed partial class GraphsWindow : Window
{
    /// <summary>Distinct stacked categories drawn before the rest are pooled into "other".</summary>
    private const int MaxDailySeries = 7;

    /// <summary>Model rows drawn before the rest are summarised in the overflow caption.</summary>
    private const int MaxModelRows = 8;

    private const string WindowId = "graphs";

    private readonly IntPtr hwnd;
    private readonly UsageRefreshService service;
    private readonly List<ProviderOption> providers = [];

    /// <summary>
    /// Times the open. The number that matters is "window created -> first chart frame", because
    /// until that frame lands the window is blank; it is logged once so the startup pre-warm can
    /// be shown to work rather than assumed to.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch openStopwatch = System.Diagnostics.Stopwatch.StartNew();

    private ChartPalette palette;
    private string selectedProviderKey = ProviderKeys.Codex("default");
    private string errorFullText = string.Empty;
    private bool suppressComboEvents;
    private bool isOpen;
    private bool firstFrameLogged;

    public GraphsWindow(UsageRefreshService service)
    {
        this.service = service;

        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        palette = ChartPalette.For(RootGrid);

        Title = $"Usage graphs - {AppInfo.AppName}";
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "CodexBarWindows.ico"));

        var scale = NativeWindow.ScaleFor(hwnd);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(940 * scale),
            (int)Math.Round(760 * scale)));

        RootGrid.ActualThemeChanged += (_, _) => OnActualThemeChanged();
        AppTheme.Changed += OnThemeChanged;
        Activated += (_, _) => ActivationChanged?.Invoke(this, EventArgs.Empty);
        Closed += (_, _) => OnWindowClosed();

        service.HistoryUpdated += OnHistoryUpdated;
        service.RefreshingChanged += OnRefreshingChanged;
        service.CodexEntriesChanged += ConfigureProviders;

        DailyChart.UpdateFinished += _ =>
        {
            if (firstFrameLogged)
            {
                return;
            }

            firstFrameLogged = true;
            DiagnosticLog.Write(
                "graphs first chart frame in {0} ms (prewarmed={1})",
                openStopwatch.ElapsedMilliseconds,
                ChartPrewarm.IsWarm);
        };

        AppTheme.Apply(this, RootGrid, TintLayer);
        ConfigureCharts();
        ConfigureProviders();
    }

    /// <summary>
    /// Raised whenever this window's activation changes, so the flyout can re-test whether the
    /// foreground is still inside this process. See <see cref="FlyoutWindow.ReArmDismissCheck"/>.
    /// </summary>
    public event EventHandler? ActivationChanged;

    public string SelectedProvider => selectedProviderKey;

    public void ShowAndFocus()
    {
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

    private void OnWindowClosed()
    {
        AppTheme.Changed -= OnThemeChanged;
        service.HistoryUpdated -= OnHistoryUpdated;
        service.RefreshingChanged -= OnRefreshingChanged;
        service.CodexEntriesChanged -= ConfigureProviders;

        if (isOpen)
        {
            isOpen = false;
            service.IncludeHistory = false;
            service.SetWindowOpen(WindowId, false);
            DiagnosticLog.Write("graphs closed includeHistory=false polling={0}", service.IsPolling);
        }
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
        Render();
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressComboEvents)
        {
            return;
        }

        var index = ProviderCombo.SelectedIndex;
        if (index < 0 || index >= providers.Count)
        {
            return;
        }

        selectedProviderKey = providers[index].Key;
        Render();

        if (!service.GetHistory(selectedProviderKey).HasInsights)
        {
            service.Refresh();
        }
    }

    // ---------------------------------------------------------------- rendering

    private void OnHistoryUpdated(string providerKey, ProviderUsageInsightsLookupResult result)
    {
        if (providerKey == selectedProviderKey)
        {
            Render();
        }
    }

    private void OnRefreshingChanged(bool refreshing) => Render();

    private void Render()
    {
        var result = service.GetHistory(selectedProviderKey);
        var refreshing = service.IsRefreshing;

        BusyRing.IsActive = refreshing;
        BusyRing.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;

        if (result.Insights is not { } insights)
        {
            // No numbers at all yet: either the first scan is running or it failed outright.
            RenderSubtitle(
                refreshing ? "Scanning local sessions…" : "No usage history loaded yet",
                isStale: false);
            RenderError(result.Error, ErrorKind.NoData);
            SetMetric(TodayValueText, TodayDetailText, refreshing ? "…" : "--", string.Empty);
            SetMetric(MonthValueText, MonthDetailText, refreshing ? "…" : "--", string.Empty);
            ShowChartOverlay(
                DailyChart,
                DailyOverlay,
                DailyOverlayRing,
                DailyOverlayText,
                refreshing ? "Scanning local sessions…" : "No history yet",
                busy: refreshing);
            ShowChartOverlay(
                ModelChart,
                ModelOverlay,
                ModelOverlayRing,
                ModelOverlayText,
                refreshing ? "Scanning local sessions…" : "No model breakdown yet",
                busy: refreshing);
            ModelOverflowText.Visibility = Visibility.Collapsed;
            return;
        }

        var source = string.IsNullOrWhiteSpace(insights.Source) ? "Local estimates" : insights.Source;
        RenderSubtitle(
            result.IsStale
                ? $"Showing data from {FormatObservedAt(insights.ObservedAt)} — the last refresh failed"
                : refreshing
                    ? $"{source}  ·  refreshing…"
                    : $"{source}  ·  updated {FormatObservedAt(insights.ObservedAt)}",
            result.IsStale);
        RenderError(result.Error, result.IsStale ? ErrorKind.Stale : ErrorKind.Incomplete);

        SetMetric(
            TodayValueText,
            TodayDetailText,
            FormatUsd(insights.TodayEstimatedCostUsd),
            MetricDetail(insights.TodayTokens, insights.TodayFastEstimatedCostUsd));
        SetMetric(
            MonthValueText,
            MonthDetailText,
            FormatUsd(insights.Last30DaysEstimatedCostUsd),
            MetricDetail(insights.Last30DaysTokens, insights.Last30DaysFastEstimatedCostUsd));

        RenderDailyChart(insights);
        RenderModelChart(insights);
    }

    private void RenderSubtitle(string text, bool isStale)
    {
        SubtitleText.Text = text;
        StaleIcon.Visibility = isStale ? Visibility.Visible : Visibility.Collapsed;

        if (isStale)
        {
            // Built from the palette, NOT from Application.Current.Resources: a brush pulled out
            // of the app resources resolves against the APP theme and renders wrong the moment
            // the user forces the opposite theme. The palette follows the element's ActualTheme
            // and is rebuilt on ActualThemeChanged.
            var warning = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(
                palette.Warning.Alpha,
                palette.Warning.Red,
                palette.Warning.Green,
                palette.Warning.Blue));
            SubtitleText.Foreground = warning;
            StaleIcon.Foreground = warning;
        }
        else
        {
            // Cleared so the XAML {ThemeResource} tertiary brush takes over again.
            SubtitleText.ClearValue(TextBlock.ForegroundProperty);
        }
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
    /// Shows an error with its FULL text - wrapped by the InfoBar, repeated in the tooltip and
    /// copyable - matching the flyout. The WinForms window squeezed this into a one-line
    /// ellipsised subtitle, which routinely cut the only explanation for missing numbers.
    /// </summary>
    private void RenderError(string? error, ErrorKind kind)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            errorFullText = string.Empty;
            ErrorBar.IsOpen = false;
            ToolTipService.SetToolTip(ErrorBar, null);
            return;
        }

        errorFullText = error;
        // Numbers on screen plus a failed refresh is a warning, not a dead end; nothing at all
        // is the error case.
        ErrorBar.Severity = kind == ErrorKind.NoData ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        ErrorBar.Title = kind switch
        {
            ErrorKind.NoData => "Usage history unavailable",
            ErrorKind.Stale => "Usage history may be out of date",
            _ => "Usage history is incomplete"
        };
        ErrorBar.Message = error;
        ErrorBar.IsOpen = true;
        ToolTipService.SetToolTip(ErrorBar, error);
    }

    private void OnCopyError(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(errorFullText))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(errorFullText);
        Clipboard.SetContent(package);
    }

    private static void SetMetric(TextBlock valueText, TextBlock detailText, string value, string detail)
    {
        valueText.Text = value;
        detailText.Text = detail;
    }

    // ---------------------------------------------------------------- daily chart

    /// <summary>
    /// The daily chart: one stacked column per day, split by spend category. The category set,
    /// the stack order (regular categories first, then the "fast" ones, alphabetical within each)
    /// and the colours are the WinForms rules; LiveCharts draws them.
    /// </summary>
    private void RenderDailyChart(ProviderUsageInsights insights)
    {
        var daily = insights.Daily;
        if (daily.Count == 0 || daily.All(day => day.EstimatedCostUsd <= 0))
        {
            DailyChart.Series = [];
            ShowChartOverlay(
                DailyChart,
                DailyOverlay,
                DailyOverlayRing,
                DailyOverlayText,
                insights.HasUsage ? "No spend recorded in this window" : "No usage recorded yet",
                busy: false);
            return;
        }

        HideChartOverlay(DailyChart, DailyOverlay, DailyOverlayRing);

        // Every distinct category, biggest spender first, with the tail pooled so the legend
        // stays readable on an account that has used a dozen models.
        var totals = daily
            .SelectMany(DailySpendCategories)
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

        var series = new List<ISeries>();
        var usedColors = new Dictionary<uint, int>();

        foreach (var label in ordered)
        {
            series.Add(BuildDailySeries(
                ShortSpendLabel(label),
                daily,
                day => DailySpendCategories(day)
                    .Where(category => string.Equals(category.Label, label, StringComparison.OrdinalIgnoreCase))
                    .Sum(category => category.EstimatedCostUsd),
                CategoryColor(label, usedColors)));
        }

        if (pooled.Count > 0)
        {
            series.Add(BuildDailySeries(
                $"other ({pooled.Count})",
                daily,
                day => DailySpendCategories(day)
                    .Where(category => pooled.Contains(category.Label))
                    .Sum(category => category.EstimatedCostUsd),
                CategoryColor("other", usedColors)));
        }

        // No category split at all (a provider that only reports a daily total).
        if (series.Count == 0)
        {
            series.Add(BuildDailySeries("Estimated spend", daily, day => day.EstimatedCostUsd, palette.Accent));
        }

        DailyChart.Series = series;
    }

    private ISeries BuildDailySeries(
        string name,
        IReadOnlyList<ProviderDailyUsage> daily,
        Func<ProviderDailyUsage, decimal> selector,
        SKColor color)
    {
        // Every series carries one entry PER DAY, nulls included: LiveCharts stacks by entity
        // index, so a series that skipped its empty days would stack against the wrong dates.
        var values = daily
            .Select(day =>
            {
                var value = selector(day);
                return new DateTimePoint(
                    day.Day.ToDateTime(TimeOnly.MinValue),
                    value > 0 ? (double)value : null);
            })
            .ToArray();

        return new StackedColumnSeries<DateTimePoint>
        {
            Name = name,
            Values = values,
            Fill = new SolidColorPaint(color),
            Stroke = null,
            Rx = 2,
            Ry = 2,
            Padding = 2,
            MaxBarWidth = 26,
            // The tooltip already prints the series name in its own column; repeating it here
            // showed every row twice.
            YToolTipLabelFormatter = point => FormatUsd((decimal)point.Coordinate.PrimaryValue)
        };
    }

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
        return ChartPalette.Nudge(color, step, palette.IsDark);
    }

    // ---------------------------------------------------------------- model chart

    /// <summary>
    /// The per-model breakdown: a horizontal bar per model, top spender first, coloured by the
    /// same category rules. One series per model (rather than one series of many points) is what
    /// gives each row its own colour, with <c>IgnoresBarPosition</c> keeping them on one line.
    /// </summary>
    private void RenderModelChart(ProviderUsageInsights insights)
    {
        var ranked = insights.Models
            .Where(model => model.EstimatedCostUsd > 0)
            .OrderByDescending(model => model.EstimatedCostUsd)
            .ToArray();

        if (ranked.Length == 0)
        {
            ModelChart.Series = [];
            ModelOverflowText.Visibility = Visibility.Collapsed;
            ShowChartOverlay(
                ModelChart,
                ModelOverlay,
                ModelOverlayRing,
                ModelOverlayText,
                insights.HasUsage ? "No per-model costs in this window" : "No model data yet",
                busy: false);
            return;
        }

        HideChartOverlay(ModelChart, ModelOverlay, ModelOverlayRing);

        var visible = ranked.Take(MaxModelRows).ToArray();
        // Row charts count from the bottom, so the biggest spender has to sit at the last index
        // to appear at the top.
        var rows = visible.Reverse().ToArray();

        var series = new List<ISeries>();
        var usedColors = new Dictionary<uint, int>();
        for (var index = 0; index < rows.Length; index++)
        {
            var model = rows[index];
            var values = new double?[rows.Length];
            values[index] = (double)model.EstimatedCostUsd;

            var label = FriendlyModelLabel(model.Model);
            var detail = model.FastEstimatedCostUsd > 0
                ? $"  ·  fast {FormatUsd(model.FastEstimatedCostUsd)}"
                : string.Empty;

            series.Add(new RowSeries<double?>
            {
                Name = label,
                Values = values,
                Fill = new SolidColorPaint(CategoryColor(model.Model, usedColors)),
                Stroke = null,
                Rx = 3,
                Ry = 3,
                Padding = 4,
                MaxBarWidth = 22,
                IgnoresBarPosition = true,
                IsVisibleAtLegend = false,
                XToolTipLabelFormatter = point =>
                    $"{model.Model}\nEstimated {FormatUsd(model.EstimatedCostUsd)}{detail}\n" +
                    $"{FormatTokens(model.TotalTokens)} total, {FormatTokens(model.OutputTokens)} output"
            });
        }

        ModelChart.Series = series;
        ModelChart.YAxes = [BuildModelNameAxis(rows.Select(model => FriendlyModelLabel(model.Model)).ToArray())];

        if (ranked.Length > visible.Length)
        {
            var rest = ranked.Skip(visible.Length).ToArray();
            ModelOverflowText.Text = $"+{rest.Length} more  ·  {FormatUsd(rest.Sum(model => model.EstimatedCostUsd))}";
            ModelOverflowText.Visibility = Visibility.Visible;
        }
        else
        {
            ModelOverflowText.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------- chart chrome

    /// <summary>
    /// Axes, legend and tooltip chrome. Every paint is set explicitly from
    /// <see cref="palette"/>: LiveCharts' own default theme is a fixed light one, so an unstyled
    /// chart renders near-black text on a dark window.
    /// </summary>
    private void ConfigureCharts()
    {
        var labels = new SolidColorPaint(palette.SecondaryText);
        var separators = new SolidColorPaint(palette.Separator) { StrokeThickness = 1 };

        DailyChart.XAxes =
        [
            new DateTimeAxis(TimeSpan.FromDays(1), date => date.ToString("MMM d"))
            {
                LabelsPaint = labels,
                TextSize = 11,
                SeparatorsPaint = null,
                TicksPaint = null
            }
        ];
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
        DailyChart.LegendPosition = LegendPosition.Bottom;
        DailyChart.LegendTextPaint = new SolidColorPaint(palette.SecondaryText);
        DailyChart.LegendTextSize = 11;
        DailyChart.LegendBackgroundPaint = new SolidColorPaint(SKColors.Transparent);
        DailyChart.TooltipPosition = TooltipPosition.Top;
        // The whole day's stack in one tooltip, which is what the GDI+ chart's hover text showed.
        DailyChart.FindingStrategy = FindingStrategy.CompareOnlyX;
        ApplyTooltipPaints(DailyChart);

        ModelChart.XAxes =
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
        ModelChart.YAxes = [BuildModelNameAxis([])];
        ModelChart.LegendPosition = LegendPosition.Hidden;
        ModelChart.TooltipPosition = TooltipPosition.Top;
        ApplyTooltipPaints(ModelChart);
    }

    private Axis BuildModelNameAxis(string[] names) => new()
    {
        Labels = names,
        LabelsPaint = new SolidColorPaint(palette.Text),
        TextSize = 12,
        MinStep = 1,
        ForceStepToMin = true,
        SeparatorsPaint = null,
        TicksPaint = null
    };

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
    private static void ShowChartOverlay(
        LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart,
        FrameworkElement overlay,
        ProgressRing ring,
        TextBlock text,
        string message,
        bool busy)
    {
        chart.Opacity = 0;
        overlay.Visibility = Visibility.Visible;
        text.Text = message;
        ring.IsActive = busy;
        ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void HideChartOverlay(
        LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart,
        FrameworkElement overlay,
        ProgressRing ring)
    {
        chart.Opacity = 1;
        overlay.Visibility = Visibility.Collapsed;
        ring.IsActive = false;
    }

    // ---------------------------------------------------------------- theme

    private void OnThemeChanged(object? sender, EventArgs e) => AppTheme.Apply(this, RootGrid, TintLayer);

    /// <summary>
    /// Chart paints are Skia colours, not <c>{ThemeResource}</c>s, so nothing re-resolves them
    /// for free: the palette and every paint built from it are rebuilt here.
    /// </summary>
    private void OnActualThemeChanged()
    {
        AppTheme.ApplyTint(RootGrid, TintLayer);
        palette = ChartPalette.For(RootGrid);
        ConfigureCharts();
        Render();
    }

    // ---------------------------------------------------------------- formatting

    /// <summary>
    /// The categories that make up one day's bar, in stack order. Providers that report no
    /// category split still get a regular/fast pair so the bar means the same thing everywhere.
    /// </summary>
    private static IReadOnlyList<ProviderSpendCategory> DailySpendCategories(ProviderDailyUsage day)
    {
        if (day.Categories.Count > 0)
        {
            return day.Categories
                .Where(category => category.EstimatedCostUsd > 0)
                .OrderBy(category => category.Label.Contains(" fast", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var categories = new List<ProviderSpendCategory>();
        if (day.RegularEstimatedCostUsd > 0)
        {
            categories.Add(new ProviderSpendCategory("regular", day.RegularEstimatedCostUsd));
        }

        if (day.FastEstimatedCostUsd > 0)
        {
            categories.Add(new ProviderSpendCategory("fast", day.FastEstimatedCostUsd));
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
