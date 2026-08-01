using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CodexBarWindows;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBar.WinUI;

/// <summary>
/// The tray flyout, and the app's primary surface: every enabled provider's rate-limit windows
/// in one compact list, the Codex banked-reset rows and a status line.
/// </summary>
/// <remarks>
/// Chrome-less, always on top, hidden from the taskbar and Alt-Tab, rounded, backed by the
/// configured system material, anchored next to the notification area and dismissed when focus
/// leaves the app. It is HIDDEN, never closed, so it keeps its state and its (expensive) XAML
/// tree between shows. The header doubles as a drag handle (see
/// <see cref="OnHeaderPointerPressed"/>).
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

    /// <summary>Live group cards, keyed by provider key. Reused across renders, never rebuilt.</summary>
    private readonly Dictionary<string, ProviderGroupView> groups = new(StringComparer.Ordinal);

    private FlyoutPalette palette;
    private string statusFullText = string.Empty;
    private bool renderQueued;
    private bool isDragging;
    private bool isOpen;

    /// <summary>
    /// Set by <see cref="ShutDown"/>. Unlike the other two windows this one is HIDDEN rather than
    /// closed for its whole life, so it is only ever true during app exit - but the hazard is the
    /// same one <c>GraphsWindow.isClosed</c> guards: <see cref="RequestRender"/> defers a render
    /// to the end of the dispatcher turn, and a request made just before the shutdown would
    /// otherwise be delivered to a window whose XAML tree is already gone.
    /// </summary>
    private bool isClosed;
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
        // clicked, and is what picks the monitor on a multi-display setup. This also discards
        // whatever display a previous drag left the window on - by policy every open re-anchors
        // to the tray corner.
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
        foreach (var group in groups.Values)
        {
            group.Confirming = false;
        }

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
        // Set FIRST, so a render already queued for the end of this turn finds the window shut
        // rather than half-detached.
        isClosed = true;
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

        public bool IsGrok => Provider == UsageProvider.Grok;

        public bool IsCursor => Provider == UsageProvider.Cursor;
    }

    /// <summary>
    /// Rebuilds the provider list from the configured Codex accounts and the per-tool opt-outs.
    /// Every one of them is on screen at once, so this is purely about WHICH cards exist.
    /// </summary>
    private void ConfigureProviders()
    {
        var settings = AppTheme.Settings;
        var descriptors = service.CodexEntries
            .Select(entry => new ProviderDescriptor(ProviderKeys.Codex(entry.Id), entry.Name, UsageProvider.Codex))
            .Append(new ProviderDescriptor(ProviderKeys.Claude, "Claude", UsageProvider.Claude))
            .Append(new ProviderDescriptor(ProviderKeys.Grok, "Grok", UsageProvider.Grok))
            .Append(new ProviderDescriptor(ProviderKeys.Cursor, "Cursor", UsageProvider.Cursor))
            .Where(descriptor => settings.IsProviderEnabled(descriptor.Provider))
            .ToList();

        // Everything disabled would leave a chrome-only flyout with no way back, so the list
        // always keeps at least Codex.
        if (descriptors.Count == 0)
        {
            descriptors.Add(new ProviderDescriptor(ProviderKeys.Codex("default"), "Codex", UsageProvider.Codex));
        }

        providers.Clear();
        providers.AddRange(descriptors);

        Render();
    }

    // ---------------------------------------------------------------- render scheduling

    private void OnUsageUpdated(string providerKey, ProviderUsageLookupResult result) => RequestRender();

    private void OnResetCreditStateChanged(string providerKey, ResetCreditState state)
    {
        // A redemption that has already started must not leave its own confirm on screen.
        if (state.Busy && groups.TryGetValue(providerKey, out var group))
        {
            group.Confirming = false;
        }

        RequestRender();
    }

    private void OnRefreshingChanged(bool refreshing)
    {
        RefreshButton.IsEnabled = !refreshing;
        RefreshGlyph.Visibility = refreshing ? Visibility.Collapsed : Visibility.Visible;
        RefreshSpinner.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
        RefreshSpinner.IsActive = refreshing;
        RequestRender();
    }

    /// <summary>
    /// Coalesces renders that land in the same dispatcher turn.
    /// </summary>
    /// <remarks>
    /// A single refresh raises <c>RefreshingChanged(true)</c> synchronously on the caller's
    /// stack, then the data event, then <c>RefreshingChanged(false)</c> - and the manual path
    /// adds one more of its own. Rendering each of those separately is what produced the
    /// visible double animation. Rows survive a render now, so the duplicates are cheap, but
    /// they are still redundant work and still resize the window, so back-to-back requests are
    /// folded into one pass at the end of the turn.
    /// </remarks>
    private void RequestRender()
    {
        if (isClosed)
        {
            return;
        }

        if (renderQueued)
        {
            return;
        }

        renderQueued = true;
        if (!queue.TryEnqueue(() =>
        {
            renderQueued = false;
            if (!isClosed)
            {
                Render();
            }
        }))
        {
            renderQueued = false;
            Render();
        }
    }

    /// <summary>Updates every data-driven surface in place, then resizes.</summary>
    private void Render()
    {
        SyncGroups();

        DateTimeOffset? newest = null;
        foreach (var descriptor in providers)
        {
            var result = service.GetUsage(descriptor.Key);
            RenderGroup(groups[descriptor.Key], descriptor, result);

            if (result.Snapshot is { } snapshot && (newest is null || snapshot.ObservedAt > newest))
            {
                newest = snapshot.ObservedAt;
            }
        }

        RenderStatus(newest);
        ResizeToContent();
    }

    // ---------------------------------------------------------------- provider groups

    /// <summary>
    /// The retained visual + state for one provider card. Holding the elements (rather than
    /// re-creating them per render) is what keeps the meters, the hover state of the reset
    /// button and the scroll offset alive across a refresh.
    /// </summary>
    private sealed class ProviderGroupView
    {
        public required string Key { get; init; }

        public required ProviderDescriptor Descriptor { get; set; }

        public required Border Card { get; init; }

        /// <summary>Kept so the mark can be re-tinted when the actual theme flips.</summary>
        public Shape? IconShape { get; init; }

        public required TextBlock NameText { get; init; }

        public required TextBlock PlanText { get; init; }

        public required TextBlock AccountText { get; init; }

        public required ItemsControl Rows { get; init; }

        public ObservableCollection<UsageRowModel> RowModels { get; } = [];

        public required TextBlock ErrorText { get; init; }

        public required StackPanel ResetRow { get; init; }

        public required Border ResetBadge { get; init; }

        public required TextBlock ResetBadgeText { get; init; }

        public required TextBlock ResetTitleText { get; init; }

        public required TextBlock ResetDetailText { get; init; }

        public required ProgressRing ResetBusyRing { get; init; }

        public required Button RedeemButton { get; init; }

        public required Button ConfirmButton { get; init; }

        public required Button CancelButton { get; init; }

        /// <summary>
        /// Per-group, NOT per-window: with several accounts on screen a single flag would put
        /// the "use this reset?" confirm on the wrong account.
        /// </summary>
        public bool Confirming { get; set; }
    }

    /// <summary>Creates missing cards, drops departed ones and fixes the order. Nothing else.</summary>
    private void SyncGroups()
    {
        foreach (var key in groups.Keys.Where(key => providers.All(provider => provider.Key != key)).ToList())
        {
            ProviderGroups.Children.Remove(groups[key].Card);
            groups.Remove(key);
        }

        for (var index = 0; index < providers.Count; index++)
        {
            var descriptor = providers[index];
            if (!groups.TryGetValue(descriptor.Key, out var group))
            {
                group = CreateGroup(descriptor);
                groups[descriptor.Key] = group;
            }

            var current = ProviderGroups.Children.IndexOf(group.Card);
            if (current == index)
            {
                continue;
            }

            if (current >= 0)
            {
                ProviderGroups.Children.RemoveAt(current);
            }

            ProviderGroups.Children.Insert(index, group.Card);
        }
    }

    /// <summary>
    /// Resolves one of the group styles declared in <c>RootGrid.Resources</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>Application.Current.Resources</c>: that dictionary resolves against the
    /// app theme (which nothing sets) and hands back a captured brush, so a colour taken from it
    /// freezes to the theme that was current when the group was built. Styles from the window's own
    /// dictionary carry <c>ThemeResource</c> setters, which re-resolve per element against
    /// ActualTheme - so a forced theme and a live system flip both reach these groups.
    /// </remarks>
    private Style GroupStyle(string key) => (Style)RootGrid.Resources[key];

    private ProviderGroupView CreateGroup(ProviderDescriptor descriptor)
    {
        var icon = ProviderGeometry.CreateIcon(
            descriptor.Provider,
            ProviderGlyphBrush(descriptor, palette));

        var nameText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        var planText = new TextBlock
        {
            Style = GroupStyle("SecondaryCaptionStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var accountText = new TextBlock
        {
            Style = GroupStyle("TertiaryCaptionStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        if (icon is not null)
        {
            identity.Children.Add(icon);
        }

        identity.Children.Add(nameText);
        identity.Children.Add(planText);

        var header = new Grid { Padding = new Thickness(12, 8, 12, 4), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(identity);
        Grid.SetColumn(accountText, 1);
        header.Children.Add(accountText);

        var rows = new ItemsControl
        {
            ItemsSource = null,
            ItemTemplate = (DataTemplate)RootGrid.Resources["UsageRowTemplate"],
            Margin = new Thickness(0, 0, 0, 4)
        };

        // Errors WRAP and are never trimmed: an ellipsised error is the one thing that reliably
        // makes missing numbers unexplainable. Right-click copies the full text.
        var errorText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Margin = new Thickness(12, 0, 12, 8),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var errorMenu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem { Text = "Copy", MinWidth = 140 };
        copyItem.Click += (_, _) => CopyToClipboard(errorText.Text);
        errorMenu.Items.Add(copyItem);
        errorText.ContextFlyout = errorMenu;

        var group = new ProviderGroupView
        {
            Key = descriptor.Key,
            Descriptor = descriptor,
            IconShape = icon as Shape,
            NameText = nameText,
            PlanText = planText,
            AccountText = accountText,
            Rows = rows,
            ErrorText = errorText,
            Card = new Border { Style = GroupStyle("ProviderCardStyle") },
            ResetRow = new StackPanel { Visibility = Visibility.Collapsed },
            ResetBadge = new Border
            {
                Height = 16,
                MinWidth = 16,
                Padding = new Thickness(5, 0, 5, 0),
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center
            },
            ResetBadgeText = new TextBlock
            {
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            ResetTitleText = new TextBlock
            {
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            },
            ResetDetailText = new TextBlock
            {
                Style = GroupStyle("SecondaryCaptionStyle"),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            },
            ResetBusyRing = new ProgressRing
            {
                Width = 14,
                Height = 14,
                IsActive = false,
                Visibility = Visibility.Collapsed
            },
            // Neutral, not accent: this opens a confirm rather than committing, and a loud
            // primary button next to an irreversible spend invites the click we do not want.
            RedeemButton = new Button
            {
                Content = "Use",
                Padding = new Thickness(10, 2, 10, 2),
                MinWidth = 0,
                MinHeight = 0,
                FontSize = 12
            },
            ConfirmButton = new Button
            {
                Content = "Use it",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Padding = new Thickness(10, 2, 10, 2),
                MinWidth = 0,
                MinHeight = 0,
                FontSize = 12,
                Visibility = Visibility.Collapsed
            },
            CancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(10, 2, 10, 2),
                MinWidth = 0,
                MinHeight = 0,
                FontSize = 12,
                Visibility = Visibility.Collapsed
            }
        };

        group.ResetBadge.Child = group.ResetBadgeText;
        group.RedeemButton.Click += (_, _) => BeginConfirmRedeem(group);
        group.ConfirmButton.Click += (_, _) => CommitRedeem(group);
        group.CancelButton.Click += (_, _) =>
        {
            group.Confirming = false;
            RequestRender();
        };

        var resetActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        resetActions.Children.Add(group.ResetBusyRing);
        resetActions.Children.Add(group.RedeemButton);
        resetActions.Children.Add(group.ConfirmButton);
        resetActions.Children.Add(group.CancelButton);

        // ONE LINE, not a stacked title-and-detail block. Banked resets are read constantly and
        // acted on about once a week, so the old two-line card earned none of the height it took
        // from the meters above it. Title and detail sit side by side, the detail ellipsised,
        // which keeps the whole row the height of a single caption.
        var resetBody = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        resetBody.Children.Add(group.ResetTitleText);
        resetBody.Children.Add(group.ResetDetailText);

        var resetGrid = new Grid { Padding = new Thickness(12, 3, 12, 8), ColumnSpacing = 8 };
        resetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resetGrid.Children.Add(group.ResetBadge);
        Grid.SetColumn(resetBody, 1);
        resetGrid.Children.Add(resetBody);
        Grid.SetColumn(resetActions, 2);
        resetGrid.Children.Add(resetActions);

        var separator = new Border
        {
            Style = GroupStyle("ProviderSeparatorStyle"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        group.ResetRow.Children.Add(separator);
        group.ResetRow.Children.Add(resetGrid);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(rows);
        body.Children.Add(errorText);
        body.Children.Add(group.ResetRow);
        group.Card.Child = body;

        rows.ItemsSource = group.RowModels;
        AutomationProperties.SetName(group.Card, descriptor.Name);
        return group;
    }

    private void RenderGroup(ProviderGroupView group, ProviderDescriptor descriptor, ProviderUsageLookupResult result)
    {
        group.Descriptor = descriptor;

        // Cheap and unconditional: it is also how the mark follows a theme flip, since the
        // palette is rebuilt on ActualThemeChanged and the group visuals are not.
        if (group.IconShape is { } shape)
        {
            shape.Fill = ProviderGlyphBrush(descriptor, palette);
        }

        group.NameText.Text = descriptor.Name;

        var plan = BuildPlanText(descriptor, result.Snapshot);
        group.PlanText.Text = plan;
        group.PlanText.Visibility = string.IsNullOrEmpty(plan) ? Visibility.Collapsed : Visibility.Visible;

        var email = result.Snapshot?.AccountEmail;
        group.AccountText.Text = email ?? string.Empty;
        group.AccountText.Visibility = string.IsNullOrWhiteSpace(email) ? Visibility.Collapsed : Visibility.Visible;

        RenderRows(group, descriptor, result);
        RenderGroupError(group, result);
        RenderResetRow(group, descriptor, result);
    }

    /// <summary>
    /// The provider's own error, inline in its own card: with every provider on screen at once a
    /// single shared status line cannot say WHICH account failed.
    /// </summary>
    private void RenderGroupError(ProviderGroupView group, ProviderUsageLookupResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Error))
        {
            group.ErrorText.Text = string.Empty;
            group.ErrorText.Visibility = Visibility.Collapsed;
            return;
        }

        // A retained (stale) snapshot says so explicitly, so an error paired with old numbers
        // cannot read as if the numbers were just fetched.
        group.ErrorText.Text = result.IsStale && result.Snapshot is { } snapshot
            ? $"{result.Error}  ·  showing limits from {FormatObservedAt(snapshot.ObservedAt)}"
            : result.Error;
        group.ErrorText.Foreground = palette.Danger;
        group.ErrorText.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(group.ErrorText, group.ErrorText.Text);
    }

    private string BuildPlanText(ProviderDescriptor descriptor, ProviderUsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return service.IsRefreshing ? "·  fetching…" : "·  no data yet";
        }

        var segments = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.PlanType))
        {
            segments.Add(ProviderPlanFormatter.DisplayName(descriptor.Provider, snapshot.PlanType));
        }

        // Grok gets this line ONLY when it has no on-demand meter of its own. With a cap the reader
        // emits a secondary window carrying the same figure, so printing it here too put the same
        // number twice in one card; with spend but no cap there is no meter, and this is the only
        // place it can appear at all.
        var showsCost = descriptor.IsCursor || (descriptor.IsGrok && snapshot.Secondary is null);
        if (showsCost && CursorCostText(snapshot) is { Length: > 0 } cost)
        {
            segments.Add(cost);
        }

        return segments.Count == 0 ? string.Empty : $"·  {string.Join("  ·  ", segments)}";
    }

    // ---------------------------------------------------------------- usage rows

    /// <summary>One line of the compact layout, before it is applied to a retained row model.</summary>
    private readonly record struct RowSpec(
        string Key,
        string Title,
        string PercentText,
        double MeterValue,
        Brush Heat,
        string ResetText,
        string DetailText,
        bool IsIndeterminate);

    private void RenderRows(ProviderGroupView group, ProviderDescriptor descriptor, ProviderUsageLookupResult result)
    {
        var specs = new List<RowSpec>();

        if (result.Snapshot is not { } snapshot)
        {
            var loading = service.IsRefreshing;
            var titles = descriptor.IsCursor
                ? new[] { "Total", "Auto", "API" }
                : descriptor.IsGrok
                    ? ["Week"]
                    : ["5 hour limit", "Weekly limit"];

            for (var index = 0; index < titles.Length; index++)
            {
                specs.Add(new RowSpec(
                    $"{index}:{titles[index]}",
                    ShortWindowLabel(titles[index]),
                    loading ? "…" : "--",
                    0,
                    palette.Accent,
                    string.Empty,
                    loading ? "Fetching usage…" : "No usage data yet",
                    loading));
            }
        }
        else
        {
            var windows = snapshot.Windows;
            for (var index = 0; index < windows.Count; index++)
            {
                var window = windows[index];
                specs.Add(new RowSpec(
                    $"{index}:{window.Title}",
                    ShortWindowLabel(window.Title),
                    $"{window.UsedPercent:0.#}%",
                    Math.Clamp(window.UsedPercent, 0, 100),
                    palette.Heat(window.UsedPercent),
                    window.ResetsAt is { } resetAt ? $"·  {FormatResetShort(resetAt)}" : string.Empty,
                    BuildRowDetail(window),
                    false));
            }
        }

        SyncRows(group.RowModels, specs);
    }

    /// <summary>
    /// Applies the specs onto the EXISTING row models, adding and trimming only at the ends.
    /// Rebuilding the collection instead is what made every meter replay its slide-in.
    /// </summary>
    private static void SyncRows(ObservableCollection<UsageRowModel> models, List<RowSpec> specs)
    {
        for (var index = 0; index < specs.Count; index++)
        {
            var spec = specs[index];
            if (index >= models.Count)
            {
                models.Add(new UsageRowModel(spec.Key));
            }
            else if (models[index].Key != spec.Key)
            {
                // A different window in this slot is genuinely a different row; replacing it is
                // correct (and, being an ObservableCollection, only regenerates that container).
                models[index] = new UsageRowModel(spec.Key);
            }

            var model = models[index];
            model.Title = spec.Title;
            model.PercentText = spec.PercentText;
            model.HeatBrush = spec.Heat;
            model.ResetText = spec.ResetText;
            model.DetailText = spec.DetailText;
            model.IsIndeterminate = spec.IsIndeterminate;
            model.MeterValue = spec.MeterValue;
        }

        while (models.Count > specs.Count)
        {
            models.RemoveAt(models.Count - 1);
        }
    }

    /// <summary>
    /// The compact label for a rate-limit window title. Unknown titles are passed through
    /// UNCHANGED: a mangled or empty label is strictly worse than a long one.
    /// </summary>
    /// <remarks>
    /// The label column is a FIXED 42 DIP shared by every meter. Anything longer than about five
    /// characters ellipsises ("Weekl…"), so every known weekly/monthly title must collapse here —
    /// including Grok's billing strings — to the same "Week"/"Month" the Codex and Claude rows use.
    /// </remarks>
    private static string ShortWindowLabel(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        // Contains, not Equals: Grok and future readers may say "Weekly credits", "Weekly limit",
        // "7-day weekly window", etc. All of them must land on the same short label.
        if (title.Contains("weekly", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Week", StringComparison.OrdinalIgnoreCase))
        {
            return "Week";
        }

        if (title.Contains("monthly", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Month", StringComparison.OrdinalIgnoreCase))
        {
            return "Month";
        }

        // Grok's second meter. Nine characters ellipsises to "On-de…" in the 42 DIP label column,
        // which reads as a truncation bug rather than a label.
        if (title.Equals("On-demand", StringComparison.OrdinalIgnoreCase))
        {
            return "Extra";
        }

        if (title.StartsWith("Fable", StringComparison.OrdinalIgnoreCase))
        {
            return "Fable";
        }

        // "5 hour limit" / "3 hour limit" / "45 minute limit" - the readers build these from the
        // window length, so the number is the only part worth keeping.
        var parts = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out var amount))
        {
            if (parts[1].StartsWith("hour", StringComparison.OrdinalIgnoreCase))
            {
                return $"{amount}h";
            }

            if (parts[1].StartsWith("minute", StringComparison.OrdinalIgnoreCase))
            {
                return $"{amount}m";
            }
        }

        return title;
    }

    private static string BuildRowDetail(ProviderUsageWindow window)
    {
        var reset = window.ResetsAt is { } resetAt ? $"resets {FormatReset(resetAt)}" : "reset unknown";
        return $"{window.Title}  ·  {window.UsedPercent:0.#}% used  ·  {window.RemainingPercent:0.#}% remaining  ·  {reset}";
    }

    // ---------------------------------------------------------------- reset credits

    private void RenderResetRow(ProviderGroupView group, ProviderDescriptor descriptor, ProviderUsageLookupResult result)
    {
        var state = service.GetResetCreditState(descriptor.Key);
        var credits = result.Snapshot?.ResetCredits ?? CodexResetCredits.None;
        var show = descriptor.Provider == UsageProvider.Codex && (state.HasSomethingToSay || credits.HasAny);

        group.ResetRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        if (state.Busy)
        {
            group.ResetBadge.Visibility = Visibility.Collapsed;
            group.ResetTitleText.Text = "Applying reset…";
            group.ResetDetailText.Text = "·  Asking Codex to redeem this credit";
            group.ResetDetailText.ClearValue(TextBlock.ForegroundProperty);
            group.ResetBusyRing.Visibility = Visibility.Visible;
            group.ResetBusyRing.IsActive = true;
            group.RedeemButton.Visibility = Visibility.Collapsed;
            group.ConfirmButton.Visibility = Visibility.Collapsed;
            group.CancelButton.Visibility = Visibility.Collapsed;
            return;
        }

        group.ResetBusyRing.IsActive = false;
        group.ResetBusyRing.Visibility = Visibility.Collapsed;

        var offered = credits.NextExpiring;
        var expiringSoon = offered?.ExpiresAt is { } expiry && expiry - DateTimeOffset.Now <= ResetExpiryWarningWindow;

        // A refresh that replaced the offered credit must not leave a confirm on screen naming
        // one credit while a different one would actually be spent.
        if (group.Confirming && offered is null)
        {
            group.Confirming = false;
        }

        if (group.Confirming && offered is { } pending)
        {
            var nearLimit = IsNearLimit(result.Snapshot);
            group.ResetBadge.Visibility = Visibility.Collapsed;
            group.ResetTitleText.Text = $"Use “{pending.DisplayTitle}”?";
            // Below the eligibility mark the spend may reset nothing, which is the one thing
            // worth saying at the moment of commitment.
            group.ResetDetailText.Text = nearLimit
                ? "·  can't be undone"
                : "·  nothing's near a limit";
            if (nearLimit)
            {
                group.ResetDetailText.ClearValue(TextBlock.ForegroundProperty);
            }
            else
            {
                group.ResetDetailText.Foreground = palette.Warning;
            }

            group.RedeemButton.Visibility = Visibility.Collapsed;
            group.ConfirmButton.Visibility = Visibility.Visible;
            group.CancelButton.Visibility = Visibility.Visible;
            return;
        }

        group.ResetBadge.Visibility = Visibility.Visible;
        group.ResetBadge.Background = expiringSoon ? palette.Warning : palette.Accent;
        group.ResetBadgeText.Foreground = expiringSoon ? palette.OnWarningText : palette.OnAccentText;
        group.ResetBadgeText.Text = credits.AvailableCount > 99 ? "99+" : credits.AvailableCount.ToString();

        group.ResetTitleText.Text = credits.AvailableCount == 1 ? "reset available" : "resets available";
        group.ResetDetailText.Text = $"·  {state.Message ?? DescribeInventory(offered)}";
        if (state.Message is null && expiringSoon)
        {
            group.ResetDetailText.Foreground = palette.Warning;
        }
        else
        {
            group.ResetDetailText.ClearValue(TextBlock.ForegroundProperty);
        }

        group.RedeemButton.Visibility = Visibility.Visible;
        // Spending below the eligibility mark is the account holder's call, so the only thing
        // that can disable this is having no id to charge.
        group.RedeemButton.IsEnabled = offered is not null;
        group.ConfirmButton.Visibility = Visibility.Collapsed;
        group.CancelButton.Visibility = Visibility.Collapsed;
    }

    private static string DescribeInventory(CodexResetCredit? offered)
    {
        if (offered is null)
        {
            // Count without detail rows: there is no id to charge, so redeeming has to happen
            // in the Codex CLI rather than here.
            return "redeem from the Codex CLI";
        }

        return offered.ExpiresAt is { } expiry ? $"expires {FormatExpiry(expiry)}" : "no expiry";
    }

    /// <summary>
    /// Whether any usage window is close enough to exhaustion for a reset to have something to
    /// reset. Every window matters: the weekly cap blocks work just as the 5 hour one does.
    /// </summary>
    private static bool IsNearLimit(ProviderUsageSnapshot? snapshot) =>
        snapshot is not null && snapshot.Windows.Any(window => window.UsedPercent >= ResetEligibleUsedPercent);

    private void BeginConfirmRedeem(ProviderGroupView group)
    {
        if (OfferedCreditFor(group.Descriptor.Key) is null)
        {
            return;
        }

        group.Confirming = true;
        RequestRender();
    }

    private CodexResetCredits CreditsFor(string providerKey) =>
        service.GetUsage(providerKey).Snapshot?.ResetCredits ?? CodexResetCredits.None;

    /// <summary>The credit that would actually be spent: use-it-or-lose-it, soonest expiry first.</summary>
    private CodexResetCredit? OfferedCreditFor(string providerKey) => CreditsFor(providerKey).NextExpiring;

    private void CommitRedeem(ProviderGroupView group)
    {
        var providerKey = group.Descriptor.Key;
        var credit = OfferedCreditFor(providerKey);

        // Re-check against the snapshot the row was rendered from: a refresh may have landed
        // between render and confirm, and the credit must belong to the account being charged.
        if (credit is null ||
            group.Descriptor.Provider != UsageProvider.Codex ||
            CreditsFor(providerKey).Find(credit.Id) is null)
        {
            group.Confirming = false;
            ApplyStatus("That reset is no longer available. Refreshing…", StatusSeverity.Warning);
            service.Refresh();
            return;
        }

        group.Confirming = false;
        service.RedeemResetCredit(new CodexResetRedeemRequest(providerKey, credit));
    }

    // ---------------------------------------------------------------- status line

    private enum StatusSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// The GLOBAL status line: freshness only. Per-provider failures live in their own card, so
    /// this stays a single stable sentence no matter how many accounts are configured.
    /// </summary>
    private void RenderStatus(DateTimeOffset? newestObservedAt)
    {
        if (service.IsRefreshing)
        {
            ApplyStatus("Refreshing limits…", StatusSeverity.Info);
            return;
        }

        if (newestObservedAt is not { } observedAt)
        {
            ApplyStatus("No usage data found.", StatusSeverity.Info);
            return;
        }

        ApplyStatus($"Updated {FormatObservedAt(observedAt)}", StatusSeverity.Info);
    }

    /// <summary>
    /// Writes the status line. Text keeps its FULL form: wrapped to at most three lines on
    /// screen, complete in the tooltip, and copyable from the right-click menu. The WinForms
    /// popup single-lined and ellipsised this, which routinely cut the only explanation the
    /// app ever gives for missing numbers.
    /// </summary>
    private void ApplyStatus(string text, StatusSeverity severity)
    {
        statusFullText = text;
        StatusText.Text = text;
        ToolTipService.SetToolTip(StatusText, string.IsNullOrWhiteSpace(text) ? null : text);

        // Routine status goes BESIDE THE TITLE and the bottom bar collapses entirely - that row
        // was buying a permanent chin for the words "Updated just now". Anything the user needs to
        // act on keeps the bottom bar, which is the only place an error has room to wrap to three
        // lines and stay copyable.
        var routine = severity == StatusSeverity.Info;
        HeaderStatusText.Text = routine ? text : string.Empty;
        HeaderStatusText.Visibility = routine && !string.IsNullOrWhiteSpace(text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusBar.Visibility = routine ? Visibility.Collapsed : Visibility.Visible;

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
            // BeginRefresh already raised RefreshingChanged synchronously on this stack, so this
            // only coalesces into that render rather than adding one.
            RequestRender();
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

    /// <summary>
    /// The long form, for the row's tooltip: the countdown plus the instant it lands on.
    /// </summary>
    /// <remarks>
    /// THE DATE IS PART OF IT, not just the clock time. The row itself is now a pure countdown, so
    /// this is the only place the actual reset moment appears - and "4:30 AM" on its own is
    /// ambiguous for anything past today, which is exactly when someone hovers to ask.
    /// </remarks>
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

        var local = resetAt.ToLocalTime();
        var absolute = local.Date == DateTimeOffset.Now.Date
            ? local.ToString("h:mm tt")
            : remaining.TotalDays < 6
                ? local.ToString("ddd h:mm tt")
                : local.ToString("ddd dd MMM h:mm tt");

        return $"{relative}, {absolute}";
    }

    /// <summary>
    /// TIME LEFT, not the wall-clock instant: "in 2h 15m", "in 3d 9h". The exact reset time stays
    /// on the row's tooltip via <see cref="FormatReset"/>.
    /// </summary>
    /// <remarks>
    /// The question this column answers is "how long until I get my quota back", and a countdown
    /// answers it directly where a timestamp made the reader do the subtraction. It also removes
    /// the format zoo the absolute form needed to fit a fixed-width column - a bare clock time
    /// today, "Tmrw", a weekday, then a date - which stacked four different shapes down one column
    /// and left "Tmrw 9:37 AM" sitting under "Thu 4:30 AM" and "08 Aug 11:32 AM".
    ///
    /// One unit of precision below the leading one: the second unit is what distinguishes "in 3d"
    /// from "in 3d 23h", and anything finer is noise on a window that refreshes on a poll. Past a
    /// week the hours stop being interesting and are dropped. This is the same vocabulary as the
    /// reset-credit line's "expires in 11d", so a Codex card now reads in one voice.
    /// </remarks>
    private static string FormatResetShort(DateTimeOffset resetAt)
    {
        var remaining = resetAt - DateTimeOffset.Now;
        if (remaining.TotalSeconds <= 0)
        {
            return "now";
        }

        if (remaining.TotalDays >= 7)
        {
            return $"in {(int)remaining.TotalDays}d";
        }

        if (remaining.TotalDays >= 1)
        {
            var days = (int)remaining.TotalDays;
            return remaining.Hours > 0 ? $"in {days}d {remaining.Hours}h" : $"in {days}d";
        }

        if (remaining.TotalHours >= 1)
        {
            var hours = (int)remaining.TotalHours;
            return remaining.Minutes > 0 ? $"in {hours}h {remaining.Minutes}m" : $"in {hours}h";
        }

        // Never "in 0m" for a reset that has not happened yet.
        return $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
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

    private static Brush ProviderGlyphBrush(ProviderDescriptor descriptor, FlyoutPalette palette) =>
        descriptor.IsClaude
            ? palette.ClaudeGlyph
            : descriptor.IsGrok
                ? palette.GrokGlyph
                : palette.Glyph;

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
        // Tool opt-outs live in the same settings record, so the provider set may have changed.
        ConfigureProviders();
    }

    /// <summary>
    /// The palette is derived from the element's ACTUAL theme, so it has to be rebuilt (and
    /// every brush it handed out re-applied) whenever that resolves differently - otherwise heat
    /// colours and glyph tints freeze to whichever theme was current when the window was created.
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

    // ---------------------------------------------------------------- dragging

    /// <summary>
    /// Press-move-release dragging of the whole window from the header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This deliberately does NOT hand off to the system move loop
    /// (<c>ReleaseCapture</c> + <c>WM_NCLBUTTONDOWN</c>/<c>HTCAPTION</c>), which is the usual
    /// trick for a caption-less window. That loop ends when it sees the button go UP, and the
    /// button-up here is delivered to the XAML island's own child HWND rather than to this
    /// window - so the loop never ended, and the window stayed glued to the cursor until the
    /// next click. That reads as a "drag mode" toggle, not as dragging.
    /// </para>
    /// <para>
    /// Tracking the pointer ourselves is what gives normal hold-to-drag, release-to-drop. The
    /// arithmetic is done in SCREEN pixels from the cursor - <c>AppWindow.Position</c> is in
    /// physical pixels too, so nothing has to be scaled, and the window keeps up with the cursor
    /// across a DPI boundary.
    /// </para>
    /// <para>
    /// Dismissal is unaffected: the pointer is captured by an element in this window, so the
    /// foreground never leaves the process and <see cref="CheckForegroundOwnership"/> keeps
    /// seeing us. The buttons are excluded by walking up from the original source, so a press on
    /// refresh or close can never start a drag.
    /// </para>
    /// </remarks>
    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
            !e.GetCurrentPoint(ContentRoot).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (IsDragExempt(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (NativeWindow.TryGetCursorPosition() is not { } cursor)
        {
            return;
        }

        if (!ContentRoot.CapturePointer(e.Pointer))
        {
            return;
        }

        dragPointerId = e.Pointer.PointerId;
        dragCursorOrigin = cursor;
        dragWindowOrigin = AppWindow.Position;
        isDragging = true;
        e.Handled = true;
    }

    private void OnHeaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragging || e.Pointer.PointerId != dragPointerId)
        {
            return;
        }

        if (NativeWindow.TryGetCursorPosition() is not { } cursor)
        {
            return;
        }

        // Move, never MoveAndResize: the size is the content's business, and re-asserting it here
        // would fight a refresh that lands mid-drag.
        AppWindow.Move(new PointInt32(
            dragWindowOrigin.X + (cursor.X - dragCursorOrigin.X),
            dragWindowOrigin.Y + (cursor.Y - dragCursorOrigin.Y)));

        e.Handled = true;
    }

    private void OnHeaderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragging || e.Pointer.PointerId != dragPointerId)
        {
            return;
        }

        ContentRoot.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// The single place a drag ends. Capture-lost fires for a normal release as well as for a
    /// capture stolen by the system, so ending here cannot leave the window stuck to the cursor.
    /// </summary>
    private void OnHeaderPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragging || e.Pointer.PointerId != dragPointerId)
        {
            return;
        }

        isDragging = false;
        dragPointerId = null;
        AdoptDraggedPosition();
    }

    private uint? dragPointerId;
    private PointInt32 dragCursorOrigin;
    private PointInt32 dragWindowOrigin;

    /// <summary>
    /// Whether a press at this element must NOT start a window drag.
    /// </summary>
    /// <remarks>
    /// Buttons always opt out, so a press on refresh, close or "Use reset" is a click. The list
    /// opts out only while it can ACTUALLY scroll: dragging the window and scrolling the list are
    /// the same gesture, so the list has to win when there is something to scroll to - and when
    /// there is not (the usual case, since the window sizes to its content) the whole surface
    /// stays draggable.
    /// </remarks>
    private bool IsDragExempt(DependencyObject? source)
    {
        while (source is not null && source != ContentRoot)
        {
            if (source is ButtonBase)
            {
                return true;
            }

            if (ReferenceEquals(source, ScrollHost) && ScrollHost.ScrollableHeight > 0)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>
    /// Re-derives the growth anchor after a drop. The window may now be on another display and
    /// against the opposite edge, so both the display used for measuring and the edge that stays
    /// pinned while the content grows have to follow it. The position itself is deliberately NOT
    /// persisted: the next <see cref="ShowFlyout"/> re-anchors to the tray corner, because a
    /// remembered point is wrong the moment the monitor layout or taskbar edge changes.
    /// </summary>
    private void AdoptDraggedPosition()
    {
        var position = AppWindow.Position;
        anchorPoint = new PointInt32(position.X, position.Y);

        var work = WorkArea(out _);
        var size = AppWindow.Size;
        var toTop = position.Y - work.Y;
        var toBottom = work.Y + work.Height - (position.Y + size.Height);
        anchorToBottom = toBottom <= toTop;

        ResizeToContent();
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
        LogScrollState("open");
    }

    /// <summary>
    /// Grows and shrinks the window with its content while keeping the edge nearest the tray -
    /// or, after a drag, the edge the user parked it against - pinned. Re-deriving the position
    /// from the work area instead would teleport a flyout the user had moved.
    /// </summary>
    private void ResizeToContent()
    {
        if (!isOpen || isDragging)
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
        // Growing must not push the window off the display it was dragged to; this only bites
        // when the new height would not fit where it currently sits.
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        AppWindow.MoveAndResize(new RectInt32(position.X, y, size.Width, height));
        LogScrollState("resize");
    }

    /// <summary>
    /// Cursor position captured when the tray was clicked, used to pick the display.
    /// </summary>
    /// <remarks>
    /// Resolving the display from the flyout's OWN window handle picks whichever monitor the
    /// hidden window happens to sit on — on first show that is WinUI's default placement, so a
    /// tray click on a secondary monitor opened the flyout on the primary one. The tray icon
    /// that was clicked is next to the cursor, so the cursor is the correct anchor. The WinForms
    /// original used Cursor.Position for exactly this reason. A drag replaces it with the
    /// window's own corner, which is then the truthful anchor until the next open.
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
    /// Physical pixels the non-client frame eats out of the window's HEIGHT: the difference
    /// between the window rect (what <see cref="AppWindow.MoveAndResize"/> sets) and the client
    /// rect (what XAML actually gets to lay out in).
    /// </summary>
    /// <remarks>
    /// THIS is what made the flyout scroll by a few pixels no matter how carefully the content
    /// was measured. <c>MoveAndResize</c> takes an OUTER rect, and this window is not frameless:
    /// <c>SetBorderAndTitleBar(hasBorder: true, ...)</c> keeps WS_CAPTION|WS_SYSMENU (measured
    /// style 0x04C80000) so that DWM still rounds and shadows it, and WinUI only suppresses the
    /// caption BAR, not the frame. Measured on the live window at 96 dpi: window rect 400x280,
    /// client rect 384x272 - 8px left, 8px right, 8px bottom, 0 top (DWM extended frame bounds
    /// 386x273, i.e. the client area IS the visible window; the border is invisible).
    /// So passing the content height straight to MoveAndResize handed the content a client area
    /// 8px SHORTER than it asked for, and the ScrollViewer dutifully reported ScrollableHeight=8:
    /// a permanent scrollbar with a tiny scroll, on content that "fits". The delta is read from
    /// the window itself rather than hard-coded because it scales with DPI (~10px at 150%).
    /// <para>
    /// The WIDTH is deliberately left as the outer rect. The same 16px is missing horizontally,
    /// but nothing depends on it (the row grid is fixed-column plus a star) and widening the
    /// visible flyout is a change nobody asked for; only the height causes the scrollbar.
    /// </para>
    /// </remarks>
    private int VerticalFramePixels()
    {
        var frame = AppWindow.Size.Height - AppWindow.ClientSize.Height;

        // Sanity bounds: a negative or absurd delta means the window is in some state where the
        // client rect is not meaningful (never observed, but a bad value here would size the
        // window wrongly forever). Zero is the safe answer - it is the old behaviour.
        return frame is > 0 and < 200 ? frame : 0;
    }

    /// <summary>
    /// The OUTER window height, in physical pixels, whose CLIENT area is exactly as tall as the
    /// content wants - clamped so the window still fits the work area.
    /// </summary>
    /// <remarks>
    /// The provider list lives in a ScrollViewer, and a ScrollViewer's own DesiredSize is a
    /// scroll VIEWPORT, not its content - measuring RootGrid alone would size the window to its
    /// chrome and scroll everything else out of sight. So the inner panel is measured first and
    /// the viewer is pinned to that height only for the duration of this pass; the star-sized
    /// row then hands the viewer exactly the height the window ended up with, which is what
    /// makes the content SCROLL rather than clip once the work-area clamp bites.
    /// The clamp is applied to the OUTER height (content + frame), because it is the outer rect
    /// that has to fit between the work-area margins.
    /// </remarks>
    private int MeasuredHeightPixels(RectInt32 work, double scale)
    {
        // A real layout pass, not hand-rolled Measure calls. Measure() on an element that is
        // already in the tree is a no-op unless that exact element was invalidated, so measuring
        // ProviderGroups directly returned a size that predated the rows just synced into it -
        // the window came out a row or two short and the list scrolled to make up the difference.
        // UpdateLayout settles the whole subtree first; after it, DesiredSize is trustworthy.
        ContentRoot.UpdateLayout();

        // The THREE PARTS are summed rather than reading ContentRoot's own DesiredSize. Its middle
        // row is star-sized, so the grid's height is whatever the window already had - the
        // measurement that DECIDES the window height would be reading back its own previous
        // answer, and any error in it would be permanent. A header, a stack of cards and a status
        // line each know their own height, and the ScrollViewer measures its content unbounded, so
        // ProviderGroups.DesiredSize is the true content height even while the viewer is clipped.
        var spacing = 2 * ContentRoot.RowSpacing;
        var chrome = ContentRoot.Padding.Top + ContentRoot.Padding.Bottom + spacing
            + HeaderBar.DesiredSize.Height
            + StatusBar.DesiredSize.Height;
        var desired = chrome + ProviderGroups.DesiredSize.Height;

        if (double.IsNaN(desired) || desired < 1)
        {
            desired = FallbackHeightDip;
        }

        // Everything above is in DIPs and describes the CLIENT area; everything below is physical
        // pixels and describes the WINDOW rect, because that is what AppWindow speaks.
        var frame = VerticalFramePixels();
        var marginPx = (int)Math.Round(MarginDip * scale);
        var minPx = (int)Math.Round(MinHeightDip * scale) + frame;
        var maxPx = Math.Max(minPx, work.Height - (2 * marginPx));

        // The window is sized to the content whenever the work area allows it, so the viewer only
        // ever scrolls when there are genuinely more providers than fit on screen - a scrollbar on
        // content that fits was the complaint that started this.
        var desiredPx = (int)Math.Round(Math.Ceiling(desired) * scale) + frame;
        var clampedPx = Math.Clamp(desiredPx, minPx, maxPx);

        // The one line that says whether the window is the right height: if clamped < desired the
        // work area genuinely could not fit the content and the list SHOULD scroll; if they match
        // and it still scrolls, the measurement is wrong. `frame` is logged next to them because it
        // is the term three previous attempts were missing - all of them reasoned in DIPs about the
        // client area and then handed the number to an API that sets the OUTER rect.
        DiagnosticLog.Write(
            "flyout measure header={0:0} groups={1:0} status={2:0} desiredDip={3:0} frame={4} desiredPx={5} clampedPx={6} maxPx={7}",
            HeaderBar.DesiredSize.Height,
            ProviderGroups.DesiredSize.Height,
            StatusBar.DesiredSize.Height,
            desired,
            frame,
            desiredPx,
            clampedPx,
            maxPx);

        return clampedPx;
    }

    /// <summary>
    /// Logs what the ScrollViewer ended up with once layout has settled after a resize. Kept
    /// permanently (behind CODEXBAR_WINUI_DIAG) because a stray scrollbar is the one flyout defect
    /// that cannot be diagnosed from the measurement alone: it is the gap between the height the
    /// window was GIVEN and the height the viewport actually GOT. Healthy reading is
    /// scrollable=0 with viewport == extent; anything else names its own cause - a non-zero
    /// scrollable with window-client == frame means the frame delta is wrong again.
    /// </summary>
    private void LogScrollState(string phase)
    {
        if (!DiagnosticLog.IsEnabled)
        {
            return;
        }

        // Low priority: MoveAndResize has to reach the XAML island and a layout pass has to run
        // before ScrollHost knows its new viewport. Reading it inline reports the PREVIOUS size.
        _ = queue.TryEnqueue(DispatcherQueuePriority.Low, () => DiagnosticLog.Write(
            "flyout scroll {0} window={1} client={2} viewport={3:0.##} extent={4:0.##} scrollable={5:0.##}",
            phase,
            AppWindow.Size.Height,
            AppWindow.ClientSize.Height,
            ScrollHost.ViewportHeight,
            ScrollHost.ExtentHeight,
            ScrollHost.ScrollableHeight));
    }
}
