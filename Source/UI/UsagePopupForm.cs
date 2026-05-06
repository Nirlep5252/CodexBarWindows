using Microsoft.Win32;

namespace CodexBarWindows;

public sealed class UsagePopupForm : Form
{
    private const int BaseWidth = 420;
    private const int BaseHeight = 304;
    private readonly Label titleLabel;
    private readonly List<ProviderTabButton> tabButtons = [];
    private readonly List<ProviderDescriptor> providers = [];
    private readonly Dictionary<string, ProviderUsageLookupResult> usageByProvider = [];
    private readonly Label planLabel;
    private readonly Label statusLabel;
    private readonly UsageSection fiveHourSection;
    private readonly UsageSection weeklySection;
    private readonly CloseGlyphButton closeButton;
    private ThemePalette theme = ThemePalette.FromWindows();
    private string selectedProviderKey = CodexProviderKey("default");

    public event EventHandler<string>? SelectedProviderChanged;

    public UsagePopupForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = theme.Surface;
        ClientSize = new Size(BaseWidth, BaseHeight);
        ControlBox = false;
        DoubleBuffered = true;
        Font = CreateFont("Segoe UI Variable Text", 9f, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Codex limits";
        TopMost = true;

        closeButton = new CloseGlyphButton
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TabIndex = 0
        };
        closeButton.Click += (_, _) => Hide();

        titleLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = CreateFont("Segoe UI Variable Display", 15.5f, FontStyle.Bold),
            Text = "Codex rate limits"
        };

        planLabel = new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 9f, FontStyle.Regular)
        };

        fiveHourSection = new UsageSection("5 hour limit");

        weeklySection = new UsageSection("Weekly limit");

        statusLabel = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 8.5f, FontStyle.Regular)
        };

        Controls.Add(titleLabel);
        Controls.Add(planLabel);
        Controls.Add(fiveHourSection);
        Controls.Add(weeklySection);
        Controls.Add(statusLabel);
        Controls.Add(closeButton);

        EnableDragMove(this);
        EnableDragMove(titleLabel);
        EnableDragMove(planLabel);
        EnableDragMove(statusLabel);
        fiveHourSection.EnableDragMove();
        weeklySection.EnableDragMove();

        Deactivate += (_, _) => Hide();
        FormClosing += OnFormClosing;
        ConfigureProviders([new ProviderDescriptor(CodexProviderKey("default"), "Codex", false), ClaudeProvider]);
        ApplyScaledLayout();
        ApplyTheme();
        RenderSelectedProvider();
    }

    public string SelectedProvider => selectedProviderKey;

    protected override CreateParams CreateParams
    {
        get
        {
            const int csDropShadow = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= csDropShadow;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyScaledLayout();
        NativeMethods.ApplyWindowAttributes(Handle, theme.IsDark);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var borderPen = new Pen(theme.Border);
        var borderRect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPath(borderRect, 16);
        e.Graphics.DrawPath(borderPen, path);
    }

    public void ShowNear(Point anchor)
    {
        RefreshTheme();
        ApplyScaledLayout();
        Location = CalculateLocation(anchor);
        Show();
        Activate();
    }

    protected override void WndProc(ref Message m)
    {
        const int wmSettingChange = 0x001A;
        const int wmDpiChanged = 0x02E0;
        const int wmThemeChanged = 0x031A;

        base.WndProc(ref m);

        if (m.Msg is wmSettingChange or wmDpiChanged or wmThemeChanged)
        {
            RefreshTheme();
            ApplyScaledLayout();
        }
    }

    private void ApplyScaledLayout()
    {
        var scale = DpiScale;
        SuspendLayout();

        ClientSize = new Size(ScaleInt(BaseWidth, scale), ScaleInt(BaseHeight, scale));

        closeButton.Bounds = ScaleRect(374, 14, 30, 30, scale);
        planLabel.Bounds = ScaleRect(24, 54, 330, 24, scale);
        var firstTabLeft = LayoutTabButtons(scale);
        var titleLeft = ScaleInt(22, scale);
        titleLabel.Bounds = new Rectangle(
            titleLeft,
            ScaleInt(17, scale),
            Math.Max(ScaleInt(140, scale), firstTabLeft - titleLeft - ScaleInt(10, scale)),
            ScaleInt(34, scale));
        fiveHourSection.Bounds = ScaleRect(18, 82, 384, 86, scale);
        weeklySection.Bounds = ScaleRect(18, 178, 384, 86, scale);
        statusLabel.Bounds = ScaleRect(24, 274, 372, 22, scale);

        fiveHourSection.ApplyLayoutScale(scale);
        weeklySection.ApplyLayoutScale(scale);

        ResumeLayout(performLayout: true);
        Invalidate(true);
    }

    private float DpiScale
    {
        get
        {
            var dpi = IsHandleCreated ? DeviceDpi : 96;
            return Math.Max(1f, dpi / 96f);
        }
    }

    private static Rectangle ScaleRect(int x, int y, int width, int height, float scale)
    {
        return new Rectangle(
            ScaleInt(x, scale),
            ScaleInt(y, scale),
            ScaleInt(width, scale),
            ScaleInt(height, scale));
    }

    private static int ScaleInt(int value, float scale)
    {
        return (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
    }

    public void ConfigureCodexEntries(IReadOnlyList<CodexCliEntry> codexEntries)
    {
        var descriptors = codexEntries
            .Select(entry => new ProviderDescriptor(CodexProviderKey(entry.Id), entry.Name, false))
            .Append(ClaudeProvider)
            .ToList();

        ConfigureProviders(descriptors);
    }

    public void UpdateUsage(string providerKey, ProviderUsageLookupResult result)
    {
        usageByProvider[providerKey] = result;

        if (providerKey == selectedProviderKey)
        {
            RenderSelectedProvider();
        }
    }

    public void SetLoading(string providerKey)
    {
        var provider = GetProvider(providerKey);
        var current = GetProviderUsage(providerKey);
        if (current.Snapshot is { } && providerKey == selectedProviderKey)
        {
            RenderSelectedProvider();
            statusLabel.Text = $"Refreshing {provider.Name} limits...";
            return;
        }

        if (providerKey == selectedProviderKey)
        {
            planLabel.Text = $"Fetching {provider.Name} limits...";
            fiveHourSection.SetLoading("5 hour limit");
            weeklySection.SetLoading("Weekly limit");
            statusLabel.Text = provider.IsClaude
                ? "Reading from Claude Code OAuth..."
                : "Reading from Codex CLI...";
        }
    }

    private void SelectProvider(string providerKey)
    {
        if (selectedProviderKey == providerKey)
        {
            return;
        }

        selectedProviderKey = providerKey;
        RenderSelectedProvider();
        SelectedProviderChanged?.Invoke(this, providerKey);
    }

    private void RenderSelectedProvider()
    {
        foreach (var tabButton in tabButtons)
        {
            tabButton.Selected = tabButton.ProviderKey == selectedProviderKey;
        }

        var provider = GetProvider(selectedProviderKey);
        titleLabel.Text = $"{provider.Name} rate limits";
        var result = GetProviderUsage(selectedProviderKey);

        if (result.Snapshot is not { } snapshot)
        {
            planLabel.Text = $"Waiting for local {provider.Name} usage data";
            fiveHourSection.SetUnavailable("5 hour limit");
            weeklySection.SetUnavailable("Weekly limit");
            statusLabel.Text = result.Error ?? "No usage data found.";
            return;
        }

        planLabel.Text = string.IsNullOrWhiteSpace(snapshot.PlanType)
            ? provider.IsClaude ? "Claude Code usage data" : "Local Codex session data"
            : $"{ToTitleCase(snapshot.PlanType)} plan";

        fiveHourSection.SetUsage(snapshot.Primary);
        if (snapshot.Secondary is { } secondary)
        {
            weeklySection.SetUsage(secondary);
        }
        else
        {
            weeklySection.SetUnavailable(snapshot.Primary.WindowMinutes == 10080 ? "5 hour limit" : "Weekly limit");
        }

        statusLabel.Text = string.Empty;
    }

    private ProviderUsageLookupResult GetProviderUsage(string providerKey)
    {
        return usageByProvider.TryGetValue(providerKey, out var result)
            ? result
            : new ProviderUsageLookupResult(null, "Usage has not been loaded yet.");
    }

    private ProviderDescriptor GetProvider(string providerKey)
    {
        return providers.FirstOrDefault(provider => provider.Key == providerKey)
            ?? providers.FirstOrDefault()
            ?? new ProviderDescriptor(CodexProviderKey("default"), "Codex", false);
    }

    private void ConfigureProviders(IReadOnlyList<ProviderDescriptor> descriptors)
    {
        providers.Clear();
        providers.AddRange(descriptors);

        foreach (var tabButton in tabButtons)
        {
            Controls.Remove(tabButton);
            tabButton.Dispose();
        }

        tabButtons.Clear();
        foreach (var provider in providers)
        {
            usageByProvider.TryAdd(provider.Key, new ProviderUsageLookupResult(null, "Usage has not been loaded yet."));
            var tabButton = new ProviderTabButton(provider.Name, provider.Key, provider.IsClaude)
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AccessibleName = provider.Name
            };
            tabButton.Click += (_, _) => SelectProvider(provider.Key);
            tabButton.ApplyTheme(theme);
            tabButtons.Add(tabButton);
            Controls.Add(tabButton);
        }

        if (!providers.Any(provider => provider.Key == selectedProviderKey))
        {
            selectedProviderKey = providers.FirstOrDefault()?.Key ?? CodexProviderKey("default");
        }

        ApplyScaledLayout();
        RenderSelectedProvider();
    }

    private int LayoutTabButtons(float scale)
    {
        var right = ScaleInt(366, scale);
        var width = ScaleInt(34, scale);
        var gap = ScaleInt(4, scale);
        var firstLeft = right;
        for (var index = tabButtons.Count - 1; index >= 0; index--)
        {
            firstLeft = right - width;
            tabButtons[index].Bounds = new Rectangle(firstLeft, ScaleInt(16, scale), width, ScaleInt(30, scale));
            tabButtons[index].BringToFront();
            right -= width + gap;
        }

        closeButton.BringToFront();
        return firstLeft;
    }

    public static string CodexProviderKey(string id)
    {
        return $"codex:{id}";
    }

    public const string ClaudeProviderKey = "claude";
    private static readonly ProviderDescriptor ClaudeProvider = new(ClaudeProviderKey, "Claude", true);
    private sealed record ProviderDescriptor(string Key, string Name, bool IsClaude);

    private Point CalculateLocation(Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        var workingArea = screen.WorkingArea;

        var x = Math.Clamp(anchor.X - Width + 20, workingArea.Left + 8, workingArea.Right - Width - 8);
        var y = anchor.Y >= workingArea.Top + (workingArea.Height / 2)
            ? workingArea.Bottom - Height - 8
            : workingArea.Top + 8;

        return new Point(x, y);
    }

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

    private static string ToTitleCase(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    private void RefreshTheme()
    {
        var updated = ThemePalette.FromWindows();
        if (updated == theme)
        {
            return;
        }

        theme = updated;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        BackColor = theme.Surface;
        titleLabel.BackColor = theme.Surface;
        titleLabel.ForeColor = theme.TextPrimary;
        planLabel.BackColor = theme.Surface;
        planLabel.ForeColor = theme.TextSecondary;
        statusLabel.BackColor = theme.Surface;
        statusLabel.ForeColor = theme.TextSecondary;

        closeButton.ApplyTheme(theme);
        foreach (var tabButton in tabButtons)
        {
            tabButton.ApplyTheme(theme);
        }

        fiveHourSection.ApplyTheme(theme);
        weeklySection.ApplyTheme(theme);

        if (IsHandleCreated)
        {
            NativeMethods.ApplyWindowAttributes(Handle, theme.IsDark);
        }

        Invalidate(true);
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

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.ApplicationExitCall || e.CloseReason == CloseReason.TaskManagerClosing)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void EnableDragMove(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WmNclButtonDown, NativeMethods.HtCaption, 0);
        };
    }

    private sealed class UsageSection : Panel
    {
        private readonly Label nameLabel;
        private readonly Label percentLabel;
        private readonly Label remainingLabel;
        private readonly Label resetLabel;
        private readonly UsageMeterControl meter;
        private ThemePalette theme = ThemePalette.FromWindows();
        private float layoutScale = 1f;

        public UsageSection(string name)
        {
            BackColor = theme.Card;
            DoubleBuffered = true;
            Padding = new Padding(14, 12, 14, 12);

            nameLabel = new Label
            {
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 9.5f, FontStyle.Bold),
                Text = name
            };

            percentLabel = new Label
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.TopRight
            };

            meter = new UsageMeterControl
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Top
            };

            remainingLabel = new Label
            {
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 8.75f, FontStyle.Regular)
            };

            resetLabel = new Label
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 8.75f, FontStyle.Regular),
                TextAlign = ContentAlignment.TopRight
            };

            Controls.Add(nameLabel);
            Controls.Add(percentLabel);
            Controls.Add(meter);
            Controls.Add(remainingLabel);
            Controls.Add(resetLabel);
            ApplyTheme(theme);
            UpdateChildLayout();
        }

        public void EnableDragMove()
        {
            if (FindForm() is not UsagePopupForm popup)
            {
                HandleCreated += (_, _) =>
                {
                    if (FindForm() is UsagePopupForm createdPopup)
                    {
                        createdPopup.EnableDragMove(this);
                        createdPopup.EnableDragMove(nameLabel);
                        createdPopup.EnableDragMove(percentLabel);
                        createdPopup.EnableDragMove(remainingLabel);
                        createdPopup.EnableDragMove(resetLabel);
                        createdPopup.EnableDragMove(meter);
                    }
                };
                return;
            }

            popup.EnableDragMove(this);
            popup.EnableDragMove(nameLabel);
            popup.EnableDragMove(percentLabel);
            popup.EnableDragMove(remainingLabel);
            popup.EnableDragMove(resetLabel);
            popup.EnableDragMove(meter);
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Card;

            foreach (Control control in Controls)
            {
                control.BackColor = theme.Card;
            }

            nameLabel.ForeColor = theme.TextPrimary;
            percentLabel.ForeColor = theme.Accent;
            remainingLabel.ForeColor = theme.TextSecondary;
            resetLabel.ForeColor = theme.TextSecondary;

            meter.TrackColor = theme.MeterTrack;
            meter.AccentColor = theme.Accent;
            meter.WarningColor = theme.Warning;
            meter.DangerColor = theme.Danger;
            meter.BackColor = theme.Card;

            Invalidate(true);
        }

        public void ApplyLayoutScale(float scale)
        {
            layoutScale = Math.Max(1f, scale);
            UpdateChildLayout();
        }

        public void SetUnavailable(string title)
        {
            nameLabel.Text = title;
            percentLabel.Text = "-- used";
            meter.Value = 0;
            remainingLabel.Text = "-- remaining";
            resetLabel.Text = "Reset unknown";
        }

        public void SetLoading(string title)
        {
            nameLabel.Text = title;
            percentLabel.Text = "Loading...";
            meter.Value = 0;
            remainingLabel.Text = "Fetching usage";
            resetLabel.Text = "Reset loading";
        }

        public void SetUsage(ProviderUsageWindow usage)
        {
            nameLabel.Text = usage.Title;
            percentLabel.Text = $"{usage.UsedPercent:0.#}% used";
            meter.Value = usage.UsedPercent;
            remainingLabel.Text = $"{usage.RemainingPercent:0.#}% remaining";
            resetLabel.Text = usage.ResetsAt is { } resetAt
                ? $"Resets {FormatReset(resetAt)}"
                : "Reset unknown";
        }

        private static string FormatReset(DateTimeOffset resetAt)
        {
            var now = DateTimeOffset.Now;
            var remaining = resetAt - now;

            if (remaining.TotalSeconds <= 0)
            {
                return "now";
            }

            var relative = remaining.TotalDays >= 1
                ? $"in {(int)remaining.TotalDays}d {remaining.Hours}h"
                : remaining.TotalHours >= 1
                    ? $"in {(int)remaining.TotalHours}h {remaining.Minutes}m"
                    : $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";

            return $"{relative}, {resetAt:h:mm tt}";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var fillBrush = new SolidBrush(theme.Card);
            using var borderPen = new Pen(theme.CardBorder);
            using var path = RoundedPath(bounds, 12);

            e.Graphics.FillPath(fillBrush, path);
            e.Graphics.DrawPath(borderPen, path);

            base.OnPaint(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateChildLayout();
        }

        private void UpdateChildLayout()
        {
            var leftPadding = ScaleInt(16, layoutScale);
            var rightPadding = ScaleInt(34, layoutScale);
            var titleTop = ScaleInt(13, layoutScale);
            var titleHeight = ScaleInt(24, layoutScale);
            var meterTop = ScaleInt(43, layoutScale);
            var meterHeight = ScaleInt(8, layoutScale);
            var footerTop = ScaleInt(59, layoutScale);
            var footerHeight = ScaleInt(22, layoutScale);

            nameLabel.Bounds = new Rectangle(
                leftPadding,
                titleTop,
                Math.Max(ScaleInt(130, layoutScale), Width - ScaleInt(220, layoutScale)),
                titleHeight);

            percentLabel.Bounds = new Rectangle(
                Math.Max(leftPadding, Width - ScaleInt(186, layoutScale)),
                titleTop,
                Math.Max(ScaleInt(120, layoutScale), ScaleInt(168, layoutScale)),
                titleHeight);
            percentLabel.Width = Math.Max(
                ScaleInt(110, layoutScale),
                Width - percentLabel.Left - rightPadding);

            meter.Bounds = new Rectangle(
                leftPadding,
                meterTop,
                Math.Max(ScaleInt(80, layoutScale), Width - leftPadding - rightPadding),
                meterHeight);

            remainingLabel.Bounds = new Rectangle(
                leftPadding,
                footerTop,
                Math.Max(ScaleInt(120, layoutScale), Width / 2 - leftPadding),
                footerHeight);

            resetLabel.Bounds = new Rectangle(
                Math.Max(leftPadding, Width - ScaleInt(210, layoutScale)),
                footerTop,
                Math.Max(ScaleInt(140, layoutScale), ScaleInt(192, layoutScale)),
                footerHeight);
            resetLabel.Width = Math.Max(
                ScaleInt(140, layoutScale),
                Width - resetLabel.Left - rightPadding);
        }
    }

    private sealed record ThemePalette(
        bool IsDark,
        Color Surface,
        Color Card,
        Color Border,
        Color CardBorder,
        Color TextPrimary,
        Color TextSecondary,
        Color Accent,
        Color MeterTrack,
        Color Hover,
        Color Pressed,
        Color Warning,
        Color Danger)
    {
        public static ThemePalette FromWindows()
        {
            var isLight = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1) as int? ?? 1;

            return isLight == 0 ? Dark() : Light();
        }

        private static ThemePalette Dark()
        {
            return new ThemePalette(
                true,
                Color.FromArgb(32, 32, 32),
                Color.FromArgb(43, 43, 43),
                Color.FromArgb(62, 62, 62),
                Color.FromArgb(70, 70, 70),
                Color.FromArgb(245, 245, 245),
                Color.FromArgb(199, 199, 199),
                Color.FromArgb(96, 205, 255),
                Color.FromArgb(62, 69, 74),
                Color.FromArgb(54, 54, 54),
                Color.FromArgb(68, 68, 68),
                Color.FromArgb(255, 185, 0),
                Color.FromArgb(255, 99, 71));
        }

        private static ThemePalette Light()
        {
            return new ThemePalette(
                false,
                Color.FromArgb(243, 243, 243),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(218, 220, 224),
                Color.FromArgb(226, 226, 226),
                Color.FromArgb(32, 31, 30),
                Color.FromArgb(96, 94, 92),
                Color.FromArgb(0, 95, 184),
                Color.FromArgb(233, 238, 243),
                Color.FromArgb(235, 235, 235),
                Color.FromArgb(225, 225, 225),
                Color.FromArgb(202, 80, 16),
                Color.FromArgb(196, 43, 28));
        }
    }

    private sealed class ProviderTabButton : Control
    {
        private const string ClaudeSymbolPathData =
            "m19.6 66.5 19.7-11 .3-1-.3-.5h-1l-3.3-.2-11.2-.3L14 53l-9.5-.5-2.4-.5L0 49l.2-1.5 2-1.3 2.9.2 6.3.5 9.5.6 6.9.4L38 49.1h1.6l.2-.7-.5-.4-.4-.4L29 41l-10.6-7-5.6-4.1-3-2-1.5-2-.6-4.2 2.7-3 3.7.3.9.2 3.7 2.9 8 6.1L37 36l1.5 1.2.6-.4.1-.3-.7-1.1L33 25l-6-10.4-2.7-4.3-.7-2.6c-.3-1-.4-2-.4-3l3-4.2L28 0l4.2.6L33.8 2l2.6 6 4.1 9.3L47 29.9l2 3.8 1 3.4.3 1h.7v-.5l.5-7.2 1-8.7 1-11.2.3-3.2 1.6-3.8 3-2L61 2.6l2 2.9-.3 1.8-1.1 7.7L59 27.1l-1.5 8.2h.9l1-1.1 4.1-5.4 6.9-8.6 3-3.5L77 13l2.3-1.8h4.3l3.1 4.7-1.4 4.9-4.4 5.6-3.7 4.7-5.3 7.1-3.2 5.7.3.4h.7l12-2.6 6.4-1.1 7.6-1.3 3.5 1.6.4 1.6-1.4 3.4-8.2 2-9.6 2-14.3 3.3-.2.1.2.3 6.4.6 2.8.2h6.8l12.6 1 3.3 2 1.9 2.7-.3 2-5.1 2.6-6.8-1.6-16-3.8-5.4-1.3h-.8v.4l4.6 4.5 8.3 7.5L89 80.1l.5 2.4-1.3 2-1.4-.2-9.2-7-3.6-3-8-6.8h-.5v.7l1.8 2.7 9.8 14.7.5 4.5-.7 1.4-2.6 1-2.7-.6-5.8-8-6-9-4.7-8.2-.5.4-2.9 30.2-1.3 1.5-3 1.2-2.5-2-1.4-3 1.4-6.2 1.6-8 1.3-6.4 1.2-7.9.7-2.6v-.2H49L43 72l-9 12.3-7.2 7.6-1.7.7-3-1.5.3-2.8L24 86l10-12.8 6-7.9 4-4.6-.1-.5h-.3L17.2 77.4l-4.7.6-2-2 .2-3 1-1 8-5.5Z";

        private static readonly string OpenAiWhiteLogoPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "OpenAICodexLogoWhite.png");

        private static readonly string OpenAiBlackLogoPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "OpenAICodexLogoBlack.png");

        private ThemePalette theme = ThemePalette.FromWindows();
        private bool hovering;
        private bool pressing;
        private bool selected;

        public ProviderTabButton(string text, string providerKey, bool isClaude)
        {
            Text = text;
            ProviderKey = providerKey;
            IsClaude = isClaude;
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.UserPaint,
                true);
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string ProviderKey { get; }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool IsClaude { get; }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value)
                {
                    return;
                }

                selected = value;
                Invalidate();
            }
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Surface;
            Invalidate();
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
            if (e.Button == MouseButtons.Left)
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
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var fill = selected
                ? theme.Accent
                : pressing
                    ? theme.Pressed
                    : hovering
                        ? theme.Hover
                        : theme.Surface;

            using var fillBrush = new SolidBrush(fill);
            using var borderPen = new Pen(selected ? theme.Accent : theme.CardBorder);
            using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 8);
            e.Graphics.FillPath(fillBrush, path);
            e.Graphics.DrawPath(borderPen, path);

            var iconSize = Math.Max(16, Math.Min(Width, Height) - Math.Max(8, Height / 3));
            var iconBounds = new Rectangle(Width / 2 - iconSize / 2, Height / 2 - iconSize / 2, iconSize, iconSize);
            if (IsClaude)
            {
                DrawClaudeLogo(e.Graphics, iconBounds);
                return;
            }

            DrawOpenAiLogo(e.Graphics, iconBounds, selected);

            if (!string.Equals(Text, "Codex", StringComparison.OrdinalIgnoreCase))
            {
                using var font = new Font(Font.FontFamily, Math.Max(6f, Font.Size - 2f), FontStyle.Bold);
                using var brush = new SolidBrush(selected || !theme.IsDark ? Color.Black : theme.TextPrimary);
                var label = Text.Length <= 2 ? Text : Text[^1..];
                e.Graphics.DrawString(label, font, brush, Width - 12, Height - 13);
            }
        }

        private void DrawOpenAiLogo(Graphics graphics, Rectangle bounds, bool isSelected)
        {
            var preferredPath = isSelected || !theme.IsDark ? OpenAiBlackLogoPath : OpenAiWhiteLogoPath;
            var fallbackPath = preferredPath == OpenAiBlackLogoPath ? OpenAiWhiteLogoPath : OpenAiBlackLogoPath;
            var path = File.Exists(preferredPath) ? preferredPath : fallbackPath;

            if (File.Exists(path))
            {
                using var image = Image.FromFile(path);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(image, bounds);
                return;
            }

            var fallbackColor = isSelected || !theme.IsDark ? Color.Black : Color.White;
            using var pen = new Pen(fallbackColor, 2.2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };
            graphics.DrawEllipse(pen, bounds);
            graphics.DrawLine(pen, bounds.Left + 4, bounds.Top + 13, bounds.Right - 3, bounds.Top + 5);
            graphics.DrawLine(pen, bounds.Left + 5, bounds.Top + 5, bounds.Right - 4, bounds.Bottom - 4);
        }

        private static void DrawClaudeLogo(Graphics graphics, Rectangle bounds)
        {
            using var brush = new SolidBrush(Color.FromArgb(217, 119, 87));
            try
            {
                using var path = CreateSvgPath(ClaudeSymbolPathData);
                using var transform = new System.Drawing.Drawing2D.Matrix(
                    bounds.Width / 100f,
                    0,
                    0,
                    bounds.Height / 100f,
                    bounds.Left,
                    bounds.Top);
                path.Transform(transform);
                graphics.FillPath(brush, path);
            }
            catch
            {
                graphics.FillEllipse(brush, bounds);
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath CreateSvgPath(string data)
        {
            var tokens = System.Text.RegularExpressions.Regex.Matches(
                    data,
                    @"[AaCcHhLlMmQqSsTtVvZz]|[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")
                .Select(match => match.Value)
                .ToArray();

            var path = new System.Drawing.Drawing2D.GraphicsPath();
            var index = 0;
            var command = '\0';
            var current = PointF.Empty;
            var start = PointF.Empty;

            while (index < tokens.Length)
            {
                if (IsCommand(tokens[index][0]))
                {
                    command = tokens[index++][0];
                }

                switch (command)
                {
                    case 'M':
                    case 'm':
                    {
                        var first = true;
                        while (HasNumberPair(tokens, index))
                        {
                            var point = ReadPoint(tokens, ref index, current, command == 'm');
                            if (first)
                            {
                                path.StartFigure();
                                current = point;
                                start = point;
                                first = false;
                            }
                            else
                            {
                                path.AddLine(current, point);
                                current = point;
                            }
                        }

                        command = command == 'm' ? 'l' : 'L';
                        break;
                    }
                    case 'L':
                    case 'l':
                        while (HasNumberPair(tokens, index))
                        {
                            var point = ReadPoint(tokens, ref index, current, command == 'l');
                            path.AddLine(current, point);
                            current = point;
                        }

                        break;
                    case 'H':
                    case 'h':
                        while (HasNumber(tokens, index))
                        {
                            var x = ReadNumber(tokens, ref index);
                            if (command == 'h')
                            {
                                x += current.X;
                            }

                            var point = new PointF(x, current.Y);
                            path.AddLine(current, point);
                            current = point;
                        }

                        break;
                    case 'V':
                    case 'v':
                        while (HasNumber(tokens, index))
                        {
                            var y = ReadNumber(tokens, ref index);
                            if (command == 'v')
                            {
                                y += current.Y;
                            }

                            var point = new PointF(current.X, y);
                            path.AddLine(current, point);
                            current = point;
                        }

                        break;
                    case 'C':
                    case 'c':
                        while (HasNumbers(tokens, index, 6))
                        {
                            var relative = command == 'c';
                            var control1 = ReadPoint(tokens, ref index, current, relative);
                            var control2 = ReadPoint(tokens, ref index, current, relative);
                            var end = ReadPoint(tokens, ref index, current, relative);
                            path.AddBezier(current, control1, control2, end);
                            current = end;
                        }

                        break;
                    case 'Z':
                    case 'z':
                        path.CloseFigure();
                        current = start;
                        break;
                    default:
                        index++;
                        break;
                }
            }

            return path;
        }

        private static PointF ReadPoint(string[] tokens, ref int index, PointF current, bool relative)
        {
            var x = ReadNumber(tokens, ref index);
            var y = ReadNumber(tokens, ref index);
            return relative ? new PointF(current.X + x, current.Y + y) : new PointF(x, y);
        }

        private static float ReadNumber(string[] tokens, ref int index)
        {
            return float.Parse(tokens[index++], System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool HasNumber(string[] tokens, int index)
        {
            return index < tokens.Length && !IsCommand(tokens[index][0]);
        }

        private static bool HasNumberPair(string[] tokens, int index)
        {
            return HasNumbers(tokens, index, 2);
        }

        private static bool HasNumbers(string[] tokens, int index, int count)
        {
            for (var offset = 0; offset < count; offset++)
            {
                if (!HasNumber(tokens, index + offset))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsCommand(char value)
        {
            return char.IsAsciiLetter(value);
        }
    }

    private sealed class CloseGlyphButton : Control
    {
        private ThemePalette theme = ThemePalette.FromWindows();
        private bool hovering;
        private bool pressing;

        public CloseGlyphButton()
        {
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Surface;
            Invalidate();
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
            pressing = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressing = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var fill = pressing ? theme.Pressed : hovering ? theme.Hover : theme.Surface;
            using var fillBrush = new SolidBrush(fill);
            using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 7);
            e.Graphics.FillPath(fillBrush, path);

            using var pen = new Pen(theme.TextSecondary, 1.4f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };

            var inset = Math.Max(8, Width / 3);
            e.Graphics.DrawLine(pen, inset, inset, Width - inset, Height - inset);
            e.Graphics.DrawLine(pen, Width - inset, inset, inset, Height - inset);
        }
    }

    private static class NativeMethods
    {
        public const int WmNclButtonDown = 0x00A1;
        public const int HtCaption = 0x0002;

        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwcpRound = 2;

        public static void ApplyWindowAttributes(IntPtr handle, bool isDark)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                return;
            }

            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(
                handle,
                DwmwaWindowCornerPreference,
                ref preference,
                sizeof(int));

            var darkMode = isDark ? 1 : 0;
            _ = DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkMode,
                ref darkMode,
                sizeof(int));
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int dwAttribute,
            ref int pvAttribute,
            int cbAttribute);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    }
}
