using System.Drawing.Drawing2D;

// This UI is constructed in code only (no WinForms designer), so designer
// code-serialization metadata for control properties is irrelevant.
#pragma warning disable WFO1000

namespace CodexBarWindows;

/// <summary>
/// Windows 11 Settings-style dialog: a left navigation rail (General, Appearance, Codex
/// accounts, Cursor) and per-section pages built from the Fluent settings cards/expanders.
/// Appearance changes persist through <see cref="UiSettings"/> and restyle this window in place.
/// </summary>
public sealed class SettingsForm : Form
{
    // Additional Segoe Fluent Icons glyphs (not part of the shared FluentIcons set).
    private const string GeneralGlyph = "\uE770";     // System
    private const string AppearanceGlyph = "\uE790";  // Color
    private const string AccountsGlyph = "\uE716";    // People
    private const string CursorGlyph = "\uE774";      // Globe
    private const string StartupGlyph = "\uE7E8";     // Power button
    private const string MaterialGlyph = "\uE771";    // Personalize
    private const string VibesGlyph = "\uE945";       // Lightning bolt

    private readonly Font subtitleFont;
    private readonly Font bodyFont;
    private readonly Font captionFont;
    private readonly List<CodexCliEntry> codexCliEntries;
    private readonly NavigationRail navRail;
    private readonly Panel[] pages;

    private FluentTokens tokens;
    private UiSettings uiSettings;

    private Panel generalPage = null!;
    private Panel appearancePage = null!;
    private Panel accountsPage = null!;
    private Panel cursorPage = null!;

    private Label generalTitleLabel = null!;
    private SettingsCard startupCard = null!;
    private FluentToggle startWithWindowsToggle = null!;
    private SettingsCard versionCard = null!;

    private Label appearanceTitleLabel = null!;
    private SettingsCard themeCard = null!;
    private FluentComboBox themeCombo = null!;
    private SettingsExpander materialExpander = null!;
    private FluentComboBox materialCombo = null!;
    private SettingsExpanderRow opacityRow = null!;
    private Panel opacityPanel = null!;
    private FluentSlider opacitySlider = null!;
    private Label opacityValueLabel = null!;
    private SettingsCard vibesCard = null!;
    private FluentToggle vibesToggle = null!;

    private Label accountsTitleLabel = null!;
    private Label accountsCaptionLabel = null!;
    private AccountListBox accountListBox = null!;
    private Label accountNameLabel = null!;
    private FluentTextField accountNameTextBox = null!;
    private Label binaryPathLabel = null!;
    private FluentTextField binaryPathTextBox = null!;
    private FluentButton browseButton = null!;
    private FluentButton addButton = null!;
    private FluentButton saveButton = null!;
    private FluentButton removeButton = null!;

    private Label cursorTitleLabel = null!;
    private Label cursorCaptionLabel = null!;
    private SettingsCard cursorCard = null!;
    private FluentTextField cursorCookieTextBox = null!;
    private FluentButton saveCursorCookieButton = null!;
    private FluentButton clearCursorCookieButton = null!;

    public event EventHandler? CodexCliEntriesChanged;
    public event EventHandler? CursorSettingsChanged;

    public SettingsForm()
    {
        // Re-read the accent so a dialog opened after an accent change does not paint the
        // launch-time color (the popup only refreshes the cache once its own handle exists).
        FluentTheme.RefreshAccent();
        uiSettings = UiSettings.Load();
        tokens = FluentTheme.Get(uiSettings.ResolveIsDark(), onBackdrop: false);

        codexCliEntries = CodexCliSettings.Load().ToList();

        subtitleFont = FluentTheme.SubtitleFont(1f);
        bodyFont = FluentTheme.BodyFont(1f);
        captionFont = FluentTheme.CaptionFont(1f);

        // Without explicit AutoScaleDimensions, AutoScaleMode.Dpi never scales (factor stays 1.0)
        // and the DeviceDpi-scaled paint metrics would overflow the unscaled control bounds.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = tokens.Background;
        ClientSize = new Size(780, 560);
        Font = bodyFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = TrayIconFactory.Create();
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "CodexBar Settings";

        navRail = new NavigationRail(tokens)
        {
            Location = new Point(8, 16),
            Size = new Size(192, 160)
        };
        navRail.AddItem(GeneralGlyph, "General");
        navRail.AddItem(AppearanceGlyph, "Appearance");
        navRail.AddItem(AccountsGlyph, "Codex accounts");
        navRail.AddItem(CursorGlyph, "Cursor");

        generalPage = CreatePage();
        appearancePage = CreatePage();
        accountsPage = CreatePage();
        cursorPage = CreatePage();
        pages = [generalPage, appearancePage, accountsPage, cursorPage];

        BuildGeneralPage();
        BuildAppearancePage();
        BuildAccountsPage();
        BuildCursorPage();

        Controls.AddRange([navRail, generalPage, appearancePage, accountsPage, cursorPage]);

        navRail.SelectedIndexChanged += (_, _) => ShowPage(navRail.SelectedIndex);

        cursorCookieTextBox.Text = CursorSettings.LoadCookieHeader();
        RefreshCodexCliList();
        ShowPage(0);
        ApplyThemeToTree();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = WindowEffects.TryApplyBackdrop(Handle, SystemBackdrop.Mica);
        WindowEffects.SetImmersiveDarkMode(Handle, tokens.IsDark);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Expanded after the form has auto-scaled so the expander computes its expanded height
        // from final (per-monitor) device metrics instead of the pre-scale designer units.
        materialExpander.Expanded = true;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            subtitleFont.Dispose();
            bodyFont.Dispose();
            captionFont.Dispose();
        }
    }

    private Panel CreatePage()
    {
        return new Panel
        {
            AutoScroll = true,
            BackColor = tokens.Background,
            Location = new Point(200, 0),
            Size = new Size(580, 560),
            Visible = false
        };
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visible = i == index;
        }
    }

    private void BuildGeneralPage()
    {
        generalTitleLabel = CreatePageTitle("General");

        startWithWindowsToggle = new FluentToggle(tokens)
        {
            Checked = StartupSettings.IsEnabled,
            Size = new Size(40, 20)
        };
        startWithWindowsToggle.CheckedChanged += (_, _) => StartupSettings.SetEnabled(startWithWindowsToggle.Checked);

        startupCard = new SettingsCard(tokens)
        {
            Description = "Launch CodexBar automatically when you sign in",
            Glyph = StartupGlyph,
            Location = new Point(24, 70),
            Size = new Size(532, 64),
            Title = "Start with Windows"
        };
        startupCard.ActionControl = startWithWindowsToggle;

        versionCard = new SettingsCard(tokens)
        {
            Description = AppInfo.VersionText,
            Glyph = FluentIcons.Info,
            Location = new Point(24, 142),
            Size = new Size(532, 64),
            Title = "Version"
        };

        generalPage.Controls.AddRange([generalTitleLabel, startupCard, versionCard]);
    }

    private void BuildAppearancePage()
    {
        appearanceTitleLabel = CreatePageTitle("Appearance");

        themeCombo = new FluentComboBox(tokens)
        {
            Size = new Size(180, 32)
        };
        themeCombo.Items.AddRange(["System default", "Light", "Dark"]);
        themeCombo.SelectedIndex = (int)uiSettings.Theme;

        themeCard = new SettingsCard(tokens)
        {
            Description = "Choose how CodexBar looks",
            Glyph = AppearanceGlyph,
            Location = new Point(24, 70),
            Size = new Size(532, 64),
            Title = "Theme"
        };
        themeCard.ActionControl = themeCombo;

        materialCombo = new FluentComboBox(tokens)
        {
            Size = new Size(180, 32)
        };
        materialCombo.Items.AddRange(["Acrylic (default)", "Mica", "Mica Alt", "Solid"]);
        materialCombo.SelectedIndex = (int)uiSettings.Material;

        materialExpander = new SettingsExpander(tokens)
        {
            Description = "Select the visual material used for the flyout background",
            Glyph = MaterialGlyph,
            Location = new Point(24, 142),
            Size = new Size(532, 64),
            Title = "Material"
        };
        materialExpander.HeaderControl = materialCombo;

        opacitySlider = new FluentSlider(tokens)
        {
            Enabled = uiSettings.EffectiveMaterial != BackdropMaterial.Solid,
            Location = new Point(0, 0),
            Maximum = 100,
            Minimum = 0,
            Size = new Size(220, 28),
            Value = uiSettings.TintOpacityPercent
        };

        opacityValueLabel = new Label
        {
            AutoSize = false,
            BackColor = tokens.CardFill,
            Font = captionFont,
            Location = new Point(228, 0),
            Size = new Size(40, 28),
            Text = uiSettings.TintOpacityPercent.ToString(),
            TextAlign = ContentAlignment.MiddleRight
        };

        opacityPanel = new Panel
        {
            BackColor = tokens.CardFill,
            Size = new Size(268, 28)
        };
        opacityPanel.Controls.AddRange([opacitySlider, opacityValueLabel]);

        opacityRow = new SettingsExpanderRow(tokens)
        {
            Description = "Background tint strength",
            Size = new Size(532, 64),
            Title = "Opacity"
        };
        opacityRow.ActionControl = opacityPanel;
        materialExpander.AddRow(opacityRow);

        vibesToggle = new FluentToggle(tokens)
        {
            Checked = uiSettings.VibesEnabled,
            Size = new Size(40, 20)
        };

        vibesCard = new SettingsCard(tokens)
        {
            Description = "V3 Code theme, animated meters, and little celebrations.",
            Glyph = VibesGlyph,
            Location = new Point(24, 278),
            Size = new Size(532, 64),
            Title = "Turn on the vibes"
        };
        vibesCard.ActionControl = vibesToggle;

        // Vibes always rides the stock Acrylic backdrop: the material picker is inert while
        // the toggle is on, but the tint opacity slider stays live so translucency remains
        // adjustable.
        materialCombo.Enabled = !uiSettings.VibesEnabled;

        // Subscribed after the initial values are set so construction never writes settings.
        themeCombo.SelectedIndexChanged += (_, _) => OnThemeSelectionChanged();
        materialCombo.SelectedIndexChanged += (_, _) => OnMaterialSelectionChanged();
        opacitySlider.ValueChanged += (_, _) => OnOpacityChanged();
        vibesToggle.CheckedChanged += (_, _) => OnVibesToggled();

        appearancePage.Controls.AddRange([appearanceTitleLabel, themeCard, materialExpander, vibesCard]);
    }

    private void BuildAccountsPage()
    {
        accountsTitleLabel = CreatePageTitle("Codex accounts");
        accountsCaptionLabel = CreateCaption(
            "Add another authenticated CLI binary or wrapper script for each extra account.",
            new Point(24, 58),
            new Size(532, 18));

        accountListBox = new AccountListBox(tokens)
        {
            Location = new Point(24, 88),
            Size = new Size(220, 240)
        };
        accountListBox.SelectedIndexChanged += (_, _) => PopulateSelectedCodexCli();

        accountNameLabel = CreateCaption("Display name", new Point(260, 88), new Size(296, 16));
        accountNameTextBox = new FluentTextField(tokens)
        {
            Location = new Point(260, 108),
            Size = new Size(296, 32),
            PlaceholderText = "Name shown in popup"
        };

        binaryPathLabel = CreateCaption("Codex binary path", new Point(260, 152), new Size(296, 16));
        binaryPathTextBox = new FluentTextField(tokens)
        {
            Location = new Point(260, 172),
            Size = new Size(210, 32),
            PlaceholderText = "codex.exe, codex.cmd, or wrapper path"
        };

        browseButton = new FluentButton(tokens, FluentButtonKind.Secondary)
        {
            Location = new Point(478, 172),
            Size = new Size(78, 32),
            Text = "Browse"
        };
        browseButton.Click += (_, _) => BrowseForCodexCli();

        addButton = new FluentButton(tokens, FluentButtonKind.Primary)
        {
            Location = new Point(260, 220),
            Size = new Size(86, 32),
            Text = "Add"
        };
        addButton.Click += (_, _) => AddCodexCli();

        saveButton = new FluentButton(tokens, FluentButtonKind.Secondary)
        {
            Location = new Point(354, 220),
            Size = new Size(86, 32),
            Text = "Save"
        };
        saveButton.Click += (_, _) => SaveSelectedCodexCli();

        removeButton = new FluentButton(tokens, FluentButtonKind.Secondary)
        {
            Location = new Point(448, 220),
            Size = new Size(100, 32),
            Text = "Remove"
        };
        removeButton.Click += (_, _) => RemoveSelectedCodexCli();

        accountsPage.Controls.AddRange([
            accountsTitleLabel,
            accountsCaptionLabel,
            accountListBox,
            accountNameLabel,
            accountNameTextBox,
            binaryPathLabel,
            binaryPathTextBox,
            browseButton,
            addButton,
            saveButton,
            removeButton
        ]);
    }

    private void BuildCursorPage()
    {
        cursorTitleLabel = CreatePageTitle("Cursor");
        cursorCaptionLabel = CreateCaption(
            "Paste a Cookie header from a cursor.com request to show Cursor usage.",
            new Point(24, 58),
            new Size(532, 18));

        cursorCookieTextBox = new FluentTextField(tokens)
        {
            Location = new Point(16, 52),
            Size = new Size(340, 32),
            PlaceholderText = "Cookie: WorkosCursorSessionToken=..."
        };

        saveCursorCookieButton = new FluentButton(tokens, FluentButtonKind.Primary)
        {
            Location = new Point(364, 52),
            Size = new Size(72, 32),
            Text = "Save"
        };
        saveCursorCookieButton.Click += (_, _) => SaveCursorCookieHeader();

        clearCursorCookieButton = new FluentButton(tokens, FluentButtonKind.Secondary)
        {
            Location = new Point(444, 52),
            Size = new Size(72, 32),
            Text = "Clear"
        };
        clearCursorCookieButton.Click += (_, _) => ClearCursorCookieHeader();

        cursorCard = new SettingsCard(tokens)
        {
            Location = new Point(24, 88),
            Size = new Size(532, 100),
            Title = "Cookie header",
            TopAlignContent = true
        };
        cursorCard.Controls.AddRange([cursorCookieTextBox, saveCursorCookieButton, clearCursorCookieButton]);

        cursorPage.Controls.AddRange([cursorTitleLabel, cursorCaptionLabel, cursorCard]);
    }

    private void OnThemeSelectionChanged()
    {
        var theme = (AppThemeMode)Math.Clamp(themeCombo.SelectedIndex, 0, 2);
        if (theme == uiSettings.Theme)
        {
            return;
        }

        uiSettings = uiSettings with { Theme = theme };
        uiSettings.Save();
        ApplyThemeToTree();
    }

    private void OnMaterialSelectionChanged()
    {
        var material = (BackdropMaterial)Math.Clamp(materialCombo.SelectedIndex, 0, 3);
        if (material == uiSettings.Material)
        {
            return;
        }

        uiSettings = uiSettings with { Material = material };
        uiSettings.Save();
        opacitySlider.Enabled = material != BackdropMaterial.Solid;
    }

    private void OnOpacityChanged()
    {
        opacityValueLabel.Text = opacitySlider.Value.ToString();
        if (opacitySlider.Value == uiSettings.TintOpacityPercent)
        {
            return;
        }

        uiSettings = uiSettings with { TintOpacityPercent = opacitySlider.Value };
        uiSettings.Save();
    }

    private void OnVibesToggled()
    {
        if (vibesToggle.Checked == uiSettings.VibesEnabled)
        {
            return;
        }

        uiSettings = uiSettings with { VibesEnabled = vibesToggle.Checked };
        uiSettings.Save();
        materialCombo.Enabled = !uiSettings.VibesEnabled;
        opacitySlider.Enabled = uiSettings.EffectiveMaterial != BackdropMaterial.Solid;
        ApplyThemeToTree();
    }

    /// <summary>
    /// Re-resolves the token set from the current <see cref="UiSettings"/> and restyles the
    /// whole control tree in place (single pass; no controls are rebuilt).
    /// </summary>
    private void ApplyThemeToTree()
    {
        tokens = FluentTheme.Get(uiSettings.ResolveIsDark(), onBackdrop: false);
        BackColor = tokens.Background;

        navRail.ApplyTheme(tokens);

        foreach (var page in pages)
        {
            page.BackColor = tokens.Background;
        }

        foreach (var title in new[] { generalTitleLabel, appearanceTitleLabel, accountsTitleLabel, cursorTitleLabel })
        {
            title.BackColor = tokens.Background;
            title.ForeColor = tokens.TextPrimary;
        }

        foreach (var caption in new[] { accountsCaptionLabel, cursorCaptionLabel, accountNameLabel, binaryPathLabel })
        {
            caption.BackColor = tokens.Background;
            caption.ForeColor = tokens.TextSecondary;
        }

        startupCard.ApplyTheme(tokens);
        versionCard.ApplyTheme(tokens);
        themeCard.ApplyTheme(tokens);
        materialExpander.ApplyTheme(tokens);
        vibesCard.ApplyTheme(tokens);
        cursorCard.ApplyTheme(tokens);

        accountListBox.ApplyTheme(tokens);
        accountNameTextBox.ApplyTheme(tokens);
        binaryPathTextBox.ApplyTheme(tokens);
        browseButton.ApplyTheme(tokens);
        addButton.ApplyTheme(tokens);
        saveButton.ApplyTheme(tokens);
        removeButton.ApplyTheme(tokens);

        // The cascade only re-surfaces hosted controls; the value label keeps its own text color.
        opacityValueLabel.ForeColor = tokens.TextSecondary;

        if (IsHandleCreated)
        {
            WindowEffects.SetImmersiveDarkMode(Handle, tokens.IsDark);
        }

        Invalidate(true);
    }

    private void RefreshCodexCliList()
    {
        var selectedId = accountListBox.SelectedItem is CodexCliEntry selected ? selected.Id : null;
        accountListBox.BeginUpdate();
        accountListBox.Items.Clear();
        foreach (var entry in codexCliEntries)
        {
            accountListBox.Items.Add(entry);
        }

        accountListBox.DisplayMember = nameof(CodexCliEntry.Name);
        accountListBox.EndUpdate();

        var selectedIndex = selectedId is null
            ? 0
            : codexCliEntries.FindIndex(entry => entry.Id == selectedId);
        accountListBox.SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, codexCliEntries.Count - 1));
        PopulateSelectedCodexCli();
    }

    private void PopulateSelectedCodexCli()
    {
        var entry = accountListBox.SelectedItem as CodexCliEntry;
        accountNameTextBox.Text = entry?.Name ?? string.Empty;
        binaryPathTextBox.Text = entry?.BinaryPath ?? string.Empty;

        var canEditSelected = entry is not null && !entry.IsDefault;
        accountNameTextBox.Enabled = true;
        binaryPathTextBox.Enabled = true;
        browseButton.Enabled = true;
        saveButton.Enabled = canEditSelected;
        removeButton.Enabled = canEditSelected;
    }

    private void BrowseForCodexCli()
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Executable or command files|*.exe;*.cmd;*.bat|All files|*.*",
            Title = "Select Codex CLI binary"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            binaryPathTextBox.Text = dialog.FileName;
        }
    }

    private void AddCodexCli()
    {
        var path = binaryPathTextBox.Text.Trim();
        if (!ValidateCodexPath(path))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(accountNameTextBox.Text)
            ? $"Codex {codexCliEntries.Count + 1}"
            : accountNameTextBox.Text.Trim();

        codexCliEntries.Add(new CodexCliEntry(Guid.NewGuid().ToString("N"), name, path));
        SaveCodexCliEntries();
        RefreshCodexCliList();
        accountListBox.SelectedIndex = codexCliEntries.Count - 1;
    }

    private void SaveSelectedCodexCli()
    {
        if (accountListBox.SelectedItem is not CodexCliEntry entry || entry.IsDefault)
        {
            return;
        }

        var path = binaryPathTextBox.Text.Trim();
        if (!ValidateCodexPath(path))
        {
            return;
        }

        var index = codexCliEntries.FindIndex(candidate => candidate.Id == entry.Id);
        if (index < 0)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(accountNameTextBox.Text)
            ? entry.Name
            : accountNameTextBox.Text.Trim();
        codexCliEntries[index] = entry with { Name = name, BinaryPath = path };
        SaveCodexCliEntries();
        RefreshCodexCliList();
    }

    private void RemoveSelectedCodexCli()
    {
        if (accountListBox.SelectedItem is not CodexCliEntry entry || entry.IsDefault)
        {
            return;
        }

        codexCliEntries.RemoveAll(candidate => candidate.Id == entry.Id);
        SaveCodexCliEntries();
        RefreshCodexCliList();
    }

    private bool ValidateCodexPath(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        MessageBox.Show(
            this,
            "Choose an existing Codex CLI binary or wrapper script.",
            "Codex CLI path",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return false;
    }

    private void SaveCodexCliEntries()
    {
        CodexCliSettings.SaveAdditional(codexCliEntries);
        CodexCliEntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveCursorCookieHeader()
    {
        var normalized = CursorUsageReader.NormalizeCookieHeader(cursorCookieTextBox.Text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            MessageBox.Show(
                this,
                "Paste a Cookie header from a cursor.com request, or use Clear to remove the current header.",
                "Cursor Cookie header",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        CursorSettings.SaveCookieHeader(normalized);
        cursorCookieTextBox.Text = normalized;
        CursorSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearCursorCookieHeader()
    {
        CursorSettings.SaveCookieHeader(string.Empty);
        cursorCookieTextBox.Text = string.Empty;
        CursorSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private Label CreatePageTitle(string text)
    {
        return new Label
        {
            AutoSize = false,
            Font = subtitleFont,
            Location = new Point(24, 24),
            Size = new Size(532, 30),
            Text = text
        };
    }

    private Label CreateCaption(string text, Point location, Size size)
    {
        return new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Font = captionFont,
            Location = location,
            Size = size,
            Text = text
        };
    }

    /// <summary>
    /// Windows 11 Settings-style navigation rail: 36px items with a 16px glyph and Body label,
    /// SubtleHover hot-tracking and ControlFill + accent pill for the selected item.
    /// </summary>
    private sealed class NavigationRail : Control
    {
        private const float ItemHeight96 = 36f;
        private const float ItemGap96 = 4f;

        private readonly List<(string Glyph, string Label)> items = [];
        private readonly Font labelFont = FluentTheme.BodyFont(1f);
        private readonly Font iconFont = FluentIcons.CreateFont(12f);
        private FluentTokens tokens;
        private int selectedIndex;
        private int hotIndex = -1;

        public event EventHandler? SelectedIndexChanged;

        public NavigationRail(FluentTokens tokens)
        {
            this.tokens = tokens;
            BackColor = tokens.Background;
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        public int SelectedIndex
        {
            get => selectedIndex;
            set
            {
                var clamped = Math.Clamp(value, 0, Math.Max(0, items.Count - 1));
                if (clamped == selectedIndex)
                {
                    return;
                }

                selectedIndex = clamped;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void AddItem(string glyph, string label)
        {
            items.Add((glyph, label));
            Invalidate();
        }

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;
            BackColor = palette.Background;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                labelFont.Dispose();
                iconFont.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var index = ItemIndexAt(e.Location);
            if (index != hotIndex)
            {
                hotIndex = index;
                Invalidate();
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hotIndex != -1)
            {
                hotIndex = -1;
                Invalidate();
            }

            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var index = ItemIndexAt(e.Location);
                if (index >= 0)
                {
                    SelectedIndex = index;
                }
            }

            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f;

            for (var i = 0; i < items.Count; i++)
            {
                var bounds = ItemBounds(i);
                var selected = i == selectedIndex;

                if (selected || i == hotIndex)
                {
                    using var fillBrush = new SolidBrush(selected ? tokens.ControlFill : tokens.SubtleHover);
                    using var fillPath = FluentTheme.RoundedRect(bounds, FluentTheme.ControlCornerRadius * scale);
                    graphics.FillPath(fillBrush, fillPath);
                }

                if (selected)
                {
                    var pillWidth = 3f * scale;
                    var pillHeight = 16f * scale;
                    var pillBounds = new RectangleF(
                        bounds.Left,
                        bounds.Top + ((bounds.Height - pillHeight) / 2f),
                        pillWidth,
                        pillHeight);
                    using var pillBrush = new SolidBrush(tokens.Accent);
                    using var pillPath = FluentTheme.RoundedRect(pillBounds, pillWidth / 2f);
                    graphics.FillPath(pillBrush, pillPath);
                }

                var iconSize = 16f * scale;
                var iconBounds = new RectangleF(
                    bounds.Left + (12f * scale),
                    bounds.Top + ((bounds.Height - iconSize) / 2f),
                    iconSize,
                    iconSize);
                FluentIcons.Draw(graphics, items[i].Glyph, iconFont, tokens.TextPrimary, iconBounds);

                var textBounds = new RectangleF(
                    bounds.Left + (40f * scale),
                    bounds.Top,
                    Math.Max(0f, bounds.Width - (48f * scale)),
                    bounds.Height);
                FluentControlPaint.DrawText(
                    graphics,
                    items[i].Label,
                    labelFont,
                    tokens.TextPrimary,
                    textBounds,
                    StringAlignment.Near);
            }
        }

        private RectangleF ItemBounds(int index)
        {
            var scale = DeviceDpi / 96f;
            var itemHeight = ItemHeight96 * scale;
            var gap = ItemGap96 * scale;
            return new RectangleF(0f, index * (itemHeight + gap), Width, itemHeight);
        }

        private int ItemIndexAt(Point location)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (ItemBounds(i).Contains(location))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Owner-drawn account list: ControlFill surface with a Fluent outline (drawn post-WM_PAINT
    /// because ListBox never raises OnPaint), SubtleHover hot-tracking and an accent selection pill.
    /// </summary>
    private sealed class AccountListBox : ListBox
    {
        private FluentTokens tokens;
        private FluentTokens layerTokens;
        private int hotIndex = -1;

        public AccountListBox(FluentTokens tokens)
        {
            this.tokens = tokens;
            layerTokens = FluentTheme.Get(tokens.IsDark, onBackdrop: true);
            BorderStyle = BorderStyle.None;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 32;
            IntegralHeight = false;
            BackColor = tokens.ControlFill;
            ForeColor = tokens.TextPrimary;
            DoubleBuffered = true;
        }

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;
            layerTokens = FluentTheme.Get(palette.IsDark, onBackdrop: true);
            BackColor = palette.ControlFill;
            ForeColor = palette.TextPrimary;
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ItemHeight = (int)Math.Round(32f * DeviceDpi / 96f);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var index = HotIndexFromPoint(e.Location);
            if (index == hotIndex)
            {
                return;
            }

            InvalidateItem(hotIndex);
            hotIndex = index;
            InvalidateItem(hotIndex);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            InvalidateItem(hotIndex);
            hotIndex = -1;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count)
            {
                return;
            }

            var graphics = e.Graphics;
            var scale = DeviceDpi / 96f;

            using (var surfaceBrush = new SolidBrush(tokens.ControlFill))
            {
                graphics.FillRectangle(surfaceBrush, e.Bounds);
            }

            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var previousSmoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var highlightBounds = new RectangleF(
                e.Bounds.Left + (4f * scale),
                e.Bounds.Top + (2f * scale),
                e.Bounds.Width - (8f * scale),
                e.Bounds.Height - (4f * scale));

            if (selected || e.Index == hotIndex)
            {
                var highlight = selected && tokens.IsDark
                    ? layerTokens.ControlFill
                    : layerTokens.SubtleHover;
                using var highlightBrush = new SolidBrush(highlight);
                using var highlightPath = FluentTheme.RoundedRect(
                    highlightBounds,
                    FluentTheme.ControlCornerRadius * scale);
                graphics.FillPath(highlightBrush, highlightPath);
            }

            if (selected)
            {
                var pillWidth = 3f * scale;
                var pillHeight = 16f * scale;
                var pillBounds = new RectangleF(
                    highlightBounds.Left,
                    e.Bounds.Top + ((e.Bounds.Height - pillHeight) / 2f),
                    pillWidth,
                    pillHeight);
                using var pillBrush = new SolidBrush(tokens.Accent);
                using var pillPath = FluentTheme.RoundedRect(pillBounds, pillWidth / 2f);
                graphics.FillPath(pillBrush, pillPath);
            }

            graphics.SmoothingMode = previousSmoothing;

            var entry = Items[e.Index] as CodexCliEntry;
            var text = entry?.Name ?? Items[e.Index]?.ToString() ?? string.Empty;
            var textBounds = new RectangleF(
                highlightBounds.Left + (12f * scale),
                highlightBounds.Top,
                Math.Max(0f, highlightBounds.Width - (20f * scale)),
                highlightBounds.Height);
            FluentControlPaint.DrawText(graphics, text, Font, tokens.TextPrimary, textBounds, StringAlignment.Near);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // ListBox never sets ControlStyles.UserPaint, so an OnPaint override is never
            // raised; the Fluent outline has to be drawn after the native WM_PAINT instead.
            const int WmPaint = 0x000F;
            if (m.Msg == WmPaint && IsHandleCreated)
            {
                using var graphics = Graphics.FromHwnd(Handle);
                DrawFluentBorder(graphics);
            }
        }

        private void DrawFluentBorder(Graphics graphics)
        {
            var scale = DeviceDpi / 96f;
            var strokeWidth = Math.Max(1f, scale);
            var previousSmoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(tokens.ControlStroke, strokeWidth);
            using var path = FluentTheme.RoundedRect(
                new RectangleF(
                    strokeWidth / 2f,
                    strokeWidth / 2f,
                    Width - strokeWidth,
                    Height - strokeWidth),
                FluentTheme.ControlCornerRadius * scale);
            graphics.DrawPath(pen, path);
            graphics.SmoothingMode = previousSmoothing;
        }

        private int HotIndexFromPoint(Point location)
        {
            var index = IndexFromPoint(location);
            if (index < 0 || index >= Items.Count)
            {
                return -1;
            }

            return GetItemRectangle(index).Contains(location) ? index : -1;
        }

        private void InvalidateItem(int index)
        {
            if (index >= 0 && index < Items.Count)
            {
                Invalidate(GetItemRectangle(index));
            }
        }
    }
}
