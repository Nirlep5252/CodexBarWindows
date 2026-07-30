using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBarWindows;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
/// Every change applies immediately - there is no OK/Cancel. Persisting through
/// <see cref="UiSettings.Save"/> raises <see cref="UiSettings.Changed"/>, which is what makes
/// the open flyout re-theme and rebuild its tab strip live.
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

    private readonly UsageRefreshService service;
    private readonly IntPtr hwnd;
    private readonly DispatcherQueue queue;
    private readonly DispatcherQueueTimer tintCommitTimer;
    private readonly List<CodexCliEntry> codexEntries;

    private UiSettings settings;
    private int? pendingTintPercent;
    /// <summary>
    /// Set in <see cref="OnClosed"/>. An update check outcome can arrive after the window is
    /// gone, and touching a closed window's controls throws.
    /// </summary>
    private bool isClosed;
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
        settings = AppTheme.Settings;
        codexEntries = CodexCliSettings.Load().ToList();

        Title = $"Settings - {AppInfo.AppName}";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "CodexBarWindows.ico"));

        var scale = NativeWindow.ScaleFor(hwnd);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(920 * scale),
            (int)Math.Round(660 * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
        }

        tintCommitTimer = queue.CreateTimer();
        tintCommitTimer.Interval = TintCommitDelay;
        tintCommitTimer.IsRepeating = false;
        tintCommitTimer.Tick += (_, _) => CommitPendingTint();

        LoadValues();
        WireEvents();

        RootGrid.ActualThemeChanged += (_, _) => AppTheme.ApplyTint(RootGrid, TintLayer);
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

        Nav.SelectedItem = Nav.MenuItems[0];
        suppressWrites = false;
    }

    /// <summary>
    /// Raised whenever this window's activation changes, so the flyout can re-test whether the
    /// foreground is still inside this process. See <see cref="FlyoutWindow.ReArmDismissCheck"/>.
    /// </summary>
    public event EventHandler? ActivationChanged;

    public void ShowAndFocus()
    {
        AppWindow.Show(activateWindow: true);
        NativeWindow.ForceForeground(hwnd);
    }

    // ------------------------------------------------------------------ lifetime

    private void OnClosed()
    {
        isClosed = true;

        // Flush before the window goes: a tint drag that ended inside the debounce window
        // would otherwise be silently dropped.
        CommitPendingTint();
        tintCommitTimer.Stop();
        AppTheme.Changed -= OnThemeChanged;

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
        VersionCard.Description = AppInfo.VersionText;

        // ShellIdentity, not StartupSettings.IsEnabled: the WinForms app owns the unsuffixed
        // Run value, and this shell must not read or overwrite it while both are installed.
        StartupToggle.IsOn = StartupSettings.IsEnabledFor(ShellIdentity.StartupRegistryValueName);
        CodexEnabledToggle.IsOn = settings.CodexEnabled;
        ClaudeEnabledToggle.IsOn = settings.ClaudeEnabled;
        CursorEnabledToggle.IsOn = settings.CursorEnabled;

        ThemeCombo.SelectedIndex = (int)settings.Theme;
        MaterialCombo.SelectedIndex = (int)settings.Material;
        VibesToggle.IsOn = settings.VibesEnabled;
        OpacitySlider.Value = settings.TintOpacityPercent;
        OpacityValueText.Text = settings.TintOpacityPercent.ToString();
        ApplyMaterialEnablement();

        CursorCookieBox.Password = CursorSettings.LoadCookieHeader();
        RenderCursorSavedState();
        RenderAccounts();
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

        ThemeCombo.SelectionChanged += (_, _) => OnThemeSelectionChanged();
        MaterialCombo.SelectionChanged += (_, _) => OnMaterialSelectionChanged();
        VibesToggle.Toggled += (_, _) => OnVibesToggled();
        OpacitySlider.ValueChanged += (_, _) => OnOpacityChanged();

        AddBrowseButton.Click += async (_, _) =>
        {
            var picked = await BrowseForCodexCliAsync();
            if (picked is not null)
            {
                AddPathBox.Text = picked;
            }
        };
        AddAccountButton.Click += (_, _) => AddCodexCli();

        SaveCursorButton.Click += (_, _) => SaveCursorCookieHeader();
        ClearCursorButton.Click += (_, _) => ClearCursorCookieHeader();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        GeneralPage.Visibility = Shown(tag == "general");
        AppearancePage.Visibility = Shown(tag == "appearance");
        AccountsPage.Visibility = Shown(tag == "accounts");
        CursorPage.Visibility = Shown(tag == "cursor");
    }

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------ general

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

        if (!CodexEnabledToggle.IsOn && !ClaudeEnabledToggle.IsOn && !CursorEnabledToggle.IsOn)
        {
            suppressWrites = true;
            CodexEnabledToggle.IsOn = true;
            suppressWrites = false;
            ToolsInfoBar.IsOpen = true;
        }
        else
        {
            ToolsInfoBar.IsOpen = false;
        }

        settings = settings with
        {
            CodexEnabled = CodexEnabledToggle.IsOn,
            ClaudeEnabled = ClaudeEnabledToggle.IsOn,
            CursorEnabled = CursorEnabledToggle.IsOn
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
        UpdateInfoBar.Severity = InfoBarSeverity.Informational;
        UpdateInfoBar.Message = "Checking for updates...";
        UpdateInfoBar.IsOpen = true;

        App.Shell?.CheckForUpdates(result =>
        {
            // The window can be closed while the HTTP call is in flight; the callback still runs
            // because the App owns the check, so the controls have to be treated as gone.
            if (isClosed)
            {
                return;
            }

            UpdateInfoBar.Severity = result.Status switch
            {
                UpdateCheckStatus.UpToDate => InfoBarSeverity.Success,
                UpdateCheckStatus.Installing => InfoBarSeverity.Success,
                _ => InfoBarSeverity.Warning
            };
            UpdateInfoBar.Message = result.Message;
            UpdateInfoBar.IsOpen = true;
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
    /// Vibes always rides the stock Acrylic backdrop, so the material picker is inert while it
    /// is on; the tint slider stays live so translucency remains adjustable, except on Solid
    /// where there is no material to tint.
    /// </summary>
    private void ApplyMaterialEnablement()
    {
        MaterialCombo.IsEnabled = !settings.VibesEnabled;
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

    // ----------------------------------------------------------------- accounts

    /// <summary>
    /// Rebuilds the account list. Each editable account is a <see cref="SettingsExpander"/>
    /// whose expanded body IS its editor, so there is no separate selected-item form to keep
    /// in sync (the WinForms version had a list plus a detached editor plus three buttons whose
    /// enabled state tracked the selection).
    /// </summary>
    private void RenderAccounts()
    {
        AccountList.Children.Clear();

        // The built-in (PATH-resolved) account is declared in XAML; only the extra ones are
        // generated here, because only they are editable.
        foreach (var entry in codexEntries.Where(entry => !entry.IsDefault))
        {
            AccountList.Children.Add(CreateAccountEditor(entry));
        }

        BuiltInAccountCard.Header = codexEntries.FirstOrDefault(entry => entry.IsDefault)?.Name ?? "Codex";
    }

    private SettingsExpander CreateAccountEditor(CodexCliEntry entry)
    {
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
        pathGrid.Children.Add(pathBox);
        Grid.SetColumn(browse, 1);
        pathGrid.Children.Add(browse);

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

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(nameBox);
        body.Children.Add(pathGrid);
        body.Children.Add(buttons);

        var expander = new SettingsExpander
        {
            Header = entry.Name,
            Description = entry.BinaryPath ?? string.Empty,
            HeaderIcon = new FontIcon { Glyph = "\uE77B" }
        };
        expander.Items.Add(new SettingsCard
        {
            ContentAlignment = ContentAlignment.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = body
        });

        return expander;
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
            ShowAccountsMessage($"Could not open the file picker: {ex.Message}", InfoBarSeverity.Error);
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
        AddAccountExpander.IsExpanded = false;
        ShowAccountsMessage($"Added “{name}”.", InfoBarSeverity.Success);
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
        ShowAccountsMessage($"Saved “{resolvedName}”.", InfoBarSeverity.Success);
    }

    private void RemoveCodexCli(CodexCliEntry entry)
    {
        codexEntries.RemoveAll(candidate => candidate.Id == entry.Id);
        SaveCodexCliEntries();
        ShowAccountsMessage($"Removed “{entry.Name}”.", InfoBarSeverity.Informational);
    }

    private bool ValidateCodexPath(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        ShowAccountsMessage(
            string.IsNullOrWhiteSpace(path)
                ? "Enter the path to an existing Codex CLI binary or wrapper script."
                : $"Not found: {path}",
            InfoBarSeverity.Warning);
        return false;
    }

    /// <summary>
    /// Writes the account list and tells the refresh service. <c>ReloadCodexEntries</c> keeps
    /// the cached usage of accounts whose binary did not move and drops the state of accounts
    /// that disappeared, then refreshes - so the flyout's tab strip and numbers follow this
    /// window immediately.
    /// </summary>
    private void SaveCodexCliEntries()
    {
        CodexCliSettings.SaveAdditional(codexEntries);
        service.ReloadCodexEntries();
        RenderAccounts();
    }

    private void ShowAccountsMessage(string message, InfoBarSeverity severity)
    {
        AccountsInfoBar.Severity = severity;
        AccountsInfoBar.Message = message;
        AccountsInfoBar.IsOpen = true;
    }

    // ------------------------------------------------------------------- cursor

    private void SaveCursorCookieHeader()
    {
        var normalized = CursorUsageReader.NormalizeCookieHeader(CursorCookieBox.Password);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            ShowCursorMessage(
                "Paste a Cookie header from a cursor.com request, or use Clear to remove the saved one.",
                InfoBarSeverity.Warning);
            return;
        }

        CursorSettings.SaveCookieHeader(normalized);
        CursorCookieBox.Password = normalized;
        RenderCursorSavedState();
        service.Refresh();
        ShowCursorMessage("Cookie header saved. Cursor usage will refresh.", InfoBarSeverity.Success);
    }

    private void ClearCursorCookieHeader()
    {
        CursorSettings.SaveCookieHeader(string.Empty);
        CursorCookieBox.Password = string.Empty;
        RenderCursorSavedState();
        service.Refresh();
        ShowCursorMessage("Cookie header cleared.", InfoBarSeverity.Informational);
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

    private void ShowCursorMessage(string message, InfoBarSeverity severity)
    {
        CursorInfoBar.Severity = severity;
        CursorInfoBar.Message = message;
        CursorInfoBar.IsOpen = true;
    }
}
