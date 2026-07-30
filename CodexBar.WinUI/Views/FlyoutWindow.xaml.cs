using System;
using CodexBarWindows;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBar.WinUI;

/// <summary>One row in the flyout's provider list. Public because x:Bind binds to it.</summary>
public sealed class ProviderRow(string name, string description, string status)
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public string Status { get; } = status;
}

/// <summary>
/// The tray flyout: chrome-less, always on top, hidden from the taskbar and Alt-Tab, rounded,
/// backed by the configured system material, anchored next to the notification area and
/// dismissed when focus leaves the app. It is HIDDEN, never closed, so it keeps its state and
/// its (expensive) XAML tree between shows.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    // Logical (DIP) design size; converted to physical pixels before positioning.
    private const int WidthDip = 380;
    private const int FallbackHeightDip = 320;
    private const int MinHeightDip = 200;
    private const int MarginDip = 12;

    /// <summary>Re-show debounce: a tray click deactivates the flyout before we see the click.</summary>
    private static readonly TimeSpan ReopenDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IntPtr hwnd;
    private readonly DispatcherQueue queue;
    private readonly DispatcherQueueTimer foregroundWatch;

    private bool isOpen;
    private bool hasBeenForeground;
    private DateTime lastHiddenUtc = DateTime.MinValue;

    public event EventHandler? SettingsRequested;
    public event EventHandler? GraphsRequested;

    public FlyoutWindow()
    {
        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        queue = DispatcherQueue.GetForCurrentThread();

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

        SubtitleText.Text = $"Version {AppInfo.VersionText}";
        StatusText.Text = "Shell only - usage data lands in the next phase";

        RefreshButton.Click += (_, _) => SetStatus($"Refreshed {DateTime.Now:HH:mm:ss} - no providers wired up yet");
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        GraphsButton.Click += (_, _) => GraphsRequested?.Invoke(this, EventArgs.Empty);

        RootGrid.KeyboardAccelerators.Add(CreateEscapeAccelerator());
        RootGrid.ActualThemeChanged += (_, _) => AppTheme.ApplyTint(RootGrid, TintLayer);

        AppTheme.Changed += OnThemeChanged;
        ApplyTheme();
        BuildProviderRows();

        Activated += OnActivated;

        foregroundWatch = queue.CreateTimer();
        foregroundWatch.Interval = TimeSpan.FromMilliseconds(250);
        foregroundWatch.Tick += (_, _) => CheckForegroundOwnership();
    }

    public bool IsOpen => isOpen;

    /// <summary>Replaces the footer status line (used for update-check results).</summary>
    public void SetStatus(string text) => StatusText.Text = text;

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
        PositionNearTray();
        AppWindow.Show(activateWindow: true);

        hasBeenForeground = false;
        isOpen = true;

        // A tray click leaves the shell in the foreground, so a plain SetForegroundWindow is
        // refused; without foreground the window can never observe LOSING it either.
        NativeWindow.ForceForeground(hwnd);
        RefreshButton.Focus(FocusState.Programmatic);

        foregroundWatch.Start();
        DiagnosticLog.Write("flyout shown foregroundIsOurs={0}", NativeWindow.ForegroundBelongsToThisProcess());
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
        DiagnosticLog.Write("flyout hidden");
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

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            DiagnosticLog.Write("flyout deactivated open={0} sawForeground={1}", isOpen, hasBeenForeground);
            ScheduleDismissCheck();
            return;
        }

        hasBeenForeground = true;
        DiagnosticLog.Write("flyout activated state={0}", e.WindowActivationState);
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

    private KeyboardAccelerator CreateEscapeAccelerator()
    {
        var accelerator = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            HideFlyout();
        };

        return accelerator;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        BuildProviderRows();
    }

    private void ApplyTheme() => AppTheme.Apply(this, RootGrid, TintLayer);

    private void BuildProviderRows()
    {
        var settings = AppTheme.Settings;
        ProviderList.Items.Clear();

        // Only the tools the user actually enabled: a disabled tool has no tab and is never
        // polled, so it has nothing to say here either.
        if (settings.CodexEnabled)
        {
            ProviderList.Items.Add(new ProviderRow("Codex", "OpenAI Codex CLI", "Pending"));
        }

        if (settings.ClaudeEnabled)
        {
            ProviderList.Items.Add(new ProviderRow("Claude", "Claude Code", "Pending"));
        }

        if (settings.CursorEnabled)
        {
            ProviderList.Items.Add(new ProviderRow("Cursor", "Cursor editor", "Pending"));
        }

        if (ProviderList.Items.Count == 0)
        {
            ProviderList.Items.Add(new ProviderRow("No tools enabled", "Turn one back on in Settings", string.Empty));
        }
    }

    /// <summary>
    /// Anchors the flyout to the work-area corner next to the notification area. WorkArea
    /// excludes the taskbar, so comparing it with OuterBounds says which edge the taskbar is on.
    /// All AppWindow geometry is in PHYSICAL pixels, hence the DPI scaling.
    /// </summary>
    private void PositionNearTray()
    {
        var displayArea = DisplayArea.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(hwnd), DisplayAreaFallback.Nearest);
        var work = displayArea.WorkArea;
        var outer = displayArea.OuterBounds;

        var scale = NativeWindow.ScaleFor(hwnd);
        var margin = (int)Math.Round(MarginDip * scale);
        var width = (int)Math.Round(WidthDip * scale);
        var maxHeightDip = (work.Height / scale) - (2 * MarginDip);
        var height = (int)Math.Round(ContentHeightDip(maxHeightDip) * scale);

        // Default: bottom-right, i.e. a bottom or right taskbar.
        var x = work.X + work.Width - width - margin;
        var y = work.Y + work.Height - height - margin;

        if (work.Y > outer.Y)
        {
            y = work.Y + margin;            // taskbar on top
        }
        else if (work.X > outer.X)
        {
            x = work.X + margin;            // taskbar on the left
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>
    /// The height the content actually wants, so the flyout hugs it: the number of provider
    /// rows depends on which tools the user enabled, and a fixed height would leave dead space
    /// (or clip) depending on that choice.
    /// </summary>
    private double ContentHeightDip(double maxHeightDip)
    {
        RootGrid.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        var desired = RootGrid.DesiredSize.Height;
        if (double.IsNaN(desired) || desired < 1)
        {
            desired = FallbackHeightDip;
        }

        return Math.Clamp(Math.Ceiling(desired), MinHeightDip, Math.Max(MinHeightDip, maxHeightDip));
    }
}
