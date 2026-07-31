using System;
using System.Collections.Generic;
using System.Linq;
using CodexBarWindows;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBar.WinUI;

/// <summary>
/// The tray flyout, and the app's primary surface: a provider tab strip, the selected
/// provider's rate-limit windows, the Codex banked-reset row and a status line.
/// </summary>
/// <remarks>
/// Chrome-less, always on top, hidden from the taskbar and Alt-Tab, rounded, backed by the
/// configured system material, anchored next to the notification area and dismissed when focus
/// leaves the app. It is HIDDEN, never closed, so it keeps its state and its (expensive) XAML
/// tree between shows.
/// </remarks>
public sealed partial class FlyoutWindow : Window
{
    // Logical (DIP) design size; converted to physical pixels before positioning.
    private const int WidthDip = 400;
    private const int FallbackHeightDip = 320;
    private const int MinHeightDip = 160;
    private const int MarginDip = 12;

    /// <summary>Re-show debounce: a tray click deactivates the flyout before we see the click.</summary>
    private static readonly TimeSpan ReopenDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Used-percent above which a reset clearly has something to reset. This only adapts the
    /// wording - redeeming stays available at any usage, because whether a credit is worth
    /// spending is the account holder's call, not ours.
    /// </summary>
    private const double ResetEligibleUsedPercent = 95;

    /// <summary>Window within which an unspent credit is close enough to expiry to flag.</summary>
    private static readonly TimeSpan ResetExpiryWarningWindow = TimeSpan.FromDays(2);

    private readonly IntPtr hwnd;
    private readonly DispatcherQueue queue;
    private readonly DispatcherQueueTimer foregroundWatch;
    private readonly UsageRefreshService service;
    private readonly List<ProviderDescriptor> providers = [];

    private FlyoutPalette palette;
    private string selectedProviderKey = ProviderKeys.Codex("default");
    private string statusFullText = string.Empty;
    private bool confirmingRedeem;
    private bool isOpen;
    private bool hasBeenForeground;
    private bool anchorToBottom = true;
    private DateTime lastHiddenUtc = DateTime.MinValue;

    public event EventHandler? GraphsRequested;

    public FlyoutWindow(UsageRefreshService service)
    {
        this.service = service;

        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        queue = DispatcherQueue.GetForCurrentThread();
        palette = FlyoutPalette.For(RootGrid);

        Title = AppInfo.AppName;

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        // hasBorder stays true so Windows 11 still rounds and shadows the window.
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;

        // Never set a window region here: it permanently defeats DWM rounding.
        NativeWindow.ApplyRoundedCorners(hwnd);

        CloseButton.Click += (_, _) => HideFlyout();
        // The flyout deliberately stays open when the graphs window takes focus, so changes
        // can be watched live side by side.
        GraphsButton.Click += (_, _) => GraphsRequested?.Invoke(this, EventArgs.Empty);
        RefreshButton.Click += (_, _) => RequestRefresh();
        RedeemButton.Click += (_, _) => BeginConfirmRedeem();
        ConfirmRedeemButton.Click += (_, _) => CommitRedeem();
        CancelRedeemButton.Click += (_, _) =>
        {
            confirmingRedeem = false;
            Render();
        };

        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(Windows.System.VirtualKey.Escape, HideFlyout));
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(Windows.System.VirtualKey.F5, RequestRefresh));
        RootGrid.ActualThemeChanged += (_, _) => OnActualThemeChanged();

        AppTheme.Changed += OnThemeChanged;
        ApplyTheme();

        service.UsageUpdated += OnUsageUpdated;
        service.RefreshingChanged += OnRefreshingChanged;
        service.ResetCreditStateChanged += OnResetCreditStateChanged;
        service.CodexEntriesChanged += ConfigureProviders;

        ConfigureProviders();

        Activated += OnActivated;

        foregroundWatch = queue.CreateTimer();
        foregroundWatch.Interval = TimeSpan.FromMilliseconds(250);
        foregroundWatch.Tick += (_, _) => CheckForegroundOwnership();
    }

    public bool IsOpen => isOpen;

    public string SelectedProvider => selectedProviderKey;

    /// <summary>
    /// Overrides the status line with a one-off notice (used for update-check results). The
    /// next render replaces it, which is the intent: it is a notice, not state.
    /// </summary>
    public void SetStatus(string text) => ApplyStatus(text, StatusSeverity.Info);

    public void Toggle()
    {
        if (isOpen)
        {
            HideFlyout();
        }
        else if (DateTime.UtcNow - lastHiddenUtc > ReopenDebounce)
        {
            ShowFlyout();
        }
    }

    public void ShowFlyout()
    {
        // Capture the cursor BEFORE showing anything: it is next to the tray icon that was
        // clicked, and is what picks the monitor on a multi-display setup.
        anchorPoint = NativeWindow.TryGetCursorPosition();

        // Render before the window is placed: PositionNearTray sizes to the measured content,
        // so the content has to be the CURRENT content or the first frame is the wrong height.
        Render();
        PositionNearTray();
        AppWindow.Show(activateWindow: true);

        hasBeenForeground = false;
        isOpen = true;

        // A tray click leaves the shell in the foreground, so a plain SetForegroundWindow is
        // refused; without foreground the window can never observe LOSING it either.
        NativeWindow.ForceForeground(hwnd);
        RefreshButton.Focus(FocusState.Programmatic);

        foregroundWatch.Start();

        // The visibility gate: the poll timer only runs while something is showing usage.
        service.SetWindowOpen(WindowId, true);
        service.Refresh();

        DiagnosticLog.Write(
            "flyout shown foregroundIsOurs={0} polling={1}",
            NativeWindow.ForegroundBelongsToThisProcess(),
            service.IsPolling);
    }

    public void HideFlyout()
    {
        if (!isOpen)
        {
            return;
        }

        foregroundWatch.Stop();
        // Hide, never Close: Close destroys the XAML tree (and, without
        // DispatcherShutdownMode.OnExplicitShutdown, would end the whole app).
        AppWindow.Hide();
        isOpen = false;
        lastHiddenUtc = DateTime.UtcNow;

        service.SetWindowOpen(WindowId, false);

        // A pending "spend this credit?" confirm must not be waiting one click away the next
        // time the flyout opens, and last session's outcome notes are stale.
        confirmingRedeem = false;
        service.ClearResetCreditMessages();

        DiagnosticLog.Write("flyout hidden polling={0}", service.IsPolling);
    }

    /// <summary>
    /// Detaches everything that could run during teardown, then closes for real. Without this
    /// the foreground watchdog and the Activated handler keep firing against a window that is
    /// already being destroyed.
    /// </summary>
    public void ShutDown()
    {
        foregroundWatch.Stop();
        isOpen = false;
        Activated -= OnActivated;
        AppTheme.Changed -= OnThemeChanged;
        service.UsageUpdated -= OnUsageUpdated;
        service.RefreshingChanged -= OnRefreshingChanged;
        service.ResetCreditStateChanged -= OnResetCreditStateChanged;
        service.CodexEntriesChanged -= ConfigureProviders;
        service.SetWindowOpen(WindowId, false);
        Close();
    }

    /// <summary>
    /// Re-runs the dismiss test. Sibling windows (settings, graphs) call this when THEY lose
    /// activation: the flyout sees no event of its own in that case, so without re-arming it
    /// would stay open forever after the user clicked away from a sibling window.
    /// </summary>
    public void ReArmDismissCheck()
    {
        if (isOpen)
        {
            ScheduleDismissCheck();
        }
    }

    private const string WindowId = "flyout";

    // ---------------------------------------------------------------- providers

    private sealed record ProviderDescriptor(string Key, string Name, UsageProvider Provider)
    {
        public bool IsClaude => Provider == UsageProvider.Claude;

        public bool IsCursor => Provider == UsageProvider.Cursor;
    }

    /// <summary>
    /// Rebuilds the tab strip from the configured Codex accounts and the per-tool opt-outs.
    /// </summary>
    private void ConfigureProviders()
    {
        var settings = AppTheme.Settings;
        var descriptors = service.CodexEntries
            .Select(entry => new ProviderDescriptor(ProviderKeys.Codex(entry.Id), entry.Name, UsageProvider.Codex))
            .Append(new ProviderDescriptor(ProviderKeys.Claude, "Claude", UsageProvider.Claude))
            .Append(new ProviderDescriptor(ProviderKeys.Cursor, "Cursor", UsageProvider.Cursor))
            .Where(descriptor => settings.IsProviderEnabled(descriptor.Provider))
            .ToList();

        // Everything disabled would leave a chrome-only flyout with no way back, so the tab
        // strip always keeps at least Codex.
        if (descriptors.Count == 0)
        {
            descriptors.Add(new ProviderDescriptor(ProviderKeys.Codex("default"), "Codex", UsageProvider.Codex));
        }

        providers.Clear();
        providers.AddRange(descriptors);

        if (providers.All(provider => provider.Key != selectedProviderKey))
        {
            selectedProviderKey = providers[0].Key;
        }

        Render();
    }

    private ProviderDescriptor CurrentProvider =>
        providers.FirstOrDefault(provider => provider.Key == selectedProviderKey)
        ?? providers.FirstOrDefault()
        ?? new ProviderDescriptor(ProviderKeys.Codex("default"), "Codex", UsageProvider.Codex);

    private void OnProviderTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string providerKey })
        {
            return;
        }

        if (providerKey == selectedProviderKey)
        {
            // Clicking the selected tab unchecked it; the re-render restores the state.
            Render();
            return;
        }

        selectedProviderKey = providerKey;
        // A half-finished confirm belongs to the account it was started on.
        confirmingRedeem = false;
        Render();

        if (!service.GetUsage(providerKey).HasSnapshot)
        {
            service.Refresh();
        }
    }

    // ---------------------------------------------------------------- rendering

    private void OnUsageUpdated(string providerKey, ProviderUsageLookupResult result)
    {
        if (providerKey == selectedProviderKey)
        {
            Render();
        }
    }

    private void OnResetCreditStateChanged(string providerKey, ResetCreditState state)
    {
        if (providerKey == selectedProviderKey)
        {
            if (state.Busy)
            {
                confirmingRedeem = false;
            }

            Render();
        }
    }

    private void OnRefreshingChanged(bool refreshing)
    {
        RefreshButton.IsEnabled = !refreshing;
        RefreshGlyph.Visibility = refreshing ? Visibility.Collapsed : Visibility.Visible;
        RefreshSpinner.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
        RefreshSpinner.IsActive = refreshing;
        Render();
    }

    /// <summary>Rebuilds every data-driven surface for the selected provider, then resizes.</summary>
    private void Render()
    {
        var provider = CurrentProvider;
        var result = service.GetUsage(provider.Key);

        RenderTabs();

        TitleText.Text = $"{provider.Name} rate limits";
        PlanText.Text = BuildPlanText(provider, result.Snapshot);

        RenderUsageRows(provider, result);
        RenderResetRow(provider, result);
        RenderStatus(provider, result);

        ResizeToContent();
    }

    /// <summary>
    /// Rebuilds the tab strip as real controls rather than a templated ItemsControl.
    /// </summary>
    /// <remarks>
    /// The icon is a vector element, and hosting a <c>UIElement</c> through a templated
    /// <c>ContentPresenter</c> crashed the process outright (0xc000027b out of
    /// CoreMessagingXP). Building the handful of buttons directly is both simpler and stable.
    /// It stays theme-safe because every control keeps its stock style - whose setters are
    /// <c>ThemeResource</c>s that re-resolve per element - and the only colour set here comes
    /// from <see cref="palette"/>, which is rebuilt whenever the actual theme changes.
    /// </remarks>
    private void RenderTabs()
    {
        ProviderTabs.Children.Clear();
        foreach (var provider in providers)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            // Anthropic's mark keeps its own colour; the other two read as monochrome UI.
            var icon = ProviderGeometry.CreateIcon(
                provider.Provider,
                provider.IsClaude ? palette.ClaudeGlyph : palette.Glyph);
            if (icon is not null)
            {
                content.Children.Add(icon);
            }

            content.Children.Add(new TextBlock
            {
                Text = provider.Name,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center
            });

            var tab = new ToggleButton
            {
                Content = content,
                Tag = provider.Key,
                MinWidth = 0,
                Padding = new Thickness(10, 4, 12, 4),
                CornerRadius = new CornerRadius(14),
                IsChecked = provider.Key == selectedProviderKey
            };
            AutomationProperties.SetName(tab, provider.Name);
            tab.Click += OnProviderTabClick;
            ProviderTabs.Children.Add(tab);
        }
    }

    private string BuildPlanText(ProviderDescriptor provider, ProviderUsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return provider.IsCursor
                ? "Waiting for Cursor usage data"
                : $"Waiting for local {provider.Name} usage data";
        }

        var plan = provider.IsCursor
            ? string.IsNullOrWhiteSpace(snapshot.PlanType) ? "Cursor usage data" : snapshot.PlanType
            : string.IsNullOrWhiteSpace(snapshot.PlanType)
                ? provider.IsClaude ? "Claude Code usage data" : "Codex CLI usage data"
                : $"{ProviderPlanFormatter.DisplayName(provider.Provider, snapshot.PlanType)} plan";

        // The account line: which login these numbers belong to. Only shown when the reader
        // actually knows it, so a provider that cannot report it does not grow an empty dot.
        return string.IsNullOrWhiteSpace(snapshot.AccountEmail)
            ? plan
            : $"{plan}  ·  {snapshot.AccountEmail}";
    }

    private void RenderUsageRows(ProviderDescriptor provider, ProviderUsageLookupResult result)
    {
        UsageRows.Items.Clear();

        if (result.Snapshot is not { } snapshot)
        {
            var loading = service.IsRefreshing;
            var titles = provider.IsCursor
                ? new[] { "Total", "Auto", "API" }
                : ["5 hour limit", "Weekly limit"];

            for (var index = 0; index < titles.Length; index++)
            {
                UsageRows.Items.Add(new UsageRowModel(
                    titles[index],
                    loading ? "…" : "--",
                    0,
                    palette.Accent,
                    loading ? "Fetching usage…" : "-- remaining",
                    loading ? "Reset pending" : "Reset unknown",
                    showSeparator: index > 0,
                    isIndeterminate: loading));
            }

            return;
        }

        var windows = snapshot.Windows;
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            UsageRows.Items.Add(new UsageRowModel(
                window.Title,
                $"{window.UsedPercent:0.#}%",
                Math.Clamp(window.UsedPercent, 0, 100),
                palette.Heat(window.UsedPercent),
                $"{window.RemainingPercent:0.#}% remaining",
                window.ResetsAt is { } resetAt ? $"Resets {FormatReset(resetAt)}" : "Reset unknown",
                showSeparator: index > 0,
                isIndeterminate: false));
        }
    }

    // ---------------------------------------------------------------- reset credits

    private CodexResetCredits Credits => service.GetUsage(selectedProviderKey).Snapshot?.ResetCredits ?? CodexResetCredits.None;

    /// <summary>The credit that would actually be spent: use-it-or-lose-it, soonest expiry first.</summary>
    private CodexResetCredit? OfferedCredit => Credits.NextExpiring;

    private void RenderResetRow(ProviderDescriptor provider, ProviderUsageLookupResult result)
    {
        var state = service.GetResetCreditState(provider.Key);
        var credits = result.Snapshot?.ResetCredits ?? CodexResetCredits.None;
        var show = provider.Provider == UsageProvider.Codex && (state.HasSomethingToSay || credits.HasAny);

        ResetRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ResetSeparator.Visibility = UsageRows.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        if (state.Busy)
        {
            ResetBadge.Visibility = Visibility.Collapsed;
            ResetTitleText.Text = "Applying reset…";
            ResetDetailText.Text = "Asking Codex to redeem this credit";
            ResetDetailText.Foreground = null;
            ResetBusyRing.Visibility = Visibility.Visible;
            ResetBusyRing.IsActive = true;
            RedeemButton.Visibility = Visibility.Collapsed;
            ConfirmRedeemButton.Visibility = Visibility.Collapsed;
            CancelRedeemButton.Visibility = Visibility.Collapsed;
            return;
        }

        ResetBusyRing.IsActive = false;
        ResetBusyRing.Visibility = Visibility.Collapsed;

        var offered = credits.NextExpiring;
        var expiringSoon = offered?.ExpiresAt is { } expiry && expiry - DateTimeOffset.Now <= ResetExpiryWarningWindow;

        // A refresh that replaced the offered credit must not leave a confirm on screen naming
        // one credit while a different one would actually be spent.
        if (confirmingRedeem && offered is null)
        {
            confirmingRedeem = false;
        }

        if (confirmingRedeem && offered is { } pending)
        {
            var nearLimit = IsNearLimit(result.Snapshot);
            ResetBadge.Visibility = Visibility.Collapsed;
            ResetTitleText.Text = $"Use “{pending.DisplayTitle}”?";
            // Below the eligibility mark the spend may reset nothing, which is the one thing
            // worth saying at the moment of commitment.
            ResetDetailText.Text = nearLimit
                ? "This can't be undone"
                : "Nothing's near a limit — may reset nothing";
            ResetDetailText.Foreground = nearLimit ? null : palette.Warning;
            RedeemButton.Visibility = Visibility.Collapsed;
            ConfirmRedeemButton.Visibility = Visibility.Visible;
            CancelRedeemButton.Visibility = Visibility.Visible;
            return;
        }

        ResetBadge.Visibility = Visibility.Visible;
        ResetBadge.Background = expiringSoon ? palette.Warning : palette.Accent;
        ResetBadgeText.Foreground = expiringSoon ? palette.OnWarningText : palette.OnAccentText;
        ResetBadgeText.Text = credits.AvailableCount > 99 ? "99+" : credits.AvailableCount.ToString();

        ResetTitleText.Text = credits.AvailableCount == 1 ? "reset available" : "resets available";
        ResetDetailText.Text = state.Message ?? DescribeInventory(offered);
        ResetDetailText.Foreground = state.Message is null && expiringSoon ? palette.Warning : null;

        RedeemButton.Visibility = Visibility.Visible;
        // Spending below the eligibility mark is the account holder's call, so the only thing
        // that can disable this is having no id to charge.
        RedeemButton.IsEnabled = offered is not null;
        ConfirmRedeemButton.Visibility = Visibility.Collapsed;
        CancelRedeemButton.Visibility = Visibility.Collapsed;
    }

    private static string DescribeInventory(CodexResetCredit? offered)
    {
        if (offered is null)
        {
            // Count without detail rows: there is no id to charge, so redeeming has to happen
            // in the Codex CLI rather than here.
            return "Redeem these from the Codex CLI";
        }

        return offered.ExpiresAt is { } expiry ? $"Next expires {FormatExpiry(expiry)}" : "These don't expire";
    }

    /// <summary>
    /// Whether any usage window is close enough to exhaustion for a reset to have something to
    /// reset. Every window matters: the weekly cap blocks work just as the 5 hour one does.
    /// </summary>
    private static bool IsNearLimit(ProviderUsageSnapshot? snapshot) =>
        snapshot is not null && snapshot.Windows.Any(window => window.UsedPercent >= ResetEligibleUsedPercent);

    private void BeginConfirmRedeem()
    {
        if (OfferedCredit is null)
        {
            return;
        }

        confirmingRedeem = true;
        Render();
    }

    private void CommitRedeem()
    {
        var providerKey = selectedProviderKey;
        var credit = OfferedCredit;

        // Re-check against the snapshot the row was rendered from: a refresh may have landed
        // between render and confirm, and the credit must belong to the account being charged.
        if (credit is null ||
            CurrentProvider.Provider != UsageProvider.Codex ||
            Credits.Find(credit.Id) is null)
        {
            confirmingRedeem = false;
            ApplyStatus("That reset is no longer available. Refreshing…", StatusSeverity.Warning);
            service.Refresh();
            return;
        }

        confirmingRedeem = false;
        service.RedeemResetCredit(new CodexResetRedeemRequest(providerKey, credit));
    }

    // ---------------------------------------------------------------- status line

    private enum StatusSeverity
    {
        Info,
        Warning,
        Error
    }

    private void RenderStatus(ProviderDescriptor provider, ProviderUsageLookupResult result)
    {
        if (result.Snapshot is not { } snapshot)
        {
            var message = result.Error ?? "No usage data found.";
            ApplyStatus(message, result.Error is null ? StatusSeverity.Info : StatusSeverity.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            // A retained (stale) snapshot says so explicitly, so an error paired with old
            // numbers cannot read as if the numbers were just fetched.
            var text = result.IsStale
                ? $"{result.Error}  ·  showing limits from {FormatObservedAt(snapshot.ObservedAt)}"
                : result.Error;
            ApplyStatus(text, StatusSeverity.Error);
            return;
        }

        if (service.IsRefreshing)
        {
            ApplyStatus($"Refreshing {provider.Name} limits…", StatusSeverity.Info);
            return;
        }

        var fetched = $"Updated {FormatObservedAt(snapshot.ObservedAt)}";
        var prefix = provider.IsCursor ? CursorCostText(snapshot) : string.Empty;
        ApplyStatus(string.IsNullOrWhiteSpace(prefix) ? fetched : $"{prefix}  ·  {fetched}", StatusSeverity.Info);
    }

    /// <summary>
    /// Writes the status line. Errors keep their FULL text: wrapped to at most three lines on
    /// screen, complete in the tooltip, and copyable from the right-click menu. The WinForms
    /// popup single-lined and ellipsised this, which routinely cut the only explanation the
    /// app ever gives for missing numbers.
    /// </summary>
    private void ApplyStatus(string text, StatusSeverity severity)
    {
        statusFullText = text;
        StatusText.Text = text;
        ToolTipService.SetToolTip(StatusText, string.IsNullOrWhiteSpace(text) ? null : text);

        switch (severity)
        {
            case StatusSeverity.Error:
                StatusText.Foreground = palette.Danger;
                StatusIcon.Foreground = palette.Danger;
                StatusIcon.Visibility = Visibility.Visible;
                break;
            case StatusSeverity.Warning:
                StatusText.Foreground = palette.Warning;
                StatusIcon.Foreground = palette.Warning;
                StatusIcon.Visibility = Visibility.Visible;
                break;
            default:
                // Cleared so the XAML {ThemeResource} tertiary brush takes over again.
                StatusText.ClearValue(TextBlock.ForegroundProperty);
                StatusIcon.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void OnCopyStatus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(statusFullText))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(statusFullText);
        Clipboard.SetContent(package);
    }

    // ---------------------------------------------------------------- refresh

    /// <summary>
    /// The manual refresh path (button and F5). The service owns the debounce, so a click-storm
    /// cannot stack refreshes; a swallowed request says so instead of silently doing nothing.
    /// </summary>
    private void RequestRefresh()
    {
        if (service.RequestManualRefresh())
        {
            DiagnosticLog.Write("manual refresh accepted");
            Render();
            return;
        }

        DiagnosticLog.Write("manual refresh debounced inFlight={0}", service.IsRefreshing);
        if (!service.IsRefreshing)
        {
            ApplyStatus("Just refreshed — try again in a moment.", StatusSeverity.Info);
        }
    }

    // ---------------------------------------------------------------- formatting

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

    private static string FormatReset(DateTimeOffset resetAt)
    {
        var remaining = resetAt - DateTimeOffset.Now;
        if (remaining.TotalSeconds <= 0)
        {
            return "now";
        }

        var relative = remaining.TotalDays >= 1
            ? $"in {(int)remaining.TotalDays}d {remaining.Hours}h"
            : remaining.TotalHours >= 1
                ? $"in {(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";

        return $"{relative}, {resetAt.ToLocalTime():h:mm tt}";
    }

    private static string FormatExpiry(DateTimeOffset expiresAt)
    {
        var remaining = expiresAt - DateTimeOffset.Now;
        if (remaining.TotalSeconds <= 0)
        {
            return "now";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"in {(int)remaining.TotalDays}d";
        }

        return remaining.TotalHours >= 1
            ? $"in {(int)remaining.TotalHours}h"
            : $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    private static string CursorCostText(ProviderUsageSnapshot snapshot)
    {
        if (snapshot.Cost is not { } cost)
        {
            return string.Empty;
        }

        var used = FormatCurrency(cost.Used, cost.CurrencyCode);
        var budget = cost.Limit is { } limit && limit > 0
            ? $" / {FormatCurrency(limit, cost.CurrencyCode)}"
            : string.Empty;
        return $"On-demand {used}{budget}";
    }

    private static string FormatCurrency(decimal value, string currencyCode)
    {
        if (!string.Equals(currencyCode, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return $"{value:0.##} {currencyCode}";
        }

        return value <= 0 ? "$0.00" : value < 0.01m ? "<$0.01" : $"${value:0.00}";
    }

    // ---------------------------------------------------------------- focus / theme

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            DiagnosticLog.Write("flyout deactivated open={0} sawForeground={1}", isOpen, hasBeenForeground);
            ScheduleDismissCheck();
            return;
        }

        hasBeenForeground = true;
    }

    /// <summary>
    /// Deferred so the check runs AFTER the activation change settles and Windows has published
    /// the new foreground window.
    /// </summary>
    private void ScheduleDismissCheck() => queue.TryEnqueue(CheckForegroundOwnership);

    /// <summary>
    /// The dismiss rule, ported from the WinForms popup: hide only when the foreground window
    /// belongs to ANOTHER PROCESS. Checking process rather than window is what keeps the tray
    /// context menu, the settings window and the graphs window from dismissing the flyout.
    /// <para>
    /// Runs both from activation changes and from a low-frequency poll, because a WinUI window
    /// that never managed to take the foreground never raises Deactivated either. The
    /// <see cref="hasBeenForeground"/> gate is the safety valve: until the flyout has actually
    /// held focus once, nothing here can dismiss it, so a failed foreground grab degrades to
    /// "stays open until clicked again" rather than "closes itself immediately".
    /// </para>
    /// </summary>
    private void CheckForegroundOwnership()
    {
        if (!isOpen)
        {
            return;
        }

        if (NativeWindow.ForegroundBelongsToThisProcess())
        {
            hasBeenForeground = true;
            return;
        }

        if (!hasBeenForeground)
        {
            return;
        }

        DiagnosticLog.Write("flyout dismissed: foreground left the process");
        HideFlyout();
    }

    private static KeyboardAccelerator CreateAccelerator(Windows.System.VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };

        return accelerator;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        // Tool opt-outs live in the same settings record, so the tab strip may have changed.
        ConfigureProviders();
    }

    /// <summary>
    /// The palette is derived from the element's ACTUAL theme, so it has to be rebuilt (and
    /// every model with it) whenever that resolves differently - otherwise heat colours and
    /// glyph tints freeze to whichever theme was current when the window was created.
    /// </summary>
    private void OnActualThemeChanged()
    {
        palette = FlyoutPalette.For(RootGrid);
        AppTheme.ApplyTint(RootGrid, TintLayer);
        Render();
    }

    private void ApplyTheme()
    {
        AppTheme.Apply(this, RootGrid, TintLayer);
        palette = FlyoutPalette.For(RootGrid);
    }

    // ---------------------------------------------------------------- geometry

    /// <summary>
    /// Anchors the flyout to the work-area corner next to the notification area. WorkArea
    /// excludes the taskbar, so comparing it with OuterBounds says which edge the taskbar is on.
    /// All AppWindow geometry is in PHYSICAL pixels, hence the DPI scaling.
    /// </summary>
    private void PositionNearTray()
    {
        var work = WorkArea(out var outer);
        var scale = NativeWindow.ScaleFor(hwnd);
        var margin = (int)Math.Round(MarginDip * scale);
        var width = (int)Math.Round(WidthDip * scale);
        var height = MeasuredHeightPixels(work, scale);

        // Default: bottom-right, i.e. a bottom or right taskbar.
        var x = work.X + work.Width - width - margin;
        var y = work.Y + work.Height - height - margin;
        anchorToBottom = true;

        if (work.Y > outer.Y)
        {
            y = work.Y + margin;            // taskbar on top
            anchorToBottom = false;
        }
        else if (work.X > outer.X)
        {
            x = work.X + margin;            // taskbar on the left
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>
    /// Grows and shrinks the window with its content while keeping the edge nearest the tray
    /// pinned. Re-deriving the position from the work area instead would teleport a flyout the
    /// user had moved, and would make every provider switch jump.
    /// </summary>
    private void ResizeToContent()
    {
        if (!isOpen)
        {
            return;
        }

        var work = WorkArea(out _);
        var scale = NativeWindow.ScaleFor(hwnd);
        var height = MeasuredHeightPixels(work, scale);
        var size = AppWindow.Size;
        if (size.Height == height)
        {
            return;
        }

        var position = AppWindow.Position;
        var y = anchorToBottom ? position.Y + size.Height - height : position.Y;
        AppWindow.MoveAndResize(new RectInt32(position.X, y, size.Width, height));
    }

    /// <summary>
    /// Cursor position captured when the tray was clicked, used to pick the display.
    /// </summary>
    /// <remarks>
    /// Resolving the display from the flyout's OWN window handle picks whichever monitor the
    /// hidden window happens to sit on — on first show that is WinUI's default placement, so a
    /// tray click on a secondary monitor opened the flyout on the primary one. The tray icon
    /// that was clicked is next to the cursor, so the cursor is the correct anchor. The WinForms
    /// original used Cursor.Position for exactly this reason.
    /// </remarks>
    private PointInt32? anchorPoint;

    private RectInt32 WorkArea(out RectInt32 outerBounds)
    {
        var displayArea = anchorPoint is { } anchor
            ? DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd), DisplayAreaFallback.Nearest);

        outerBounds = displayArea.OuterBounds;
        return displayArea.WorkArea;
    }

    /// <summary>
    /// The height the content actually wants, clamped to the screen. The row count depends on
    /// the provider (Cursor has three windows, Codex two plus an optional reset row), so a
    /// fixed height would leave dead space or clip depending on the selected tab.
    /// </summary>
    private int MeasuredHeightPixels(RectInt32 work, double scale)
    {
        var maxHeightDip = (work.Height / scale) - (2 * MarginDip);
        RootGrid.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        var desired = RootGrid.DesiredSize.Height;
        if (double.IsNaN(desired) || desired < 1)
        {
            desired = FallbackHeightDip;
        }

        var clamped = Math.Clamp(Math.Ceiling(desired), MinHeightDip, Math.Max(MinHeightDip, maxHeightDip));
        return (int)Math.Round(clamped * scale);
    }
}
