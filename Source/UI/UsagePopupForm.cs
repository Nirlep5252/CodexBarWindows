using Microsoft.Win32;

namespace CodexBarWindows;

public sealed class UsagePopupForm : Form
{
    private const int BaseWidth = 420;
    private const int BaseHeight = 304;
    private const int HistoryExpandedHeight = 660;
    private const int HistoryCollapsedHeight = 360;
    private readonly Label titleLabel;
    private readonly List<ProviderTabButton> tabButtons = [];
    private readonly List<ProviderDescriptor> providers = [];
    private readonly Dictionary<string, ProviderUsageLookupResult> usageByProvider = [];
    private readonly Dictionary<string, ProviderUsageInsightsLookupResult> historyByProvider = [];
    private readonly Label planLabel;
    private readonly Label statusLabel;
    private readonly UsageSection fiveHourSection;
    private readonly UsageSection weeklySection;
    private readonly ProviderHistorySection providerHistorySection;
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
        providerHistorySection = new ProviderHistorySection();
        providerHistorySection.ExpandedChanged += (_, _) =>
        {
            ApplyScaledLayout();
            RenderSelectedProvider();
        };

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
        Controls.Add(providerHistorySection);
        Controls.Add(statusLabel);
        Controls.Add(closeButton);

        EnableDragMove(this);
        EnableDragMove(titleLabel);
        EnableDragMove(planLabel);
        EnableDragMove(statusLabel);
        fiveHourSection.EnableDragMove();
        weeklySection.EnableDragMove();
        providerHistorySection.EnableDragMove();

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
        var baseHeight = providerHistorySection.IsExpanded ? HistoryExpandedHeight : HistoryCollapsedHeight;
        SuspendLayout();

        ClientSize = new Size(ScaleInt(BaseWidth, scale), ScaleInt(baseHeight, scale));

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
        providerHistorySection.Visible = true;
        var insightsHeight = providerHistorySection.IsExpanded ? 370 : 70;
        providerHistorySection.Bounds = ScaleRect(18, 274, 384, insightsHeight, scale);
        statusLabel.Bounds = ScaleRect(24, providerHistorySection.IsExpanded ? 642 : 344, 372, 16, scale);

        fiveHourSection.ApplyLayoutScale(scale);
        weeklySection.ApplyLayoutScale(scale);
        providerHistorySection.ApplyLayoutScale(scale);

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

    public void UpdateProviderHistory(string providerKey, ProviderUsageInsightsLookupResult result)
    {
        historyByProvider[providerKey] = result;

        if (providerKey == selectedProviderKey)
        {
            RenderSelectedProvider();
        }
    }

    public void SetProviderHistoryLoading(string providerKey)
    {
        if (providerKey == selectedProviderKey)
        {
            providerHistorySection.SetLoading();
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
        ApplyScaledLayout();
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
        providerHistorySection.Visible = true;
        titleLabel.Text = $"{provider.Name} rate limits";
        var result = GetProviderUsage(selectedProviderKey);

        if (result.Snapshot is not { } snapshot)
        {
            planLabel.Text = $"Waiting for local {provider.Name} usage data";
            fiveHourSection.SetUnavailable("5 hour limit");
            weeklySection.SetUnavailable("Weekly limit");
            RenderProviderHistory(provider);
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

        RenderProviderHistory(provider);
        statusLabel.Text = string.Empty;
    }

    private void RenderProviderHistory(ProviderDescriptor provider)
    {
        var result = GetProviderHistory(selectedProviderKey);
        if (result.Insights is { } insights)
        {
            providerHistorySection.SetInsights(insights, result.Error);
            return;
        }

        providerHistorySection.SetUnavailable(result.Error ?? "Usage history has not been loaded yet.");
    }

    private ProviderUsageLookupResult GetProviderUsage(string providerKey)
    {
        return usageByProvider.TryGetValue(providerKey, out var result)
            ? result
            : new ProviderUsageLookupResult(null, "Usage has not been loaded yet.");
    }

    private ProviderUsageInsightsLookupResult GetProviderHistory(string providerKey)
    {
        return historyByProvider.TryGetValue(providerKey, out var result)
            ? result
            : new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet.");
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
            historyByProvider.TryAdd(
                provider.Key,
                new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet."));

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
        providerHistorySection.ApplyTheme(theme);

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

    private sealed class ProviderHistorySection : Panel
    {
        private readonly Label titleLabel;
        private readonly ChevronToggleButton toggleLabel;
        private readonly Label subtitleLabel;
        private readonly Label todayLabel;
        private readonly Label monthLabel;
        private readonly HistoryMetricLabel todayMetric;
        private readonly HistoryMetricLabel monthMetric;
        private readonly DailyHistoryChart dailyChart;
        private readonly ModelBreakdownChart modelChart;
        private readonly System.Windows.Forms.Timer loadingTimer = new();
        private ThemePalette theme = ThemePalette.FromWindows();
        private float layoutScale = 1f;
        private float loadingPhase;
        private bool isExpanded = true;
        private bool isLoading;

        public event EventHandler? ExpandedChanged;

        public ProviderHistorySection()
        {
            BackColor = theme.Card;
            DoubleBuffered = true;
            Padding = new Padding(14, 12, 14, 12);

            titleLabel = new Label
            {
                AutoSize = false,
                Cursor = Cursors.Hand,
                Font = CreateFont("Segoe UI Variable Text", 9.5f, FontStyle.Bold),
                Text = "Usage history"
            };
            titleLabel.Click += (_, _) => ToggleExpanded();

            toggleLabel = new ChevronToggleButton
            {
                Cursor = Cursors.Hand,
                Expanded = true
            };
            toggleLabel.Click += (_, _) => ToggleExpanded();

            subtitleLabel = new Label
            {
                AutoEllipsis = true,
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 8.25f, FontStyle.Regular),
                Text = "Local session estimates"
            };

            todayLabel = MetricLabel();
            monthLabel = MetricLabel();
            todayMetric = new HistoryMetricLabel();
            monthMetric = new HistoryMetricLabel();
            dailyChart = new DailyHistoryChart();
            modelChart = new ModelBreakdownChart();

            Controls.Add(titleLabel);
            Controls.Add(toggleLabel);
            Controls.Add(subtitleLabel);
            Controls.Add(todayLabel);
            Controls.Add(monthLabel);
            Controls.Add(todayMetric);
            Controls.Add(monthMetric);
            Controls.Add(dailyChart);
            Controls.Add(modelChart);

            loadingTimer.Interval = 70;
            loadingTimer.Tick += (_, _) => AdvanceLoadingAnimation();

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
                        createdPopup.EnableDragMove(subtitleLabel);
                        createdPopup.EnableDragMove(todayLabel);
                        createdPopup.EnableDragMove(monthLabel);
                        createdPopup.EnableDragMove(todayMetric);
                        createdPopup.EnableDragMove(monthMetric);
                        createdPopup.EnableDragMove(dailyChart);
                        createdPopup.EnableDragMove(modelChart);
                    }
                };
                return;
            }

            popup.EnableDragMove(this);
            popup.EnableDragMove(subtitleLabel);
            popup.EnableDragMove(todayLabel);
            popup.EnableDragMove(monthLabel);
            popup.EnableDragMove(todayMetric);
            popup.EnableDragMove(monthMetric);
            popup.EnableDragMove(dailyChart);
            popup.EnableDragMove(modelChart);
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Card;
            foreach (Control control in Controls)
            {
                control.BackColor = theme.Card;
            }

            titleLabel.ForeColor = theme.TextPrimary;
            toggleLabel.ApplyTheme(theme);
            subtitleLabel.ForeColor = theme.TextSecondary;
            todayLabel.ForeColor = theme.TextPrimary;
            monthLabel.ForeColor = theme.TextPrimary;
            todayMetric.ApplyTheme(theme);
            monthMetric.ApplyTheme(theme);
            dailyChart.ApplyTheme(theme);
            modelChart.ApplyTheme(theme);
            Invalidate(true);
        }

        public bool IsExpanded
        {
            get => isExpanded;
            private set
            {
                if (isExpanded == value)
                {
                    return;
                }

                isExpanded = value;
                ApplyExpandedState();
                ExpandedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void ApplyLayoutScale(float scale)
        {
            layoutScale = Math.Max(1f, scale);
            dailyChart.LayoutScale = layoutScale;
            modelChart.LayoutScale = layoutScale;
            UpdateChildLayout();
        }

        private void ToggleExpanded()
        {
            IsExpanded = !IsExpanded;
        }

        private void ApplyExpandedState()
        {
            toggleLabel.Expanded = IsExpanded;
            var showDetails = IsExpanded;
            todayLabel.Visible = showDetails;
            monthLabel.Visible = showDetails;
            todayMetric.Visible = showDetails;
            monthMetric.Visible = showDetails;
            dailyChart.Visible = showDetails;
            modelChart.Visible = showDetails;
            subtitleLabel.Text = showDetails && !string.IsNullOrWhiteSpace(expandedSubtitle)
                ? expandedSubtitle
                : showDetails ? subtitleLabel.Text : "History hidden. Click to expand.";
            UpdateChildLayout();
            Invalidate(true);
        }

        private string? expandedSubtitle;

        private void AdvanceLoadingAnimation()
        {
            if (!isLoading)
            {
                loadingTimer.Stop();
                return;
            }

            loadingPhase += 0.035f;
            if (loadingPhase > 1f)
            {
                loadingPhase -= 1f;
            }

            todayMetric.LoadingPhase = loadingPhase;
            monthMetric.LoadingPhase = WrapPhase(loadingPhase + 0.18f);
            dailyChart.LoadingPhase = loadingPhase;
            modelChart.LoadingPhase = WrapPhase(loadingPhase + 0.28f);
        }

        private void StartLoadingAnimation()
        {
            isLoading = true;
            if (!loadingTimer.Enabled)
            {
                loadingTimer.Start();
            }
        }

        private void StopLoadingAnimation()
        {
            isLoading = false;
            loadingTimer.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                loadingTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        public void SetLoading()
        {
            StartLoadingAnimation();
            titleLabel.Text = "Usage history";
            expandedSubtitle = "Scanning local sessions...";
            subtitleLabel.Text = IsExpanded ? expandedSubtitle : "History hidden. Click to expand.";
            todayLabel.Text = "Today";
            monthLabel.Text = "30 days";
            todayMetric.SetLoading();
            monthMetric.SetLoading();
            dailyChart.SetLoading();
            modelChart.SetLoading();
        }

        public void SetUnavailable(string message)
        {
            StopLoadingAnimation();
            titleLabel.Text = "Usage history";
            expandedSubtitle = message;
            subtitleLabel.Text = IsExpanded ? expandedSubtitle : "History hidden. Click to expand.";
            todayLabel.Text = "Today";
            monthLabel.Text = "30 days";
            todayMetric.SetText("--");
            monthMetric.SetText("--");
            dailyChart.SetData([], "No history yet");
            modelChart.SetData([], "No model breakdown yet");
        }

        public void SetInsights(ProviderUsageInsights insights, string? warning)
        {
            StopLoadingAnimation();
            titleLabel.Text = "Usage history";
            var source = string.IsNullOrWhiteSpace(insights.Source) ? "Local estimates" : insights.Source;
            expandedSubtitle = warning is null
                ? $"{source}, updated {FormatObservedAt(insights.ObservedAt)}"
                : $"{warning} · {source}";
            subtitleLabel.Text = IsExpanded ? expandedSubtitle : "History hidden. Click to expand.";
            todayLabel.Text = "Today";
            monthLabel.Text = "30 days";
            todayMetric.SetText(FormatMetric(insights.TodayEstimatedCostUsd, insights.TodayFastEstimatedCostUsd, insights.TodayTokens));
            monthMetric.SetText(FormatMetric(insights.Last30DaysEstimatedCostUsd, insights.Last30DaysFastEstimatedCostUsd, insights.Last30DaysTokens));
            dailyChart.SetData(insights.Daily, insights.HasUsage ? null : "No token rows found");
            modelChart.SetData(insights.Models, insights.HasUsage ? null : "No model data found");
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
            var left = ScaleInt(16, layoutScale);
            var right = ScaleInt(16, layoutScale);
            titleLabel.Bounds = new Rectangle(left, ScaleInt(12, layoutScale), ScaleInt(170, layoutScale), ScaleInt(22, layoutScale));
            toggleLabel.Bounds = new Rectangle(Width - right - ScaleInt(24, layoutScale), ScaleInt(10, layoutScale), ScaleInt(24, layoutScale), ScaleInt(24, layoutScale));
            subtitleLabel.Bounds = new Rectangle(left, ScaleInt(34, layoutScale), Width - left - right, ScaleInt(20, layoutScale));
            if (!IsExpanded)
            {
                return;
            }

            todayLabel.Bounds = new Rectangle(left, ScaleInt(66, layoutScale), ScaleInt(168, layoutScale), ScaleInt(18, layoutScale));
            monthLabel.Bounds = new Rectangle(Width - ScaleInt(184, layoutScale), ScaleInt(66, layoutScale), ScaleInt(168, layoutScale), ScaleInt(18, layoutScale));
            todayMetric.Bounds = new Rectangle(left, ScaleInt(84, layoutScale), ScaleInt(168, layoutScale), ScaleInt(34, layoutScale));
            monthMetric.Bounds = new Rectangle(Width - ScaleInt(184, layoutScale), ScaleInt(84, layoutScale), ScaleInt(168, layoutScale), ScaleInt(34, layoutScale));
            dailyChart.Bounds = new Rectangle(left, ScaleInt(124, layoutScale), Width - left - right, ScaleInt(138, layoutScale));
            modelChart.Bounds = new Rectangle(left, ScaleInt(278, layoutScale), Width - left - right, ScaleInt(78, layoutScale));
        }

        private static Label MetricLabel()
        {
            return new Label
            {
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 8.5f, FontStyle.Bold)
            };
        }

        private static string FormatMetric(decimal totalCost, decimal fastCost, long tokens)
        {
            var text = $"{FormatCurrency(totalCost)} · {FormatTokens(tokens)}";
            return fastCost > 0 ? $"{text}\nfast {FormatCurrency(fastCost)}" : text;
        }

        private static string FormatCurrency(decimal value)
        {
            if (value <= 0)
            {
                return "$0.00";
            }

            return value < 0.01m ? "<$0.01" : $"${value:0.00}";
        }

        public static string FormatTokens(long tokens)
        {
            if (tokens >= 1_000_000_000)
            {
                return $"{tokens / 1_000_000_000d:0.##}B tok";
            }

            if (tokens >= 1_000_000)
            {
                return $"{tokens / 1_000_000d:0.#}M tok";
            }

            if (tokens >= 1_000)
            {
                return $"{tokens / 1_000d:0.#}K tok";
            }

            return $"{tokens} tok";
        }
    }

    private sealed class HistoryMetricLabel : Control
    {
        private string text = "--";
        private bool loading;
        private float loadingPhase;
        private ThemePalette theme = ThemePalette.FromWindows();

        public HistoryMetricLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float LoadingPhase
        {
            get => loadingPhase;
            set
            {
                loadingPhase = WrapPhase(value);
                if (loading)
                {
                    Invalidate();
                }
            }
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Card;
            Invalidate();
        }

        public void SetLoading()
        {
            loading = true;
            text = string.Empty;
            Invalidate();
        }

        public void SetText(string value)
        {
            loading = false;
            text = value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (loading)
            {
                DrawSkeletonPill(e.Graphics, ClientRectangle, theme, loadingPhase);
                return;
            }

            using var brush = new SolidBrush(theme.TextPrimary);
            using var font = CreateFont("Segoe UI Variable Text", 8.25f, FontStyle.Bold);
            using var format = new StringFormat(StringFormatFlags.NoWrap)
            {
                Trimming = StringTrimming.None,
                LineAlignment = StringAlignment.Near,
                Alignment = StringAlignment.Near
            };
            e.Graphics.DrawString(text, font, brush, ClientRectangle, format);
        }
    }

    private sealed class ChevronToggleButton : Control
    {
        private ThemePalette theme = ThemePalette.FromWindows();
        private bool hovering;
        private bool pressing;
        private bool expanded = true;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Expanded
        {
            get => expanded;
            set
            {
                if (expanded == value)
                {
                    return;
                }

                expanded = value;
                AccessibleName = expanded ? "Collapse Usage history" : "Expand Usage history";
                Invalidate();
            }
        }

        public ChevronToggleButton()
        {
            AccessibleName = "Collapse Usage history";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.UserPaint, true);
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Card;
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
            var fill = pressing ? theme.Pressed : hovering ? theme.Hover : theme.Card;
            using (var fillBrush = new SolidBrush(fill))
            using (var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 7))
            {
                e.Graphics.FillPath(fillBrush, path);
            }

            using var pen = new Pen(theme.TextSecondary, 1.7f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };

            var cx = Width / 2f;
            var cy = Height / 2f;
            var size = Math.Max(5f, Math.Min(Width, Height) * 0.22f);
            if (Expanded)
            {
                e.Graphics.DrawLine(pen, cx - size, cy - size / 2f, cx, cy + size / 2f);
                e.Graphics.DrawLine(pen, cx, cy + size / 2f, cx + size, cy - size / 2f);
            }
            else
            {
                e.Graphics.DrawLine(pen, cx - size / 2f, cy - size, cx + size / 2f, cy);
                e.Graphics.DrawLine(pen, cx + size / 2f, cy, cx - size / 2f, cy + size);
            }
        }
    }

    private sealed class DailyHistoryChart : Control
    {
        private readonly ToolTip toolTip = new() { AutomaticDelay = 120, AutoPopDelay = 8000, ReshowDelay = 80 };
        private IReadOnlyList<ProviderDailyUsage> daily = [];
        private string? emptyMessage;
        private ThemePalette theme = ThemePalette.FromWindows();
        private bool loading;
        private float loadingPhase;
        private int hoveredIndex = -1;
        private string? lastToolTipText;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float LayoutScale { get; set; } = 1f;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float LoadingPhase
        {
            get => loadingPhase;
            set
            {
                loadingPhase = WrapPhase(value);
                if (loading)
                {
                    Invalidate();
                }
            }
        }

        public DailyHistoryChart()
        {
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Card;
            Invalidate();
        }

        public void SetData(IReadOnlyList<ProviderDailyUsage> data, string? message)
        {
            loading = false;
            daily = data;
            emptyMessage = message;
            hoveredIndex = -1;
            lastToolTipText = null;
            toolTip.SetToolTip(this, null);
            Invalidate();
        }

        public void SetLoading()
        {
            loading = true;
            daily = [];
            emptyMessage = null;
            hoveredIndex = -1;
            lastToolTipText = null;
            toolTip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var index = HitTest(e.Location);
            if (index == hoveredIndex)
            {
                return;
            }

            hoveredIndex = index;
            if (index >= 0 && index < daily.Count)
            {
                var day = daily[index];
                var categoryLines = DailySpendCategories(day).Count == 0
                    ? string.Empty
                    : "\n" + string.Join("\n", DailySpendCategories(day)
                        .OrderByDescending(category => category.EstimatedCostUsd)
                        .Take(4)
                        .Select(category => $"{ShortSpendLabel(category.Label)} {FormatUsd(category.EstimatedCostUsd)}"));
                var cacheCreateLine = day.CacheCreationTokens > 0 ? $", {ProviderHistorySection.FormatTokens(day.CacheCreationTokens)} cache create" : string.Empty;
                var text = $"{day.Day:MMM d}: {FormatUsd(day.EstimatedCostUsd)}{categoryLines}\n{ProviderHistorySection.FormatTokens(day.TotalTokens)} total, {ProviderHistorySection.FormatTokens(day.OutputTokens)} output{cacheCreateLine}";
                if (text != lastToolTipText)
                {
                    lastToolTipText = text;
                    toolTip.SetToolTip(this, text);
                }
            }
            else
            {
                lastToolTipText = null;
                toolTip.SetToolTip(this, null);
            }

            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoveredIndex = -1;
            lastToolTipText = null;
            toolTip.SetToolTip(this, null);
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var titleBounds = new Rectangle(0, 0, Width, ScaleInt(16, LayoutScale));
            using var titleBrush = new SolidBrush(theme.TextSecondary);
            using var titleFont = CreateFont("Segoe UI Variable Text", 7.5f, FontStyle.Regular);
            e.Graphics.DrawString("Estimated spend by day", titleFont, titleBrush, titleBounds);
            DrawSpendCategoryLegend(e.Graphics, new Rectangle(Width - ScaleInt(220, LayoutScale), 0, ScaleInt(220, LayoutScale), ScaleInt(16, LayoutScale)), TopSpendCategories(daily), theme, LayoutScale);

            var chartBounds = ChartBounds;
            if (loading)
            {
                DrawDailyLoading(e.Graphics, chartBounds, theme, LayoutScale, loadingPhase);
                return;
            }

            if (daily.Count == 0 || daily.All(day => day.EstimatedCostUsd <= 0))
            {
                DrawEmpty(e.Graphics, chartBounds, emptyMessage ?? "No spend data");
                return;
            }

            var max = Math.Max(0.01m, daily.Max(day => day.EstimatedCostUsd));
            var gap = BarGap;
            var barWidth = BarWidth(chartBounds, gap);
            var x = chartBounds.Left;
            using var trackBrush = new SolidBrush(theme.MeterTrack);
            using var hoverPen = new Pen(theme.TextPrimary, 1f);
            for (var index = 0; index < daily.Count; index++)
            {
                var day = daily[index];
                var track = new Rectangle(x, chartBounds.Top, barWidth, chartBounds.Height);
                e.Graphics.FillRectangle(trackBrush, track);
                var totalHeight = (int)Math.Round(chartBounds.Height * (double)(day.EstimatedCostUsd / max));
                var categories = DailySpendCategories(day).Where(category => category.EstimatedCostUsd > 0).ToArray();
                if (totalHeight > 0 && categories.Length > 0)
                {
                    var paintedHeight = 0;
                    for (var categoryIndex = 0; categoryIndex < categories.Length; categoryIndex++)
                    {
                        var category = categories[categoryIndex];
                        var height = categoryIndex == categories.Length - 1
                            ? totalHeight - paintedHeight
                            : (int)Math.Round(totalHeight * (double)(category.EstimatedCostUsd / day.EstimatedCostUsd));
                        height = Math.Clamp(height, 0, totalHeight - paintedHeight);
                        if (height <= 0)
                        {
                            continue;
                        }

                        var color = SpendCategoryColor(category.Label, theme);
                        using var brush = new SolidBrush(index == hoveredIndex ? ControlPaint.Light(color) : color);
                        var fill = new Rectangle(x, chartBounds.Bottom - paintedHeight - height, barWidth, height);
                        e.Graphics.FillRectangle(brush, fill);
                        paintedHeight += height;
                    }
                }

                if (index == hoveredIndex)
                {
                    e.Graphics.DrawRectangle(hoverPen, track);
                }

                x += barWidth + gap;
            }

            DrawDailyAxis(e.Graphics, chartBounds, daily);
        }

        private Rectangle ChartBounds => new(0, ScaleInt(18, LayoutScale), Width, Math.Max(20, Height - ScaleInt(36, LayoutScale)));
        private int BarGap => Math.Max(1, ScaleInt(2, LayoutScale));

        private int BarWidth(Rectangle chartBounds, int gap)
        {
            return Math.Max(2, (chartBounds.Width - gap * Math.Max(0, daily.Count - 1)) / Math.Max(1, daily.Count));
        }

        private int HitTest(Point point)
        {
            if (daily.Count == 0 || !ChartBounds.Contains(point))
            {
                return -1;
            }

            var gap = BarGap;
            var step = BarWidth(ChartBounds, gap) + gap;
            var index = step <= 0 ? -1 : (point.X - ChartBounds.Left) / step;
            return index >= 0 && index < daily.Count ? index : -1;
        }
    }

    private sealed class ModelBreakdownChart : Control
    {
        private readonly ToolTip toolTip = new() { AutomaticDelay = 120, AutoPopDelay = 8000, ReshowDelay = 80 };
        private IReadOnlyList<ProviderModelUsage> models = [];
        private string? emptyMessage;
        private ThemePalette theme = ThemePalette.FromWindows();
        private bool loading;
        private float loadingPhase;
        private int hoveredIndex = -1;
        private string? lastToolTipText;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float LayoutScale { get; set; } = 1f;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float LoadingPhase
        {
            get => loadingPhase;
            set
            {
                loadingPhase = WrapPhase(value);
                if (loading)
                {
                    Invalidate();
                }
            }
        }

        public ModelBreakdownChart()
        {
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Card;
            Invalidate();
        }

        public void SetData(IReadOnlyList<ProviderModelUsage> data, string? message)
        {
            loading = false;
            models = data;
            emptyMessage = message;
            hoveredIndex = -1;
            lastToolTipText = null;
            toolTip.SetToolTip(this, null);
            Invalidate();
        }

        public void SetLoading()
        {
            loading = true;
            models = [];
            emptyMessage = null;
            hoveredIndex = -1;
            lastToolTipText = null;
            toolTip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var index = HitTest(e.Location);
            if (index == hoveredIndex)
            {
                return;
            }

            hoveredIndex = index;
            var top = TopModels;
            if (index >= 0 && index < top.Length)
            {
                var model = top[index];
                var fastLine = model.FastEstimatedCostUsd > 0 ? $"\nFast {FormatUsd(model.FastEstimatedCostUsd)}, regular {FormatUsd(model.RegularEstimatedCostUsd)}" : string.Empty;
                var cacheCreateLine = model.CacheCreationTokens > 0 ? $", {ProviderHistorySection.FormatTokens(model.CacheCreationTokens)} cache create" : string.Empty;
                var text = $"{model.Model}\nEstimated {FormatUsd(model.EstimatedCostUsd)}{fastLine}\n{ProviderHistorySection.FormatTokens(model.TotalTokens)} total, {ProviderHistorySection.FormatTokens(model.OutputTokens)} output{cacheCreateLine}";
                if (text != lastToolTipText)
                {
                    lastToolTipText = text;
                    toolTip.SetToolTip(this, text);
                }
            }
            else
            {
                lastToolTipText = null;
                toolTip.SetToolTip(this, null);
            }

            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoveredIndex = -1;
            lastToolTipText = null;
            toolTip.SetToolTip(this, null);
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var titleBounds = new Rectangle(0, 0, Width, ScaleInt(16, LayoutScale));
            using var titleBrush = new SolidBrush(theme.TextSecondary);
            using var titleFont = CreateFont("Segoe UI Variable Text", 7.5f, FontStyle.Regular);
            e.Graphics.DrawString("Estimated spend by model", titleFont, titleBrush, titleBounds);

            var chartBounds = BarBounds;
            if (loading)
            {
                DrawModelLoading(e.Graphics, chartBounds, Width, theme, LayoutScale, loadingPhase);
                return;
            }

            if (models.Count == 0 || models.All(model => model.EstimatedCostUsd <= 0))
            {
                DrawEmpty(e.Graphics, new Rectangle(0, ScaleInt(18, LayoutScale), Width, Height - ScaleInt(18, LayoutScale)), emptyMessage ?? "No model data");
                return;
            }

            var top = TopModels;
            var total = Math.Max(0.01m, top.Sum(model => model.EstimatedCostUsd));
            var x = chartBounds.Left;
            for (var index = 0; index < top.Length; index++)
            {
                var width = index == top.Length - 1
                    ? chartBounds.Right - x
                    : Math.Max(1, (int)Math.Round(chartBounds.Width * (double)(top[index].EstimatedCostUsd / total)));
                var segment = new Rectangle(x, chartBounds.Top, width, chartBounds.Height);
                var color = SegmentColor(top[index]);
                using var brush = new SolidBrush(index == hoveredIndex ? ControlPaint.Light(color) : color);
                e.Graphics.FillRectangle(brush, segment);
                if (index == hoveredIndex)
                {
                    using var pen = new Pen(theme.TextPrimary, 1f);
                    e.Graphics.DrawRectangle(pen, segment);
                }

                x += width;
            }

            DrawLegend(e.Graphics, top);
        }

        private ProviderModelUsage[] TopModels => models.Take(4).ToArray();

        private Rectangle BarBounds => new(0, ScaleInt(19, LayoutScale), Width, ScaleInt(10, LayoutScale));

        private Color SegmentColor(ProviderModelUsage model)
        {
            return SpendCategoryColor(model.Model, theme);
        }

        private int HitTest(Point point)
        {
            var top = TopModels;
            if (top.Length == 0 || !BarBounds.Contains(point))
            {
                return -1;
            }

            var total = Math.Max(0.01m, top.Sum(model => model.EstimatedCostUsd));
            var x = BarBounds.Left;
            for (var index = 0; index < top.Length; index++)
            {
                var width = index == top.Length - 1
                    ? BarBounds.Right - x
                    : Math.Max(1, (int)Math.Round(BarBounds.Width * (double)(top[index].EstimatedCostUsd / total)));
                if (point.X >= x && point.X <= x + width)
                {
                    return index;
                }

                x += width;
            }

            return -1;
        }

        private void DrawLegend(Graphics graphics, IReadOnlyList<ProviderModelUsage> top)
        {
            using var textBrush = new SolidBrush(theme.TextSecondary);
            using var font = CreateFont("Segoe UI Variable Text", 7.25f, FontStyle.Regular);
            var y = ScaleInt(36, LayoutScale);
            var columnWidth = Width / 2;
            for (var index = 0; index < Math.Min(4, top.Count); index++)
            {
                var x = (index % 2) * columnWidth;
                var rowY = y + (index / 2) * ScaleInt(16, LayoutScale);
                using var dotBrush = new SolidBrush(SegmentColor(top[index]));
                graphics.FillEllipse(dotBrush, x, rowY + ScaleInt(4, LayoutScale), ScaleInt(7, LayoutScale), ScaleInt(7, LayoutScale));
                var label = $"{ShortModel(top[index].Model)} {FormatUsd(top[index].EstimatedCostUsd)}";
                graphics.DrawString(label, font, textBrush, new RectangleF(x + ScaleInt(11, LayoutScale), rowY, columnWidth - ScaleInt(13, LayoutScale), ScaleInt(16, LayoutScale)));
            }
        }

        private static string ShortModel(string model)
        {
            var label = FriendlyModelLabel(model);
            if (label.Length <= 13)
            {
                return label;
            }

            return label[..12] + "…";
        }
    }

    private static void DrawDailyLoading(Graphics graphics, Rectangle chartBounds, ThemePalette theme, float scale, float phase)
    {
        var inset = ScaleInt(4, scale);
        var block = Rectangle.Inflate(chartBounds, -inset, -inset);
        using (var borderPen = new Pen(Color.FromArgb(theme.IsDark ? 42 : 70, theme.TextSecondary)))
        using (var path = RoundedPath(block, ScaleInt(10, scale)))
        {
            graphics.DrawPath(borderPen, path);
        }

        var top = block.Top + ScaleInt(18, scale);
        var left = block.Left + ScaleInt(18, scale);
        var width = block.Width - ScaleInt(36, scale);
        DrawSkeletonPill(graphics, new Rectangle(left, top, (int)(width * 0.72), ScaleInt(12, scale)), theme, phase);
        DrawSkeletonPill(graphics, new Rectangle(left, top + ScaleInt(28, scale), (int)(width * 0.88), ScaleInt(12, scale)), theme, WrapPhase(phase + 0.18f));
        DrawSkeletonPill(graphics, new Rectangle(left, top + ScaleInt(56, scale), (int)(width * 0.54), ScaleInt(12, scale)), theme, WrapPhase(phase + 0.36f));

        using var textBrush = new SolidBrush(theme.TextSecondary);
        using var font = CreateFont("Segoe UI Variable Text", 7f, FontStyle.Regular);
        graphics.DrawString("Scanning usage history...", font, textBrush, new RectangleF(chartBounds.Left, chartBounds.Bottom + 1, chartBounds.Width, ScaleInt(14, scale)));
    }

    private static void DrawModelLoading(Graphics graphics, Rectangle barBounds, int width, ThemePalette theme, float scale, float phase)
    {
        DrawSkeletonPill(graphics, barBounds, theme, phase);
        var y = barBounds.Bottom + ScaleInt(14, scale);
        var columnWidth = width / 2;
        DrawSkeletonPill(graphics, new Rectangle(0, y, Math.Max(40, columnWidth - ScaleInt(34, scale)), ScaleInt(9, scale)), theme, WrapPhase(phase + 0.2f));
        DrawSkeletonPill(graphics, new Rectangle(columnWidth, y, Math.Max(40, columnWidth - ScaleInt(34, scale)), ScaleInt(9, scale)), theme, WrapPhase(phase + 0.38f));
    }

    private static void DrawSkeletonPill(Graphics graphics, Rectangle bounds, ThemePalette theme, float phase)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var baseColor = theme.MeterTrack;
        var highlightColor = Color.FromArgb(
            theme.IsDark ? 82 : 165,
            Math.Min(255, theme.Accent.R + 24),
            Math.Min(255, theme.Accent.G + 24),
            Math.Min(255, theme.Accent.B + 24));
        using var brush = new SolidBrush(baseColor);
        using var highlight = new SolidBrush(highlightColor);
        using var path = RoundedPath(bounds, Math.Max(4, bounds.Height / 2));
        graphics.FillPath(brush, path);

        var state = graphics.Save();
        graphics.SetClip(path);
        var shineWidth = Math.Max(18, bounds.Width / 3);
        var travel = bounds.Width + (shineWidth * 2);
        var x = bounds.Left - shineWidth + (int)Math.Round(travel * WrapPhase(phase));
        graphics.FillRectangle(highlight, new Rectangle(x, bounds.Top, shineWidth, bounds.Height));
        graphics.Restore(state);
    }

    private static float WrapPhase(float phase)
    {
        phase %= 1f;
        return phase < 0 ? phase + 1f : phase;
    }

    private static void DrawDailyAxis(Graphics graphics, Rectangle chartBounds, IReadOnlyList<ProviderDailyUsage> daily)
    {
        if (daily.Count == 0)
        {
            return;
        }

        using var textBrush = new SolidBrush(Color.FromArgb(150, 128, 128, 128));
        using var font = CreateFont("Segoe UI Variable Text", 7f, FontStyle.Regular);
        var first = daily.First().Day.ToString("MMM d");
        var last = daily.Last().Day.ToString("MMM d");
        graphics.DrawString(first, font, textBrush, new RectangleF(chartBounds.Left, chartBounds.Bottom + 1, chartBounds.Width / 2f, 14));
        var lastSize = graphics.MeasureString(last, font);
        graphics.DrawString(last, font, textBrush, new PointF(chartBounds.Right - lastSize.Width, chartBounds.Bottom + 1));
    }

    private static void DrawSpendCategoryLegend(Graphics graphics, Rectangle bounds, IReadOnlyList<ProviderSpendCategory> categories, ThemePalette theme, float scale)
    {
        if (categories.Count == 0)
        {
            return;
        }

        using var textBrush = new SolidBrush(theme.TextSecondary);
        using var font = CreateFont("Segoe UI Variable Text", 7f, FontStyle.Regular);
        var dot = ScaleInt(6, scale);
        var y = bounds.Top + ScaleInt(5, scale);
        var x = bounds.Left + ScaleInt(4, scale);
        var gap = ScaleInt(10, scale);
        foreach (var category in categories.Take(3))
        {
            var label = ShortSpendLabel(category.Label);
            var textWidth = (int)Math.Ceiling(graphics.MeasureString(label, font).Width);
            var itemWidth = dot + ScaleInt(3, scale) + textWidth;
            if (x + itemWidth > bounds.Right)
            {
                break;
            }

            using var dotBrush = new SolidBrush(SpendCategoryColor(category.Label, theme));
            graphics.FillEllipse(dotBrush, x, y, dot, dot);
            graphics.DrawString(label, font, textBrush, x + dot + ScaleInt(3, scale), bounds.Top);
            x += itemWidth + gap;
        }
    }

    private static IReadOnlyList<ProviderSpendCategory> TopSpendCategories(IReadOnlyList<ProviderDailyUsage> daily)
    {
        return daily
            .SelectMany(DailySpendCategories)
            .GroupBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProviderSpendCategory(group.Key, group.Sum(category => category.EstimatedCostUsd)))
            .OrderByDescending(category => category.EstimatedCostUsd)
            .Take(3)
            .ToArray();
    }

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

    private static Color SpendCategoryColor(string label, ThemePalette theme)
    {
        var normalized = label.Replace(" fast", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (label.Contains("fast", StringComparison.OrdinalIgnoreCase))
        {
            return theme.Warning;
        }

        if (normalized.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase) || normalized.Contains("claude-opus", StringComparison.OrdinalIgnoreCase))
        {
            return theme.Accent;
        }

        if (normalized.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase) || normalized.Contains("claude-sonnet", StringComparison.OrdinalIgnoreCase) || normalized == "regular")
        {
            return Color.FromArgb(134, 97, 197);
        }

        if (normalized.Contains("gpt-5.3", StringComparison.OrdinalIgnoreCase) || normalized.Contains("claude-haiku", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(16, 124, 16);
        }

        if (normalized.Contains("gpt-5.2", StringComparison.OrdinalIgnoreCase))
        {
            return theme.Danger;
        }

        return ModelPalette[StableColorIndex(normalized, ModelPalette.Length)];
    }

    private static string ShortSpendLabel(string label)
    {
        var normalized = FriendlyModelLabel(label);
        return normalized.Length <= 14 ? normalized : normalized[..13] + "…";
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

    private static Color[] ModelPalette =>
    [
        Color.FromArgb(0, 95, 184),
        Color.FromArgb(16, 124, 16),
        Color.FromArgb(134, 97, 197),
        Color.FromArgb(196, 86, 9),
    ];

    private static int StableColorIndex(string value, int length)
    {
        var hash = 17;
        foreach (var character in value)
        {
            hash = unchecked((hash * 31) + character);
        }

        return (hash & int.MaxValue) % Math.Max(1, length);
    }

    private static string FormatUsd(decimal value)
    {
        if (value <= 0)
        {
            return "$0.00";
        }

        return value < 0.01m ? "<$0.01" : $"${value:0.00}";
    }

    private static void DrawEmpty(Graphics graphics, Rectangle bounds, string message)
    {
        using var pen = new Pen(Color.FromArgb(90, 128, 128, 128));
        using var brush = new SolidBrush(Color.FromArgb(150, 128, 128, 128));
        graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        using var font = CreateFont("Segoe UI Variable Text", 7.5f, FontStyle.Regular);
        graphics.DrawString(message, font, brush, bounds);
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
