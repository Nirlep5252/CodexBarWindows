using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CodexBarWindows;

public sealed class SettingsForm : Form
{
    private readonly ThemePalette theme = ThemePalette.FromWindows();
    private readonly Label headingLabel;
    private readonly Label subtitleLabel;
    private readonly CardPanel generalCard;
    private readonly CardPanel accountsCard;
    private readonly Label generalTitleLabel;
    private readonly Label generalDescriptionLabel;
    private readonly Label startupTitleLabel;
    private readonly Label startupDescriptionLabel;
    private readonly ToggleSwitch startWithWindowsToggle;
    private readonly Label accountsTitleLabel;
    private readonly Label accountsDescriptionLabel;
    private readonly AccountListBox accountListBox;
    private readonly Label accountNameLabel;
    private readonly Label binaryPathLabel;
    private readonly ModernTextField accountNameTextBox;
    private readonly ModernTextField binaryPathTextBox;
    private readonly ModernButton browseButton;
    private readonly ModernButton addButton;
    private readonly ModernButton saveButton;
    private readonly ModernButton removeButton;
    private readonly Label versionLabel;
    private readonly ModernButton closeButton;
    private readonly List<CodexCliEntry> codexCliEntries;

    public event EventHandler? CodexCliEntriesChanged;

    public SettingsForm()
    {
        codexCliEntries = CodexCliSettings.Load().ToList();

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        BackColor = theme.Window;
        ClientSize = new Size(620, 735);
        Font = CreateFont("Segoe UI Variable Text", 9f, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = TrayIconFactory.Create();
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "CodexBarWindows Settings";

        headingLabel = new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Display", 24f, FontStyle.Bold),
            Location = new Point(28, 28),
            Size = new Size(360, 42),
            Text = "Settings"
        };

        subtitleLabel = new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 9.5f, FontStyle.Regular),
            Location = new Point(30, 74),
            Size = new Size(540, 24),
            Text = "Choose how CodexBarWindows launches and which Codex CLI accounts appear in the tray popup."
        };

        generalCard = new CardPanel(theme)
        {
            Location = new Point(28, 122),
            Size = new Size(564, 152)
        };

        generalTitleLabel = CardTitle("General", new Point(20, 18), new Size(220, 28));
        generalDescriptionLabel = CardDescription(
            "Core tray behavior and startup preferences.",
            new Point(20, 48),
            new Size(500, 24));
        startupTitleLabel = RowTitle("Auto start on login", new Point(20, 96), new Size(220, 24));
        startupDescriptionLabel = RowDescription(
            "Launch automatically when you sign in to Windows.",
            new Point(20, 119),
            new Size(360, 22));
        startWithWindowsToggle = new ToggleSwitch(theme)
        {
            Checked = StartupSettings.IsEnabled,
            Location = new Point(478, 103),
            Size = new Size(58, 28)
        };
        startWithWindowsToggle.CheckedChanged += (_, _) => StartupSettings.SetEnabled(startWithWindowsToggle.Checked);
        generalCard.Controls.AddRange([
            generalTitleLabel,
            generalDescriptionLabel,
            startupTitleLabel,
            startupDescriptionLabel,
            startWithWindowsToggle
        ]);

        accountsCard = new CardPanel(theme)
        {
            Location = new Point(28, 310),
            Size = new Size(564, 260)
        };

        accountsTitleLabel = CardTitle("Codex CLI Accounts", new Point(20, 18), new Size(260, 28));
        accountsDescriptionLabel = CardDescription(
            "Add another authenticated CLI binary or wrapper script for each extra account.",
            new Point(20, 48),
            new Size(508, 24));

        accountListBox = new AccountListBox(theme)
        {
            Location = new Point(20, 86),
            Size = new Size(190, 92)
        };
        accountListBox.SelectedIndexChanged += (_, _) => PopulateSelectedCodexCli();

        accountNameLabel = FieldLabel("Account name", new Point(230, 82), new Size(140, 18));
        accountNameTextBox = new ModernTextField(theme)
        {
            Location = new Point(230, 103),
            Size = new Size(298, 34),
            PlaceholderText = "Name shown in popup"
        };

        binaryPathLabel = FieldLabel("Binary path", new Point(230, 136), new Size(140, 18));
        binaryPathTextBox = new ModernTextField(theme)
        {
            Location = new Point(230, 157),
            Size = new Size(226, 34),
            PlaceholderText = "codex.exe, codex.cmd, or wrapper path"
        };

        browseButton = new ModernButton(theme, ButtonKind.Secondary)
        {
            Location = new Point(466, 157),
            Size = new Size(62, 34),
            Text = "Browse"
        };
        browseButton.Click += (_, _) => BrowseForCodexCli();

        addButton = new ModernButton(theme, ButtonKind.Primary)
        {
            Location = new Point(230, 210),
            Size = new Size(86, 32),
            Text = "Add"
        };
        addButton.Click += (_, _) => AddCodexCli();

        saveButton = new ModernButton(theme, ButtonKind.Secondary)
        {
            Location = new Point(324, 210),
            Size = new Size(86, 32),
            Text = "Save"
        };
        saveButton.Click += (_, _) => SaveSelectedCodexCli();

        removeButton = new ModernButton(theme, ButtonKind.Secondary)
        {
            Location = new Point(418, 210),
            Size = new Size(110, 32),
            Text = "Remove"
        };
        removeButton.Click += (_, _) => RemoveSelectedCodexCli();

        accountsCard.Controls.AddRange([
            accountsTitleLabel,
            accountsDescriptionLabel,
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

        versionLabel = new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 9f, FontStyle.Regular),
            Location = new Point(30, 696),
            Size = new Size(360, 24),
            Text = $"Version {AppInfo.VersionText}"
        };

        closeButton = new ModernButton(theme, ButtonKind.Secondary)
        {
            Location = new Point(488, 688),
            Size = new Size(104, 34),
            Text = "Close"
        };
        closeButton.Click += (_, _) => Close();

        Controls.AddRange([
            headingLabel,
            subtitleLabel,
            generalCard,
            accountsCard,
            versionLabel,
            closeButton
        ]);

        RefreshCodexCliList();
        ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowAttributes(Handle, theme.IsDark);
    }

    private void ApplyTheme()
    {
        headingLabel.BackColor = theme.Window;
        headingLabel.ForeColor = theme.TextPrimary;
        subtitleLabel.BackColor = theme.Window;
        subtitleLabel.ForeColor = theme.TextSecondary;
        versionLabel.BackColor = theme.Window;
        versionLabel.ForeColor = theme.TextSecondary;

        foreach (var label in new[]
                 {
                     generalTitleLabel,
                     startupTitleLabel,
                     accountsTitleLabel
                 })
        {
            label.BackColor = theme.Card;
            label.ForeColor = theme.TextPrimary;
        }

        foreach (var label in new[]
                 {
                     generalDescriptionLabel,
                     startupDescriptionLabel,
                     accountsDescriptionLabel,
                     accountNameLabel,
                     binaryPathLabel
                 })
        {
            label.BackColor = theme.Card;
            label.ForeColor = theme.TextSecondary;
        }

        accountNameTextBox.ApplyTheme(theme);
        binaryPathTextBox.ApplyTheme(theme);
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

    private static Label CardTitle(string text, Point location, Size size)
    {
        return new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 13f, FontStyle.Bold),
            Location = location,
            Size = size,
            Text = text
        };
    }

    private static Label CardDescription(string text, Point location, Size size)
    {
        return new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 9.2f, FontStyle.Regular),
            Location = location,
            Size = size,
            Text = text
        };
    }

    private static Label RowTitle(string text, Point location, Size size)
    {
        return new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 10.2f, FontStyle.Bold),
            Location = location,
            Size = size,
            Text = text
        };
    }

    private static Label RowDescription(string text, Point location, Size size)
    {
        return new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 9f, FontStyle.Regular),
            Location = location,
            Size = size,
            Text = text
        };
    }

    private static Label FieldLabel(string text, Point location, Size size)
    {
        return new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 8.5f, FontStyle.Regular),
            Location = location,
            Size = size,
            Text = text
        };
    }

    private static Font CreateFont(string family, float size, FontStyle style)
    {
        try
        {
            return new Font(family, size, style);
        }
        catch
        {
            return new Font("Segoe UI", size, style);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle rectangle, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        var arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static bool IsDarkTheme()
    {
        var value = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1);

        return value is int intValue && intValue == 0;
    }

    private static void ApplyWindowAttributes(IntPtr handle, bool isDarkMode)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var preference = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));

        var darkMode = isDarkMode ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    private sealed record ThemePalette(
        bool IsDark,
        Color Window,
        Color Card,
        Color CardBorder,
        Color Input,
        Color InputBorder,
        Color TextPrimary,
        Color TextSecondary,
        Color Accent,
        Color AccentText,
        Color Hover,
        Color Pressed,
        Color Disabled,
        Color DisabledText)
    {
        public static ThemePalette FromWindows()
        {
            return IsDarkTheme() ? Dark() : Light();
        }

        private static ThemePalette Dark()
        {
            return new ThemePalette(
                true,
                Color.FromArgb(32, 32, 32),
                Color.FromArgb(43, 43, 43),
                Color.FromArgb(58, 58, 58),
                Color.FromArgb(52, 52, 52),
                Color.FromArgb(86, 86, 86),
                Color.FromArgb(246, 246, 246),
                Color.FromArgb(196, 196, 196),
                Color.FromArgb(96, 205, 255),
                Color.FromArgb(0, 0, 0),
                Color.FromArgb(58, 58, 58),
                Color.FromArgb(68, 68, 68),
                Color.FromArgb(49, 49, 49),
                Color.FromArgb(126, 126, 126));
        }

        private static ThemePalette Light()
        {
            return new ThemePalette(
                false,
                Color.FromArgb(243, 243, 243),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(229, 229, 229),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(211, 211, 211),
                Color.FromArgb(32, 31, 30),
                Color.FromArgb(96, 94, 92),
                Color.FromArgb(0, 95, 184),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(245, 245, 245),
                Color.FromArgb(236, 236, 236),
                Color.FromArgb(235, 235, 235),
                Color.FromArgb(132, 132, 132));
        }
    }

    private sealed class CardPanel : Panel
    {
        private readonly ThemePalette theme;

        public CardPanel(ThemePalette theme)
        {
            this.theme = theme;
            BackColor = theme.Card;
            DoubleBuffered = true;
            Padding = new Padding(18);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var fillBrush = new SolidBrush(theme.Card);
            using var borderPen = new Pen(theme.CardBorder);
            using var path = RoundedPath(bounds, 10);
            e.Graphics.FillPath(fillBrush, path);
            e.Graphics.DrawPath(borderPen, path);

            base.OnPaint(e);
        }
    }

    private sealed class ToggleSwitch : Control
    {
        private readonly ThemePalette theme;
        private bool isChecked;
        private bool hovering;

        public event EventHandler? CheckedChanged;

        public ToggleSwitch(ThemePalette theme)
        {
            this.theme = theme;
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.UserPaint,
                true);
        }

        [System.ComponentModel.Browsable(true)]
        [System.ComponentModel.DefaultValue(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Visible)]
        public bool Checked
        {
            get => isChecked;
            set
            {
                if (isChecked == value)
                {
                    return;
                }

                isChecked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Space or Keys.Enter)
            {
                Checked = !Checked;
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            var trackColor = Checked
                ? theme.Accent
                : hovering ? theme.Pressed : theme.InputBorder;
            using var trackBrush = new SolidBrush(trackColor);
            using var trackPath = RoundedPath(track, Height / 2);
            e.Graphics.FillPath(trackBrush, trackPath);

            var knobSize = Height - 8;
            var knobX = Checked ? Width - knobSize - 4 : 4;
            var knob = new Rectangle(knobX, 4, knobSize, knobSize);
            using var knobBrush = new SolidBrush(Checked ? Color.Black : theme.TextPrimary);
            e.Graphics.FillEllipse(knobBrush, knob);
        }
    }

    private enum ButtonKind
    {
        Primary,
        Secondary
    }

    private sealed class ModernButton : Control
    {
        private readonly ThemePalette theme;
        private readonly ButtonKind kind;
        private bool hovering;
        private bool pressing;

        public ModernButton(ThemePalette theme, ButtonKind kind)
        {
            this.theme = theme;
            this.kind = kind;
            Cursor = Cursors.Hand;
            Font = CreateFont("Segoe UI Variable Text", 9f, FontStyle.Regular);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.UserPaint,
                true);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            pressing = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled && e.Button == MouseButtons.Left)
            {
                pressing = true;
                Invalidate();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressing = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && e.KeyCode is Keys.Space or Keys.Enter)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var fill = ButtonFill();
            var stroke = Enabled && kind == ButtonKind.Secondary ? theme.CardBorder : fill;
            var textColor = ButtonTextColor();
            using var fillBrush = new SolidBrush(fill);
            using var borderPen = new Pen(stroke);
            using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 6);
            e.Graphics.FillPath(fillBrush, path);
            e.Graphics.DrawPath(borderPen, path);

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private Color ButtonFill()
        {
            if (!Enabled)
            {
                return theme.Disabled;
            }

            if (kind == ButtonKind.Primary)
            {
                return pressing ? ControlPaint.Dark(theme.Accent, 0.08f) : hovering ? ControlPaint.Light(theme.Accent, 0.08f) : theme.Accent;
            }

            return pressing ? theme.Pressed : hovering ? theme.Hover : theme.Input;
        }

        private Color ButtonTextColor()
        {
            if (!Enabled)
            {
                return theme.DisabledText;
            }

            return kind == ButtonKind.Primary ? theme.AccentText : theme.TextPrimary;
        }
    }

    private sealed class ModernTextField : UserControl
    {
        private readonly TextBox textBox;
        private ThemePalette theme;
        private bool focused;
        private bool hovering;

        public ModernTextField(ThemePalette theme)
        {
            this.theme = theme;
            BackColor = theme.Input;
            DoubleBuffered = true;
            Padding = new Padding(10, 6, 10, 6);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);

            textBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Location = new Point(10, 8),
                Size = new Size(Math.Max(10, Width - 20), 20)
            };
            textBox.GotFocus += (_, _) =>
            {
                focused = true;
                Invalidate();
            };
            textBox.LostFocus += (_, _) =>
            {
                focused = false;
                Invalidate();
            };
            textBox.MouseEnter += (_, _) =>
            {
                hovering = true;
                Invalidate();
            };
            textBox.MouseLeave += (_, _) =>
            {
                hovering = false;
                Invalidate();
            };

            Controls.Add(textBox);
            ApplyTheme(theme);
        }

        [System.ComponentModel.Browsable(true)]
        [System.ComponentModel.DefaultValue("")]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Visible)]
        public new string Text
        {
            get => textBox.Text;
            set => textBox.Text = value ?? string.Empty;
        }

        [System.ComponentModel.Browsable(true)]
        [System.ComponentModel.DefaultValue("")]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Visible)]
        public string PlaceholderText
        {
            get => textBox.PlaceholderText;
            set => textBox.PlaceholderText = value;
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Input;
            textBox.BackColor = theme.Input;
            textBox.ForeColor = theme.TextPrimary;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            textBox.Enabled = Enabled;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnClick(EventArgs e)
        {
            textBox.Focus();
            base.OnClick(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            textBox.Bounds = new Rectangle(10, Height / 2 - textBox.Height / 2, Math.Max(10, Width - 20), textBox.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            var fill = Enabled ? theme.Input : theme.Disabled;
            var border = !Enabled
                ? theme.CardBorder
                : focused
                    ? theme.Accent
                    : hovering
                        ? theme.TextSecondary
                        : theme.InputBorder;
            using var fillBrush = new SolidBrush(fill);
            using var borderPen = new Pen(border, focused ? 1.6f : 1f);
            using var path = RoundedPath(bounds, 6);
            e.Graphics.FillPath(fillBrush, path);
            e.Graphics.DrawPath(borderPen, path);
        }
    }

    private sealed class AccountListBox : ListBox
    {
        private readonly ThemePalette theme;

        public AccountListBox(ThemePalette theme)
        {
            this.theme = theme;
            BorderStyle = BorderStyle.None;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 28;
            IntegralHeight = false;
            BackColor = theme.Input;
            ForeColor = theme.TextPrimary;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0)
            {
                return;
            }

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var bounds = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top + 3, e.Bounds.Width - 8, e.Bounds.Height - 6);

            using var backgroundBrush = new SolidBrush(selected ? theme.Accent : theme.Input);
            using var backgroundPath = RoundedPath(bounds, 6);
            e.Graphics.FillPath(backgroundBrush, backgroundPath);

            var entry = Items[e.Index] as CodexCliEntry;
            var text = entry?.Name ?? Items[e.Index]?.ToString() ?? string.Empty;
            TextRenderer.DrawText(
                e.Graphics,
                text,
                Font,
                new Rectangle(bounds.Left + 10, bounds.Top, bounds.Width - 20, bounds.Height),
                selected ? theme.AccentText : theme.TextPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(theme.InputBorder);
            using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 6);
            e.Graphics.DrawPath(pen, path);
        }
    }
}
