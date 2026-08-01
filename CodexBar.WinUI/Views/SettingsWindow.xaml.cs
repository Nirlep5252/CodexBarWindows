using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CodexBarWindows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBar.WinUI;

/// <summary>
/// The settings surface, ported from the WinForms <c>SettingsForm</c>.
/// <para>
/// The WinForms version needed ~2000 lines of hand-rolled Fluent controls (cards, expanders,
/// toggles, sliders, combo boxes, an owner-drawn list and a navigation rail) because WinForms
/// ships none of them. Every one of those is a stock control here, so this file only holds
/// state plumbing: read the persisted settings, write them back, and tell the refresh service
/// when something it depends on moved.
/// </para>
/// <para>
/// The chrome deliberately mirrors <see cref="FlyoutWindow"/> and <see cref="GraphsWindow"/> so
/// the three surfaces read as one app: the same RootGrid/TintLayer pair, the same 16,12,16,12
/// padding with a single RowSpacing, the same CornerRadius=8 cards with 1px-separated rows
/// inside them, the same caption-scale muted vocabulary, and ONE status line at the bottom -
/// which is what replaced the four InfoBars.
/// </para>
/// <para>
/// Every change applies immediately - there is no OK/Cancel. Persisting through
/// <see cref="UiSettings.Save"/> raises <see cref="UiSettings.Changed"/>, which is what makes
/// the open flyout re-theme and rebuild its cards live.
/// </para>
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>
    /// Coalescing window for the tint slider. Dragging raises ValueChanged on every tick;
    /// saving each tick wrote the registry and re-themed every open window dozens of times per
    /// drag, which made the drag feel like it was not applying at all (see the WinForms
    /// <c>SettingsForm.OnOpacityChanged</c> for the original bug).
    /// </summary>
    private static readonly TimeSpan TintCommitDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The floor the layout is designed down to: below this the two-column rows (label plus a
    /// 160-wide combo or a 180-wide slider) start crushing their labels. Enforced rather than
    /// documented - see the presenter minimums in the constructor.
    /// </summary>
    private const int MinimumWidthDips = 520;
    private const int MinimumHeightDips = 420;

    /// <summary>What the status line says when it has nothing to report. Matches the XAML.</summary>
    private const string DefaultStatus = "Every change applies immediately.";

    private const string ToolsFloorMessage = "At least one tool has to stay on, so Codex was turned back on.";

    /// <summary>What the import row says when it is not running. Matches the XAML.</summary>
    private const string ImportIdleCaption = "Reads every session log once. Expect a few minutes.";

    private readonly UsageRefreshService service;
    private readonly IntPtr hwnd;
    private readonly DispatcherQueue queue;
    private readonly DispatcherQueueTimer tintCommitTimer;
    /// <summary>
    /// Same coalescing as the tint slider, for the same reason: a ColorPicker drag raises
    /// ColorChanged as fast as a slider, and each save writes the registry and re-themes every
    /// open window.
    /// </summary>
    private readonly DispatcherQueueTimer chartColorCommitTimer;
    private readonly List<CodexCliEntry> codexEntries;
    private readonly List<ChartColorRow> chartColorRows = [];

    /// <summary>The hex each category was last DRAWN in, as recorded by the graphs window.</summary>
    private Dictionary<string, string> drawnColors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The handful of colours that cannot be a XAML <c>{ThemeResource}</c>: the provider mark
    /// tints and the status line's warning/danger. Rebuilt on ActualThemeChanged, because a
    /// brush is captured once and would otherwise stay frozen to the theme it was born in.
    /// </summary>
    private FlyoutPalette palette;

    private UiSettings settings;
    private int? pendingTintPercent;
    private (string Key, string Hex)? pendingChartColor;
    /// <summary>Seeded with the XAML's text so a right-click Copy works before anything happens.</summary>
    private string statusFullText = DefaultStatus;
    private StatusLevel statusLevel = StatusLevel.Info;
    /// <summary>
    /// Set in <see cref="OnClosed"/>. An update check outcome can arrive after the window is
    /// gone, and touching a closed window's controls throws.
    /// </summary>
    private bool isClosed;
    /// <summary>
    /// Non-null exactly while a history import is running; also the flag the button reads to decide
    /// whether a click starts one or cancels the one in flight.
    /// </summary>
    private CancellationTokenSource? importCancellation;
    /// <summary>
    /// Suppresses the write-back handlers. Set while the controls are being populated from the
    /// persisted settings, and again around the one place this window drives a control itself
    /// (snapping the last tool back on), so neither can be mistaken for a user edit.
    /// </summary>
    private bool suppressWrites = true;

    public SettingsWindow(UsageRefreshService service)
    {
        this.service = service;

        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        queue = DispatcherQueue.GetForCurrentThread();
        // Vibes-neutralised (see AppTheme.Settings), which this window is also the one place that
        // WRITES BACK: every Save here carries VibesEnabled=false, so the first appearance change
        // a stranded UiVibes=1 user makes also clears the stale flag out of the shared registry
        // key for good. That migration is a side effect, not the fix - the fix is that nothing in
        // this shell reads the flag any more - and it is idempotent by construction.
        settings = AppTheme.Settings;
        palette = FlyoutPalette.For(RootGrid);
        codexEntries = CodexCliSettings.Load().ToList();

        Title = $"Settings - {AppInfo.AppName}";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "CodexBarWindows.ico"));

        // Smaller than the 920x660 the rail-and-gutters layout needed: the rail's 196 DIP and the
        // 28 DIP page padding either side of a 720-wide column are both gone.
        var scale = NativeWindow.ScaleFor(hwnd);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(760 * scale),
            (int)Math.Round(640 * scale)));

        // The window is resizable, so the floor is enforced rather than documented:
        // OverlappedPresenter feeds these straight into WM_GETMINMAXINFO's ptMinTrackSize, which -
        // like every other size on AppWindow - is in physical pixels, hence the same DPI scale as
        // the Resize above. Maximising is allowed now that the page column is width-capped rather
        // than stretched.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(MinimumWidthDips * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinimumHeightDips * scale);
        }

        tintCommitTimer = queue.CreateTimer();
        tintCommitTimer.Interval = TintCommitDelay;
        tintCommitTimer.IsRepeating = false;
        tintCommitTimer.Tick += (_, _) => CommitPendingTint();

        chartColorCommitTimer = queue.CreateTimer();
        chartColorCommitTimer.Interval = TintCommitDelay;
        chartColorCommitTimer.IsRepeating = false;
        chartColorCommitTimer.Tick += (_, _) => CommitPendingChartColor();

        LoadValues();
        WireEvents();

        RootGrid.ActualThemeChanged += (_, _) =>
        {
            AppTheme.ApplyTint(RootGrid, TintLayer);
            // Everything below is a colour this window assigned in CODE, and none of those
            // re-resolve for free: the palette follows the element's ActualTheme, so it and every
            // brush taken from it have to be rebuilt on a flip.
            palette = FlyoutPalette.For(RootGrid);
            RenderProviderMarks();
            ApplyStatusColour();
            // The swatches show the colour the chart will actually draw, and the automatic ones
            // are theme-derived, so they have to be re-resolved rather than left frozen.
            RefreshChartColorSwatches();
        };
        AppTheme.Changed += OnThemeChanged;
        if (App.Shell is { } shell)
        {
            // A check can be started from the tray menu or by the six-hourly timer, so the
            // button follows the App's state rather than only its own clicks.
            shell.UpdateCheckStateChanged += OnUpdateCheckStateChanged;
            CheckUpdatesButton.IsEnabled = !shell.IsCheckingForUpdates;
        }

        Activated += (_, _) => ActivationChanged?.Invoke(this, EventArgs.Empty);
        Closed += (_, _) => OnClosed();

        AppTheme.Apply(this, RootGrid, TintLayer);

        SectionBar.SelectedItem = SectionBar.Items[0];
        suppressWrites = false;
    }

    /// <summary>
    /// Raised whenever this window's activation changes, so the flyout can re-test whether the
    /// foreground is still inside this process. See <see cref="FlyoutWindow.ReArmDismissCheck"/>.
    /// </summary>
    public event EventHandler? ActivationChanged;

    public void ShowAndFocus()
    {
        // Re-read the colour catalog on every show. This window is CACHED for the life of the
        // process, so a list built once in the constructor is the list as it was the first time
        // settings was ever opened - which makes the empty state's own instruction ("open the
        // usage graphs once to populate this list") impossible to follow: the user does exactly
        // that, comes back, and sees the same empty box until they restart the app.
        RenderChartColors();

        AppWindow.Show(activateWindow: true);
        NativeWindow.ForceForeground(hwnd);
    }

    /// <summary>
    /// Shows the window with the Graphs section selected - where the history import lives.
    /// </summary>
    /// <remarks>
    /// The graphs window's "Import history" link lands here. It does NOT start the import: that
    /// reads every session log on disk, and the one rule this feature ships under is that nothing
    /// runs unless the user clicks the button themselves.
    /// <para/>
    /// No scroll-into-view. The import card is the FIRST thing on the Graphs page, and a Collapsed
    /// page has never been measured, so bringing a child into view would have to be deferred a
    /// layout pass to do nothing visible.
    /// </remarks>
    public void ShowHistoryImport()
    {
        foreach (var item in SectionBar.Items)
        {
            if (item is SelectorBarItem { Tag: "graphs" } graphs)
            {
                // Assigning SelectedItem raises SelectionChanged, so the page flip is the same code
                // path a click takes - the selector never disagrees with what is on screen.
                SectionBar.SelectedItem = graphs;
                break;
            }
        }

        ShowAndFocus();
    }

    // ------------------------------------------------------------------ lifetime

    private void OnClosed()
    {
        isClosed = true;

        // Flush before the window goes: a tint drag that ended inside the debounce window
        // would otherwise be silently dropped.
        CommitPendingTint();
        tintCommitTimer.Stop();
        CommitPendingChartColor();
        chartColorCommitTimer.Stop();
        AppTheme.Changed -= OnThemeChanged;

        // CLOSING THE WINDOW CANCELS THE IMPORT. The alternative - letting it run on - would leave
        // minutes of full-rate disk and CPU work with no progress, no result and no way to stop it,
        // which is the opposite of what makes this feature acceptable at all. Cancelling is free of
        // consequences by construction: the ledger is written once, after a corpus has been read in
        // full, so a cancelled run leaves history exactly as it was.
        importCancellation?.Cancel();

        if (App.Shell is { } shell)
        {
            shell.UpdateCheckStateChanged -= OnUpdateCheckStateChanged;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // Track the persisted record (this window is the only editor, so the controls already
        // agree with it) and re-apply theme/backdrop/tint to this window as well.
        settings = AppTheme.Settings;
        AppTheme.Apply(this, RootGrid, TintLayer);
    }

    // ------------------------------------------------------------------ wiring

    private void LoadValues()
    {
        VersionText.Text = AppInfo.VersionText;

        // ShellIdentity, not StartupSettings.IsEnabled: the WinForms app owns the unsuffixed
        // Run value, and this shell must not read or overwrite it while both are installed.
        StartupToggle.IsOn = StartupSettings.IsEnabledFor(ShellIdentity.StartupRegistryValueName);
        CodexEnabledToggle.IsOn = settings.CodexEnabled;
        ClaudeEnabledToggle.IsOn = settings.ClaudeEnabled;
        CursorEnabledToggle.IsOn = settings.CursorEnabled;
        OpenCodeGoEnabledToggle.IsOn = settings.OpenCodeGoEnabled;

        ThemeCombo.SelectedIndex = (int)settings.Theme;
        MaterialCombo.SelectedIndex = (int)settings.Material;
        VibesToggle.IsOn = settings.VibesEnabled;
        OpacitySlider.Value = settings.TintOpacityPercent;
        OpacityValueText.Text = settings.TintOpacityPercent.ToString();
        ApplyMaterialEnablement();

        CursorCookieBox.Password = CursorSettings.LoadCookieHeader();
        RenderCursorSavedState();
        OpenCodeGoCookieBox.Password = OpenCodeGoUsageReader.SessionValue(OpenCodeGoSettings.LoadCookieHeader());
        OpenCodeGoWorkspaceBox.Text = OpenCodeGoSettings.LoadWorkspaceId();
        RenderOpenCodeGoSavedState();
        RenderProviderMarks();
        RenderAccounts();
        RenderChartColors();
    }

    private void WireEvents()
    {
        StartupToggle.Toggled += (_, _) =>
        {
            if (!suppressWrites)
            {
                StartupSettings.SetEnabledFor(ShellIdentity.StartupRegistryValueName, StartupToggle.IsOn);
                DiagnosticLog.Write(
                    "start with windows {0} value={1}",
                    StartupToggle.IsOn ? "on" : "off",
                    ShellIdentity.StartupRegistryValueName);
            }
        };

        CheckUpdatesButton.Click += (_, _) => CheckForUpdates();

        CodexEnabledToggle.Toggled += (_, _) => OnProviderEnabledChanged();
        ClaudeEnabledToggle.Toggled += (_, _) => OnProviderEnabledChanged();
        CursorEnabledToggle.Toggled += (_, _) => OnProviderEnabledChanged();
        OpenCodeGoEnabledToggle.Toggled += (_, _) => OnProviderEnabledChanged();

        ThemeCombo.SelectionChanged += (_, _) => OnThemeSelectionChanged();
        MaterialCombo.SelectionChanged += (_, _) => OnMaterialSelectionChanged();
        VibesToggle.Toggled += (_, _) => OnVibesToggled();
        OpacitySlider.ValueChanged += (_, _) => OnOpacityChanged();

        AddAccountToggle.Click += (_, _) => ShowAddAccountPanel(AddAccountPanel.Visibility != Visibility.Visible);
        AddCancelButton.Click += (_, _) => ShowAddAccountPanel(false);
        AddBrowseButton.Click += async (_, _) =>
        {
            var picked = await BrowseForCodexCliAsync();
            if (picked is not null)
            {
                AddPathBox.Text = picked;
            }
        };
        AddAccountButton.Click += (_, _) => AddCodexCli();

        ResetAllChartColorsButton.Click += (_, _) => ResetAllChartColors();
        ImportHistoryButton.Click += (_, _) => ToggleHistoryImport();

        SaveCursorButton.Click += (_, _) => SaveCursorCookieHeader();
        ClearCursorButton.Click += (_, _) => ClearCursorCookieHeader();
        SaveOpenCodeGoButton.Click += (_, _) => SaveOpenCodeGoSettings();
        ClearOpenCodeGoButton.Click += (_, _) => ClearOpenCodeGoSession();
    }

    /// <summary>
    /// Shows the page the selector points at.
    /// </summary>
    /// <remarks>
    /// Still a Visibility flip across sibling ScrollViewers rather than a Frame: a Collapsed
    /// element is neither measured nor arranged, so the only cost is one retained tree per page -
    /// and in exchange every page keeps its scroll offset and its half-typed text boxes when you
    /// come back to it, which a navigated Frame throws away on every switch.
    /// </remarks>
    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        GeneralPage.Visibility = Shown(tag == "general");
        AppearancePage.Visibility = Shown(tag == "appearance");
        GraphsPage.Visibility = Shown(tag == "graphs");
        AccountsPage.Visibility = Shown(tag == "accounts");
    }

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------- status

    /// <summary>How loudly the status line says something. Errors are red, warnings amber.</summary>
    private enum StatusLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// The one status line, and the four InfoBars' replacement. Each of those was a banner that
    /// reflowed the page under the pointer as it opened, and the update-check result in
    /// particular was a banner here but a status sentence in the flyout for the same message.
    /// Text is wrapped rather than trimmed, repeated on the tooltip, selectable and copyable.
    /// </summary>
    private void SetStatus(string text, StatusLevel level = StatusLevel.Info)
    {
        statusFullText = text;
        statusLevel = level;
        StatusText.Text = text;
        ToolTipService.SetToolTip(StatusText, string.IsNullOrEmpty(text) ? null : text);
        ApplyStatusColour();
    }

    private void ApplyStatusColour()
    {
        StatusIcon.Visibility = Shown(statusLevel != StatusLevel.Info);

        if (statusLevel == StatusLevel.Info)
        {
            // Cleared so TertiaryCaptionStyle's {ThemeResource} setter takes over again - which is
            // exactly why the muted colour lives on the style and not inline.
            StatusText.ClearValue(TextBlock.ForegroundProperty);
            return;
        }

        // From the palette, NOT from Application.Current.Resources: a brush read out of the app
        // resources resolves against the APP theme and renders wrong under a forced theme.
        var brush = statusLevel == StatusLevel.Error ? palette.Danger : palette.Warning;
        StatusText.Foreground = brush;
        StatusIcon.Foreground = brush;
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

    // ------------------------------------------------------------------ general

    /// <summary>
    /// Paints the three provider marks. These are the real per-provider vectors the flyout draws,
    /// not three copies of one generic glyph: this is the page where provider identity is
    /// configured. Claude keeps Anthropic's own colour in both themes because it identifies the
    /// provider; the other two are the theme's monochrome glyph tint.
    /// </summary>
    private void RenderProviderMarks()
    {
        CodexMark.Child = ProviderGeometry.CreateIcon(UsageProvider.Codex, palette.Glyph);
        ClaudeMark.Child = ProviderGeometry.CreateIcon(UsageProvider.Claude, palette.ClaudeGlyph);
        CursorMark.Child = ProviderGeometry.CreateIcon(UsageProvider.Cursor, palette.Glyph);
        OpenCodeGoMark.Child = ProviderGeometry.CreateIcon(UsageProvider.OpenCodeGo, palette.Glyph);
        BuiltInAccountMark.Child = ProviderGeometry.CreateIcon(UsageProvider.Codex, palette.Glyph);
        CursorAccountMark.Child = ProviderGeometry.CreateIcon(UsageProvider.Cursor, palette.Glyph);
        OpenCodeGoAccountMark.Child = ProviderGeometry.CreateIcon(UsageProvider.OpenCodeGo, palette.Glyph);

        // The generated account rows hold marks too, and a Path cannot be shared between two
        // parents here (it takes the process down with 0xc000027b), so each row gets its own.
        foreach (var host in accountMarks)
        {
            host.Child = ProviderGeometry.CreateIcon(UsageProvider.Codex, palette.Glyph);
        }
    }

    /// <summary>
    /// Persists the per-tool toggles. Turning every tool off would leave the flyout with
    /// nothing to show and no way back, so the last one is held on and its toggle snapped back.
    /// </summary>
    private void OnProviderEnabledChanged()
    {
        if (suppressWrites)
        {
            return;
        }

        if (!CodexEnabledToggle.IsOn &&
            !ClaudeEnabledToggle.IsOn &&
            !CursorEnabledToggle.IsOn &&
            !OpenCodeGoEnabledToggle.IsOn)
        {
            suppressWrites = true;
            CodexEnabledToggle.IsOn = true;
            suppressWrites = false;
            SetStatus(ToolsFloorMessage, StatusLevel.Warning);
        }
        else if (statusFullText == ToolsFloorMessage)
        {
            // The InfoBar this replaced closed itself once the floor was no longer being hit, so
            // the sentence does not outlive the state it describes. Only that message is cleared -
            // an unrelated warning on the line is somebody else's and stays.
            SetStatus(DefaultStatus);
        }

        settings = settings with
        {
            CodexEnabled = CodexEnabledToggle.IsOn,
            ClaudeEnabled = ClaudeEnabledToggle.IsOn,
            CursorEnabled = CursorEnabledToggle.IsOn,
            OpenCodeGoEnabled = OpenCodeGoEnabledToggle.IsOn
        };
        settings.Save();
    }

    /// <summary>
    /// Runs an update check and reports the outcome HERE rather than in the flyout. The tray
    /// menu's copy of this command has no window of its own to answer in, so it borrows the
    /// flyout; this one is already looking at the version it is asking about.
    /// </summary>
    private void CheckForUpdates()
    {
        SetStatus("Checking for updates...");

        App.Shell?.CheckForUpdates(result =>
        {
            // The window can be closed while the HTTP call is in flight; the callback still runs
            // because the App owns the check, so the controls have to be treated as gone.
            if (isClosed)
            {
                return;
            }

            // The same severity split the InfoBar carried: a check that resolved is quiet, and
            // anything else is a warning.
            var level = result.Status switch
            {
                UpdateCheckStatus.UpToDate => StatusLevel.Info,
                UpdateCheckStatus.Installing => StatusLevel.Info,
                _ => StatusLevel.Warning
            };
            SetStatus(result.Message, level);
        });
    }

    private void OnUpdateCheckStateChanged(bool isChecking) => CheckUpdatesButton.IsEnabled = !isChecking;

    // --------------------------------------------------------------- appearance

    private void OnThemeSelectionChanged()
    {
        if (suppressWrites)
        {
            return;
        }

        var theme = (AppThemeMode)Math.Clamp(ThemeCombo.SelectedIndex, 0, 2);
        if (theme == settings.Theme)
        {
            return;
        }

        settings = settings with { Theme = theme };
        settings.Save();
    }

    private void OnMaterialSelectionChanged()
    {
        if (suppressWrites)
        {
            return;
        }

        var material = (BackdropMaterial)Math.Clamp(MaterialCombo.SelectedIndex, 0, 3);
        if (material == settings.Material)
        {
            return;
        }

        settings = settings with { Material = material };
        settings.Save();
        ApplyMaterialEnablement();
    }

    /// <summary>
    /// Unreachable today: the row is Collapsed and <see cref="AppTheme.Settings"/> neutralises
    /// the flag, so a saved <c>true</c> would come straight back as <c>false</c>. Kept wired for
    /// the port - un-hiding the row means dropping the WithoutVibes call in the same change.
    /// </summary>
    private void OnVibesToggled()
    {
        if (suppressWrites || VibesToggle.IsOn == settings.VibesEnabled)
        {
            return;
        }

        settings = settings with { VibesEnabled = VibesToggle.IsOn };
        settings.Save();
        ApplyMaterialEnablement();
    }

    /// <summary>
    /// The tint slider is dead on Solid - there is no material to tint - so the whole row is
    /// disabled there. That is the ONLY enablement rule on this page, and it is driven by the
    /// Material combo sitting directly above it.
    /// </summary>
    /// <remarks>
    /// The material combo used to be disabled while vibes were on (vibes always rode the stock
    /// Acrylic backdrop, so the picker was inert). That gate SHIPPED BROKEN once the vibes row
    /// was hidden here: a user arriving with <c>UiVibes=1</c> from the WinForms app - the
    /// registry key survives an in-place upgrade - got a permanently disabled material picker
    /// and no visible control to turn vibes off, a dead end with no escape from inside the app.
    /// A control must never be gated on a setting the user cannot reach. The gate is also
    /// pointless now: <see cref="AppTheme.Settings"/> neutralises vibes for this shell, so
    /// <c>EffectiveMaterial</c> here is always just <c>Material</c>.
    /// </remarks>
    private void ApplyMaterialEnablement()
    {
        OpacityRow.IsEnabled = settings.EffectiveMaterial != BackdropMaterial.Solid;
    }

    private void OnOpacityChanged()
    {
        var percent = (int)Math.Round(OpacitySlider.Value);
        OpacityValueText.Text = percent.ToString();

        if (suppressWrites)
        {
            return;
        }

        if (percent == settings.TintOpacityPercent)
        {
            pendingTintPercent = null;
            tintCommitTimer.Stop();
            return;
        }

        pendingTintPercent = percent;
        tintCommitTimer.Stop();
        tintCommitTimer.Start();
    }

    private void CommitPendingTint()
    {
        tintCommitTimer.Stop();
        if (pendingTintPercent is not { } percent)
        {
            return;
        }

        pendingTintPercent = null;
        if (percent == settings.TintOpacityPercent)
        {
            return;
        }

        settings = settings with { TintOpacityPercent = percent };
        settings.Save();
        DiagnosticLog.Write("tint committed {0}%", percent);
    }

    // ------------------------------------------------------------------- graphs

    /// <summary>One colour row's live controls, so a save can repaint it without a rebuild.</summary>
    private sealed record ChartColorRow(
        string Key,
        Border Swatch,
        TextBlock HexText,
        TextBlock StateText,
        Button ResetButton,
        ColorPicker Picker);

    /// <summary>
    /// Rebuilds the per-model colour list from the labels the graphs window has plotted.
    /// </summary>
    /// <remarks>
    /// Called on load and after a reset, NEVER after an ordinary colour commit: the picker is
    /// hosted in a flyout off the row it belongs to, and rebuilding the row while the user is
    /// still dragging inside it would tear the flyout down mid-gesture.
    /// </remarks>
    private void RenderChartColors()
    {
        ChartColorList.Children.Clear();
        chartColorRows.Clear();

        var catalog = ChartCategoryCatalog.Load();
        drawnColors = catalog
            .Where(entry => entry.DrawnHex is not null)
            .ToDictionary(entry => entry.Label, entry => entry.DrawnHex!, StringComparer.OrdinalIgnoreCase);

        // A model that was colour-picked and has since dropped out of the 30-day window still
        // gets a row, otherwise its override becomes invisible and unremovable.
        var known = catalog.Select(entry => entry.Label)
            .Concat(settings.ChartColorOverrides.Keys)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ChartColorEmptyCard.Visibility = Shown(known.Length == 0);
        ChartColorCard.Visibility = Shown(known.Length > 0);
        ResetAllChartColorsButton.IsEnabled = settings.ChartColorOverrides.Count > 0;

        foreach (var label in known)
        {
            // Rows divide with a hairline instead of floating apart, so the list reads as one
            // card of models rather than a ladder of identical boxes.
            if (chartColorRows.Count > 0)
            {
                ChartColorList.Children.Add(new Border { Style = RowStyle("RowSeparatorStyle") });
            }

            chartColorRows.Add(CreateChartColorRow(label));
        }

        RefreshChartColorSwatches();
    }

    private ChartColorRow CreateChartColorRow(string label)
    {
        // The swatch IS the affordance: it opens the picker, so the row loses the "Pick" button
        // that used to sit between the colour and its hex. Subtle chrome keeps the colour itself
        // the only filled thing in the row.
        var swatch = new Border { Style = RowStyle("ChartSwatchStyle") };

        var picker = new ColorPicker
        {
            // No alpha: a translucent bar would blend with whatever it stacks on and stop being
            // the colour the user picked.
            IsAlphaEnabled = false,
            IsHexInputVisible = true,
            IsColorChannelTextInputVisible = true,
            IsMoreButtonVisible = false
        };
        picker.ColorChanged += (_, args) => OnChartColorPicked(label, args.NewColor);

        var pick = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Content = swatch,
            Flyout = new Flyout { Content = picker },
            VerticalAlignment = VerticalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pick, $"Pick a colour for {label}");
        ToolTipService.SetToolTip(pick, $"Pick a colour for {label}");

        var nameText = new TextBlock
        {
            Text = label,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(nameText, label);

        var hexText = new TextBlock { Style = RowStyle("ChartHexStyle") };
        var stateText = new TextBlock { Style = RowStyle("ChartStateStyle") };

        // Icon-only, like the flyout's header actions: the meaning is on the tooltip and the
        // automation name, and a row full of word-buttons was most of what made this list read
        // like a debug dump.
        var reset = new Button
        {
            Width = 32,
            Height = 30,
            Padding = new Thickness(0),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Content = new FontIcon { FontSize = 13, Glyph = "\uE7A7" },
            VerticalAlignment = VerticalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reset, $"Reset the colour for {label}");
        ToolTipService.SetToolTip(reset, "Reset to the automatic colour");
        reset.Click += (_, _) => ResetChartColor(label);

        var row = new Grid { Style = RowStyle("SettingRowStyle"), ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        AddAt(row, pick, 0);
        AddAt(row, nameText, 1);
        AddAt(row, hexText, 2);
        AddAt(row, stateText, 3);
        AddAt(row, reset, 4);

        ChartColorList.Children.Add(row);
        return new ChartColorRow(label, swatch, hexText, stateText, reset, picker);
    }

    /// <summary>
    /// Repaints every swatch from the palette the CHARTS use, so an unset model previews the exact
    /// automatic colour it will be drawn in - including its theme-derived accent.
    /// </summary>
    /// <remarks>
    /// The palette is built from <c>RootGrid</c>, not the app theme, for the same reason the
    /// graphs window does it: a brush resolved against the app theme renders wrong the moment the
    /// user forces the opposite one.
    /// </remarks>
    private void RefreshChartColorSwatches()
    {
        if (chartColorRows.Count == 0)
        {
            return;
        }

        var chartPalette = ChartPalette.For(RootGrid, settings.ChartColorOverrides);

        foreach (var row in chartColorRows)
        {
            var isSet = settings.ChartColorOverrides.ContainsKey(row.Key);
            // An OVERRIDE is drawn exactly as picked, so the palette is authoritative for it. An
            // automatic colour is not: the charts nudge whichever of two collided categories
            // claimed the shared accent second, and that ordering is not reproducible here. Prefer
            // the hex the graphs window recorded as drawn, and fall back to the un-nudged base
            // only for a model this install has never plotted.
            var color = !isSet
                && drawnColors.TryGetValue(row.Key, out var drawn)
                && ChartPalette.TryParseHex(drawn, out var recorded)
                    ? recorded
                    : chartPalette.ForCategory(row.Key);
            var windowsColor = Windows.UI.Color.FromArgb(0xFF, color.Red, color.Green, color.Blue);

            row.Swatch.Background = new SolidColorBrush(windowsColor);
            row.HexText.Text = ChartPalette.ToHex(color);
            // Its own column at the tertiary step rather than two spaces and a word glued onto
            // the end of the hex string.
            row.StateText.Text = isSet ? "Custom" : "Auto";
            row.ResetButton.IsEnabled = isSet;

            // Suppressed: assigning Color raises ColorChanged, which would persist the automatic
            // colour as an explicit override the moment the page was opened. Restored rather than
            // cleared - this also runs during the initial load, which is already suppressed.
            var wasSuppressed = suppressWrites;
            suppressWrites = true;
            row.Picker.Color = windowsColor;
            suppressWrites = wasSuppressed;
        }
    }

    private void OnChartColorPicked(string label, Windows.UI.Color color)
    {
        if (suppressWrites)
        {
            return;
        }

        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (settings.ChartColorOverrides.TryGetValue(label, out var current) &&
            string.Equals(current, hex, StringComparison.OrdinalIgnoreCase))
        {
            pendingChartColor = null;
            chartColorCommitTimer.Stop();
            return;
        }

        pendingChartColor = (label, hex);
        chartColorCommitTimer.Stop();
        chartColorCommitTimer.Start();
    }

    private void CommitPendingChartColor()
    {
        chartColorCommitTimer.Stop();
        if (pendingChartColor is not { } pending)
        {
            return;
        }

        pendingChartColor = null;
        // The SAVE runs even when the window is already closing - OnClosed calls this precisely so
        // a pick made inside the debounce window survives, and gating the save on isClosed (which
        // OnClosed sets first) would make that flush a guaranteed no-op. Only the UI touch-up below
        // is skipped, because those controls are on their way out.
        SaveChartColors(map => map[pending.Key] = pending.Hex);

        if (isClosed)
        {
            return;
        }

        // Only the swatch and the reset button move; the rows themselves are untouched so the
        // open picker flyout survives the save.
        RefreshChartColorSwatches();
        ResetAllChartColorsButton.IsEnabled = settings.ChartColorOverrides.Count > 0;
    }

    private void ResetChartColor(string label)
    {
        pendingChartColor = null;
        chartColorCommitTimer.Stop();

        if (!settings.ChartColorOverrides.ContainsKey(label))
        {
            return;
        }

        SaveChartColors(map => map.Remove(label));
        RenderChartColors();
    }

    private void ResetAllChartColors()
    {
        pendingChartColor = null;
        chartColorCommitTimer.Stop();

        if (settings.ChartColorOverrides.Count == 0)
        {
            return;
        }

        SaveChartColors(map => map.Clear());
        RenderChartColors();
    }

    /// <summary>
    /// Applies one edit to the persisted map. The dictionary is copied rather than mutated because
    /// <see cref="UiSettings"/> is a record whose instances are shared through
    /// <c>AppTheme.Settings</c>; mutating the live one would change it under every reader without
    /// raising <c>Changed</c>.
    /// </summary>
    private void SaveChartColors(Action<Dictionary<string, string>> edit)
    {
        var map = new Dictionary<string, string>(settings.ChartColorOverrides, StringComparer.OrdinalIgnoreCase);
        edit(map);

        settings = settings with { ChartColorOverrides = map };
        settings.Save();
    }

    // ------------------------------------------------------------ usage history

    /// <summary>
    /// Starts the history import, or cancels the one already running - the button is the same
    /// control in both states, so there is never a stop control to hunt for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole job runs on a thread pool thread (<see cref="UsageLedgerBackfill.RunAsync"/> is a
    /// Task.Run over synchronous file I/O), so the UI thread only ever handles the progress reports
    /// and the final result. <see cref="System.Progress{T}"/> captures this window's dispatcher
    /// context at construction, which is what marshals those back here.
    /// </para>
    /// <para>
    /// Everything that resumes after an await re-checks <see cref="isClosed"/> before touching a
    /// control: the window can be closed at any point during a multi-minute run, and a XAML property
    /// set on a closed window takes the process down.
    /// </para>
    /// </remarks>
    private async void ToggleHistoryImport()
    {
        if (importCancellation is { } running)
        {
            running.Cancel();
            ImportHistoryButton.IsEnabled = false;
            ImportHistoryButton.Content = "Cancelling...";
            return;
        }

        var cancellation = new CancellationTokenSource();
        importCancellation = cancellation;
        ImportHistoryButton.Content = "Cancel";
        ImportHistoryProgress.Visibility = Visibility.Visible;
        ImportHistoryProgress.IsIndeterminate = true;
        ImportHistoryProgress.Value = 0;
        ImportHistoryCaption.Text = "Looking for session logs...";
        SetStatus("Importing usage history. You can keep using the app.");

        UsageLedgerBackfillResult result;
        try
        {
            result = await UsageLedgerBackfill.RunAsync(
                new Progress<UsageLedgerBackfillProgress>(OnHistoryImportProgress),
                cancellation.Token);
        }
        catch (Exception exception)
        {
            // RunAsync answers with a Failed result rather than throwing, so this is the belt to
            // that braces - an async void handler that throws is an unhandled exception.
            result = new UsageLedgerBackfillResult(
                UsageLedgerBackfillOutcome.Failed,
                0,
                0,
                null,
                null,
                $"Could not import history: {exception.Message}");
        }
        finally
        {
            importCancellation = null;
            cancellation.Dispose();
        }

        if (isClosed)
        {
            return;
        }

        ImportHistoryButton.IsEnabled = true;
        ImportHistoryButton.Content = "Import";
        ImportHistoryProgress.Visibility = Visibility.Collapsed;
        ImportHistoryProgress.IsIndeterminate = true;
        // A cancel that committed NOTHING goes back to the idle caption, because there is nothing to
        // report. One that had already written a corpus must keep saying so - the backfill goes to
        // the trouble of reporting real counts for a partial import, and dropping them here would
        // put the "nothing was changed" impression back by other means.
        ImportHistoryCaption.Text = result.Outcome == UsageLedgerBackfillOutcome.Cancelled && result.DaysImported == 0
            ? ImportIdleCaption
            : result.Message;

        // The result also goes to the one status line every surface in this app ends with, because
        // that is the line the user is already trained to read - and it is copyable.
        SetStatus(result.Message, result.Outcome switch
        {
            UsageLedgerBackfillOutcome.Failed => StatusLevel.Error,
            UsageLedgerBackfillOutcome.NothingFound => StatusLevel.Warning,
            _ => StatusLevel.Info
        });

        // A cancelled run that committed a corpus wrote real data, so it is worth the same log line
        // as a completed one.
        if (result.Outcome == UsageLedgerBackfillOutcome.Imported || result.DaysImported > 0)
        {
            DiagnosticLog.Write(
                "history import: {0} files, {1} days, {2} to {3}",
                result.FilesScanned,
                result.DaysImported,
                result.FirstDay,
                result.LastDay);
        }
    }

    private void OnHistoryImportProgress(UsageLedgerBackfillProgress progress)
    {
        // Posted through the dispatcher, so one can still be in flight when the window closes.
        if (isClosed)
        {
            return;
        }

        ImportHistoryProgress.IsIndeterminate = progress.FileCount <= 0;
        ImportHistoryProgress.Value = progress.Fraction;
        ImportHistoryCaption.Text = progress.FileCount > 0
            ? $"{progress.Label} {progress.FilesDone:N0} of {progress.FileCount:N0} files."
            : progress.Label;
    }

    // ----------------------------------------------------------------- accounts

    /// <summary>Mark hosts on the generated account rows, so a theme flip can re-tint them.</summary>
    private readonly List<Border> accountMarks = [];

    /// <summary>
    /// Rebuilds the account list. Each editable account is one ROW inside the accounts card with
    /// its editor disclosed underneath it - the flyout's progressive-disclosure pattern - so there
    /// is no separate selected-item form to keep in sync (the WinForms version had a list plus a
    /// detached editor plus three buttons whose enabled state tracked the selection).
    /// </summary>
    private void RenderAccounts()
    {
        AccountList.Children.Clear();
        accountMarks.Clear();

        // The built-in (PATH-resolved) account is declared in XAML; only the extra ones are
        // generated here, because only they are editable.
        foreach (var entry in codexEntries.Where(entry => !entry.IsDefault))
        {
            AccountList.Children.Add(new Border { Style = RowStyle("RowSeparatorStyle") });
            AccountList.Children.Add(CreateAccountEditor(entry));
        }

        BuiltInAccountName.Text = codexEntries.FirstOrDefault(entry => entry.IsDefault)?.Name ?? "Codex";
    }

    private StackPanel CreateAccountEditor(CodexCliEntry entry)
    {
        var mark = new Border
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Child = ProviderGeometry.CreateIcon(UsageProvider.Codex, palette.Glyph)
        };
        accountMarks.Add(mark);

        var pathCaption = new TextBlock
        {
            Style = RowStyle("SecondaryCaptionStyle"),
            Text = entry.BinaryPath ?? string.Empty,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(pathCaption, entry.BinaryPath ?? string.Empty);

        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock { Text = entry.Name, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        identity.Children.Add(pathCaption);

        var nameBox = new TextBox
        {
            Header = "Display name",
            Text = entry.Name,
            PlaceholderText = "Name shown in the flyout"
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameBox, $"Name for {entry.Name}");

        var pathBox = new TextBox
        {
            Header = "Codex binary path",
            Text = entry.BinaryPath ?? string.Empty,
            PlaceholderText = "codex.exe, codex.cmd, or wrapper path"
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pathBox, $"Path for {entry.Name}");

        var browse = new Button { Content = "Browse", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) =>
        {
            var picked = await BrowseForCodexCliAsync();
            if (picked is not null)
            {
                pathBox.Text = picked;
            }
        };

        var pathGrid = new Grid { ColumnSpacing = 8 };
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddAt(pathGrid, pathBox, 0);
        AddAt(pathGrid, browse, 1);

        var save = new Button
        {
            Content = "Save changes",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        save.Click += (_, _) => SaveCodexCli(entry, nameBox.Text, pathBox.Text);

        var remove = new Button { Content = "Remove" };
        remove.Click += (_, _) => RemoveCodexCli(entry);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(save);
        buttons.Children.Add(remove);

        var editor = new StackPanel
        {
            Padding = new Thickness(12, 0, 12, 12),
            Spacing = 10,
            Visibility = Visibility.Collapsed
        };
        editor.Children.Add(nameBox);
        editor.Children.Add(pathGrid);
        editor.Children.Add(buttons);

        var edit = new Button { Content = "Edit", VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, $"Edit {entry.Name}");
        edit.Click += (_, _) =>
        {
            var opening = editor.Visibility != Visibility.Visible;
            editor.Visibility = Shown(opening);
            edit.Content = opening ? "Close" : "Edit";
        };

        var row = new Grid { Style = RowStyle("SettingRowStyle") };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddAt(row, mark, 0);
        AddAt(row, identity, 1);
        AddAt(row, edit, 2);

        var block = new StackPanel();
        block.Children.Add(row);
        block.Children.Add(editor);
        return block;
    }

    private void ShowAddAccountPanel(bool visible)
    {
        AddAccountPanel.Visibility = Shown(visible);
        AddAccountToggle.Content = visible ? "Close" : "Add";
    }

    /// <summary>
    /// Opens the file picker.
    /// </summary>
    /// <remarks>
    /// This uses <c>Microsoft.Windows.Storage.Pickers.FileOpenPicker</c> - the Windows App SDK
    /// picker - NOT the WinRT <c>Windows.Storage.Pickers.FileOpenPicker</c>. The WinRT one was
    /// tried first, with the usual <c>InitializeWithWindow</c> hwnd parenting, and in this
    /// UNPACKAGED build it simply never showed a dialog: no window appeared anywhere on the
    /// desktop, no exception was thrown, and the await never returned. The SDK picker takes an
    /// <c>AppWindow.Id</c> instead of an hwnd and is the one supported without package identity.
    /// </remarks>
    private async System.Threading.Tasks.Task<string?> BrowseForCodexCliAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                CommitButtonText = "Select"
            };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".cmd");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add("*");

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("file picker failed: {0}", ex);
            SetStatus($"Could not open the file picker: {ex.Message}", StatusLevel.Error);
            return null;
        }
    }

    private void AddCodexCli()
    {
        var path = AddPathBox.Text.Trim();
        if (!ValidateCodexPath(path))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(AddNameBox.Text)
            ? $"Codex {codexEntries.Count + 1}"
            : AddNameBox.Text.Trim();

        codexEntries.Add(new CodexCliEntry(Guid.NewGuid().ToString("N"), name, path));
        SaveCodexCliEntries();

        AddNameBox.Text = string.Empty;
        AddPathBox.Text = string.Empty;
        ShowAddAccountPanel(false);
        SetStatus($"Added “{name}”.");
    }

    private void SaveCodexCli(CodexCliEntry entry, string name, string path)
    {
        path = path.Trim();
        if (!ValidateCodexPath(path))
        {
            return;
        }

        var index = codexEntries.FindIndex(candidate => candidate.Id == entry.Id);
        if (index < 0)
        {
            return;
        }

        var resolvedName = string.IsNullOrWhiteSpace(name) ? entry.Name : name.Trim();
        codexEntries[index] = entry with { Name = resolvedName, BinaryPath = path };
        SaveCodexCliEntries();
        SetStatus($"Saved “{resolvedName}”.");
    }

    private void RemoveCodexCli(CodexCliEntry entry)
    {
        codexEntries.RemoveAll(candidate => candidate.Id == entry.Id);
        SaveCodexCliEntries();
        SetStatus($"Removed “{entry.Name}”.");
    }

    private bool ValidateCodexPath(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        SetStatus(
            string.IsNullOrWhiteSpace(path)
                ? "Enter the path to an existing Codex CLI binary or wrapper script."
                : $"Not found: {path}",
            StatusLevel.Warning);
        return false;
    }

    /// <summary>
    /// Writes the account list and tells the refresh service. <c>ReloadCodexEntries</c> keeps
    /// the cached usage of accounts whose binary did not move and drops the state of accounts
    /// that disappeared, then refreshes - so the flyout's cards and numbers follow this window
    /// immediately.
    /// </summary>
    private void SaveCodexCliEntries()
    {
        CodexCliSettings.SaveAdditional(codexEntries);
        service.ReloadCodexEntries();
        RenderAccounts();
    }

    // ------------------------------------------------------------------- cursor

    private void SaveCursorCookieHeader()
    {
        var normalized = CursorUsageReader.NormalizeCookieHeader(CursorCookieBox.Password);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            SetStatus(
                "Paste a Cookie header from a cursor.com request, or use Clear to remove the saved one.",
                StatusLevel.Warning);
            return;
        }

        CursorSettings.SaveCookieHeader(normalized);
        CursorCookieBox.Password = normalized;
        RenderCursorSavedState();
        service.Refresh();
        SetStatus("Cookie header saved. Cursor usage will refresh.");
    }

    private void ClearCursorCookieHeader()
    {
        CursorSettings.SaveCookieHeader(string.Empty);
        CursorCookieBox.Password = string.Empty;
        RenderCursorSavedState();
        service.Refresh();
        SetStatus("Cookie header cleared.");
    }

    /// <summary>
    /// Says whether a header is stored without printing it: the value is a live session
    /// credential, so only its length is ever shown.
    /// </summary>
    private void RenderCursorSavedState()
    {
        var stored = CursorSettings.LoadCookieHeader();
        CursorSavedText.Text = string.IsNullOrWhiteSpace(stored)
            ? "No cookie header saved - Cursor usage is unavailable."
            : $"A cookie header is saved ({stored.Length} characters).";
    }

    // ------------------------------------------------------------ OpenCode Go

    private void SaveOpenCodeGoSettings()
    {
        var normalizedCookie = OpenCodeGoUsageReader.NormalizeCookieHeader(OpenCodeGoCookieBox.Password);
        if (string.IsNullOrWhiteSpace(normalizedCookie))
        {
            SetStatus(
                "Paste the auth cookie value from opencode.ai, or use Clear session to remove the saved one.",
                StatusLevel.Warning);
            return;
        }

        var rawWorkspace = OpenCodeGoWorkspaceBox.Text.Trim();
        var workspaceId = OpenCodeGoUsageReader.NormalizeWorkspaceId(rawWorkspace);
        if (rawWorkspace.Length > 0 && workspaceId is null)
        {
            SetStatus(
                "The workspace must be a wrk_… id or a full opencode.ai workspace URL.",
                StatusLevel.Warning);
            return;
        }

        OpenCodeGoSettings.Save(normalizedCookie, workspaceId);
        OpenCodeGoCookieBox.Password = OpenCodeGoUsageReader.SessionValue(normalizedCookie);
        OpenCodeGoWorkspaceBox.Text = workspaceId ?? string.Empty;
        OpenCodeGoEnabledToggle.IsOn = true;
        RenderOpenCodeGoSavedState();
        service.Refresh();
        SetStatus("OpenCode Go session saved. Usage will refresh.");
    }

    private void ClearOpenCodeGoSession()
    {
        var workspaceId = OpenCodeGoUsageReader.NormalizeWorkspaceId(OpenCodeGoWorkspaceBox.Text);
        OpenCodeGoSettings.Save(string.Empty, workspaceId);
        OpenCodeGoCookieBox.Password = string.Empty;
        RenderOpenCodeGoSavedState();
        service.Refresh();
        SetStatus("OpenCode Go session cleared.");
    }

    private void RenderOpenCodeGoSavedState()
    {
        var stored = OpenCodeGoSettings.LoadCookieHeader();
        var workspaceId = OpenCodeGoSettings.LoadWorkspaceId();
        var workspaceText = string.IsNullOrWhiteSpace(workspaceId)
            ? "workspace will be discovered automatically"
            : $"workspace {workspaceId}";
        OpenCodeGoSavedText.Text = string.IsNullOrWhiteSpace(stored)
            ? "No session saved - OpenCode Go usage is unavailable."
            : $"A session value is saved ({OpenCodeGoUsageReader.SessionValue(stored).Length} characters); {workspaceText}.";
    }

    // -------------------------------------------------------------------- glue

    /// <summary>
    /// A style out of this window's own resources. Reading a STYLE from a resource dictionary is
    /// safe where reading a BRUSH is not: a style's setters re-resolve their {ThemeResource}
    /// per element against ActualTheme, while a brush is captured once against the app theme.
    /// </summary>
    private Style RowStyle(string key) => (Style)RootGrid.Resources[key];

    private static void AddAt(Grid grid, FrameworkElement child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }
}
