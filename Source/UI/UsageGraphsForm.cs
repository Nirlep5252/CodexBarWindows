// This UI is constructed in code only (no WinForms designer), so designer
// code-serialization metadata for control properties is irrelevant.
#pragma warning disable WFO1000

namespace CodexBarWindows;

/// <summary>
/// Dedicated "Usage graphs" window (tray menu / popup header button): a resizable Fluent
/// window with a Mica title bar showing the local usage history for one provider at a time —
/// Today / last-30-days summary cards, a daily spend bar chart with a labelled axis and
/// gridlines, and a per-model spend breakdown. The flyout popup itself stays limits-only.
/// </summary>
public sealed class UsageGraphsForm : Form
{
    // Base (96-dpi) layout metrics, scaled manually via ScaleInt (AutoScaleMode.None).
    private const int BaseClientWidth = 680;
    private const int BaseClientHeight = 640;
    private const int MinClientWidth = 560;
    private const int MinClientHeight = 540;
    private const int OuterPad = 24;
    private const int CardGap = 12;
    private const int LoadingTimerIntervalMs = 50;
    private const int LoadingPulsePeriodMs = 1100;

    private readonly Label titleLabel;
    private readonly Label subtitleLabel;
    private readonly FluentComboBox providerCombo;
    private readonly MetricCard todayCard;
    private readonly MetricCard monthCard;
    private readonly DailySpendChart dailyChart;
    private readonly ModelSpendChart modelChart;
    private readonly System.Windows.Forms.Timer loadingTimer = new();
    private readonly List<string> providerKeys = [];
    private readonly Dictionary<string, ProviderUsageInsightsLookupResult> historyByProvider = [];
    private readonly HashSet<string> loadingProviders = [];
    private readonly List<Font> ownedFonts = [];
    private UiSettings uiSettings;
    private FluentTokens tokens;
    private string selectedProviderKey = UsagePopupForm.CodexProviderKey("default");
    private float loadingPhase;
    private bool suppressComboEvents;

    public event EventHandler<string>? SelectedProviderChanged;

    public UsageGraphsForm()
    {
        uiSettings = UiSettings.Load();
        FluentTheme.RefreshAccent();
        tokens = FluentTheme.Get(uiSettings.ResolveIsDark(), onBackdrop: false);

        AutoScaleMode = AutoScaleMode.None;
        BackColor = tokens.Background;
        ClientSize = new Size(BaseClientWidth, BaseClientHeight);
        DoubleBuffered = true;
        Font = OwnFont(FluentTheme.CaptionFont(1f));
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Usage graphs";

        titleLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Font = OwnFont(FluentTheme.SubtitleFont(1f)),
            Text = "Usage graphs",
            UseCompatibleTextRendering = true
        };

        subtitleLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Font = OwnFont(FluentTheme.CaptionFont(1f)),
            Text = "Local session estimates",
            UseCompatibleTextRendering = true
        };

        providerCombo = new FluentComboBox(tokens)
        {
            Font = OwnFont(FluentTheme.BodyFont(1f))
        };
        providerCombo.SelectedIndexChanged += (_, _) => OnProviderComboChanged();

        todayCard = new MetricCard("Today");
        monthCard = new MetricCard("Last 30 days");
        dailyChart = new DailySpendChart();
        modelChart = new ModelSpendChart();

        Controls.Add(titleLabel);
        Controls.Add(subtitleLabel);
        Controls.Add(providerCombo);
        Controls.Add(todayCard);
        Controls.Add(monthCard);
        Controls.Add(dailyChart);
        Controls.Add(modelChart);

        loadingTimer.Interval = LoadingTimerIntervalMs;
        loadingTimer.Tick += (_, _) => AdvanceLoadingAnimation();

        UiSettings.Changed += OnUiSettingsChanged;

        ConfigureCodexEntries([]);
        ApplyTheme();
        LayoutContent();
        RenderSelected();
    }

    public string SelectedProvider => selectedProviderKey;

    /// <summary>Rebuilds the provider picker: one entry per Codex CLI account plus Claude.</summary>
    public void ConfigureCodexEntries(IReadOnlyList<CodexCliEntry> codexEntries)
    {
        var previousKey = selectedProviderKey;

        providerKeys.Clear();
        var names = new List<string>();
        foreach (var entry in codexEntries)
        {
            providerKeys.Add(UsagePopupForm.CodexProviderKey(entry.Id));
            names.Add(entry.Name);
        }

        if (providerKeys.Count == 0)
        {
            providerKeys.Add(UsagePopupForm.CodexProviderKey("default"));
            names.Add("Codex");
        }

        providerKeys.Add(UsagePopupForm.ClaudeProviderKey);
        names.Add("Claude");

        suppressComboEvents = true;
        providerCombo.Items.Clear();
        providerCombo.Items.AddRange([.. names]);
        var restoredIndex = providerKeys.IndexOf(previousKey);
        providerCombo.SelectedIndex = restoredIndex >= 0 ? restoredIndex : 0;
        suppressComboEvents = false;

        selectedProviderKey = providerKeys[Math.Max(0, providerCombo.SelectedIndex)];
        RenderSelected();
    }

    public void UpdateHistory(string providerKey, ProviderUsageInsightsLookupResult result)
    {
        historyByProvider[providerKey] = result;

        // The lookup finished — clear the loading flag even on an error result,
        // otherwise an error would leave the skeleton pulsing forever.
        loadingProviders.Remove(providerKey);

        if (providerKey == selectedProviderKey)
        {
            RenderSelected();
        }
    }

    public void SetLoading(string providerKey)
    {
        loadingProviders.Add(providerKey);
        if (providerKey != selectedProviderKey)
        {
            return;
        }

        if (GetHistory(providerKey).HasInsights)
        {
            // Keep the rendered data; just hint that a refresh is in flight.
            subtitleLabel.Text = "Refreshing usage history...";
            return;
        }

        RenderSelected();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        WindowEffects.SetImmersiveDarkMode(Handle, tokens.IsDark);
        WindowEffects.TryApplyBackdrop(Handle, SystemBackdrop.Mica);

        var scale = DpiScale;
        MinimumSize = new Size(ScaleInt(MinClientWidth, scale), ScaleInt(MinClientHeight, scale));
        ClientSize = new Size(ScaleInt(BaseClientWidth, scale), ScaleInt(BaseClientHeight, scale));
        LayoutContent();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutContent();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            loadingTimer.Stop();
        }
        else if (loadingProviders.Contains(selectedProviderKey) && !GetHistory(selectedProviderKey).HasInsights)
        {
            loadingTimer.Start();
        }
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    protected override void WndProc(ref Message m)
    {
        const int wmSettingChange = 0x001A;
        const int wmDpiChanged = 0x02E0;
        const int wmThemeChanged = 0x031A;
        const int wmDwmColorizationColorChanged = 0x0320;

        base.WndProc(ref m);

        if (m.Msg == wmDpiChanged)
        {
            var scale = DpiScale;
            MinimumSize = new Size(ScaleInt(MinClientWidth, scale), ScaleInt(MinClientHeight, scale));
            LayoutContent();
        }
        else if (m.Msg is wmSettingChange or wmThemeChanged or wmDwmColorizationColorChanged)
        {
            RefreshTheme();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UiSettings.Changed -= OnUiSettingsChanged;
        }

        base.Dispose(disposing);
        if (disposing)
        {
            loadingTimer.Dispose();
            foreach (var font in ownedFonts)
            {
                font.Dispose();
            }

            ownedFonts.Clear();
        }
    }

    private void OnUiSettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnUiSettingsChanged(sender, e)));
            return;
        }

        uiSettings = UiSettings.Load();
        RefreshTheme(force: true);
    }

    private void OnProviderComboChanged()
    {
        if (suppressComboEvents)
        {
            return;
        }

        var index = providerCombo.SelectedIndex;
        if (index < 0 || index >= providerKeys.Count)
        {
            return;
        }

        selectedProviderKey = providerKeys[index];
        RenderSelected();
        SelectedProviderChanged?.Invoke(this, selectedProviderKey);
    }

    private void RenderSelected()
    {
        var result = GetHistory(selectedProviderKey);
        if (result.Insights is { } insights)
        {
            StopLoadingAnimation();
            var source = string.IsNullOrWhiteSpace(insights.Source) ? "Local estimates" : insights.Source;
            subtitleLabel.Text = result.Error is null
                ? $"{source} · updated {FormatObservedAt(insights.ObservedAt)}"
                : $"{result.Error} · {source}";

            todayCard.SetValue(
                FormatUsd(insights.TodayEstimatedCostUsd),
                MetricDetail(insights.TodayTokens, insights.TodayFastEstimatedCostUsd));
            monthCard.SetValue(
                FormatUsd(insights.Last30DaysEstimatedCostUsd),
                MetricDetail(insights.Last30DaysTokens, insights.Last30DaysFastEstimatedCostUsd));
            dailyChart.SetData(insights.Daily, insights.HasUsage ? null : "No usage recorded yet");
            modelChart.SetData(insights.Models, insights.HasUsage ? null : "No model data yet");
            return;
        }

        if (loadingProviders.Contains(selectedProviderKey))
        {
            StartLoadingAnimation();
            subtitleLabel.Text = "Scanning local sessions...";
            todayCard.SetLoading();
            monthCard.SetLoading();
            dailyChart.SetLoading();
            modelChart.SetLoading();
            return;
        }

        StopLoadingAnimation();
        subtitleLabel.Text = result.Error ?? "Usage history has not been loaded yet.";
        todayCard.SetValue("--", string.Empty);
        monthCard.SetValue("--", string.Empty);
        dailyChart.SetData([], "No history yet");
        modelChart.SetData([], "No model breakdown yet");
    }

    private ProviderUsageInsightsLookupResult GetHistory(string providerKey)
    {
        return historyByProvider.TryGetValue(providerKey, out var result)
            ? result
            : new ProviderUsageInsightsLookupResult(null, "Usage history has not been loaded yet.");
    }

    private void StartLoadingAnimation()
    {
        if (!loadingTimer.Enabled && Visible)
        {
            loadingTimer.Start();
        }
    }

    private void StopLoadingAnimation()
    {
        loadingTimer.Stop();
    }

    private void AdvanceLoadingAnimation()
    {
        // One shared phase drives every placeholder so the whole window breathes
        // in sync (calm Win11-style pulse, no sweeping shine).
        loadingPhase = WrapPhase(loadingPhase + (LoadingTimerIntervalMs / (float)LoadingPulsePeriodMs));
        todayCard.LoadingPhase = loadingPhase;
        monthCard.LoadingPhase = loadingPhase;
        dailyChart.LoadingPhase = loadingPhase;
        modelChart.LoadingPhase = loadingPhase;
    }

    private void RefreshTheme(bool force = false)
    {
        FluentTheme.RefreshAccent();
        var updated = FluentTheme.Get(uiSettings.ResolveIsDark(), onBackdrop: false);
        if (!force && updated == tokens)
        {
            return;
        }

        tokens = updated;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        BackColor = tokens.Background;
        titleLabel.ForeColor = tokens.TextPrimary;
        subtitleLabel.ForeColor = tokens.TextTertiary;
        providerCombo.ApplyTheme(tokens);
        todayCard.ApplyTheme(tokens);
        monthCard.ApplyTheme(tokens);
        dailyChart.ApplyTheme(tokens);
        modelChart.ApplyTheme(tokens);

        if (IsHandleCreated)
        {
            WindowEffects.SetImmersiveDarkMode(Handle, tokens.IsDark);
        }

        Invalidate(true);
    }

    private void LayoutContent()
    {
        // Setting ClientSize in the constructor raises OnResize before the child
        // controls exist; layout only makes sense once construction finished.
        if (modelChart is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var scale = DpiScale;
        var pad = ScaleInt(OuterPad, scale);
        var gap = ScaleInt(CardGap, scale);
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        var contentWidth = width - (pad * 2);

        SuspendLayout();

        var comboWidth = ScaleInt(200, scale);
        providerCombo.Bounds = new Rectangle(width - pad - comboWidth, ScaleInt(20, scale), comboWidth, ScaleInt(32, scale));
        titleLabel.Bounds = new Rectangle(pad, ScaleInt(16, scale), contentWidth - comboWidth - gap, ScaleInt(28, scale));
        subtitleLabel.Bounds = new Rectangle(pad, ScaleInt(46, scale), contentWidth - comboWidth - gap, ScaleInt(16, scale));

        var metricsTop = ScaleInt(76, scale);
        var metricHeight = ScaleInt(84, scale);
        var metricWidth = (contentWidth - gap) / 2;
        todayCard.Bounds = new Rectangle(pad, metricsTop, metricWidth, metricHeight);
        monthCard.Bounds = new Rectangle(pad + metricWidth + gap, metricsTop, contentWidth - metricWidth - gap, metricHeight);

        var chartsTop = metricsTop + metricHeight + gap;
        var remaining = Math.Max(ScaleInt(280, scale), height - chartsTop - pad - gap);
        var dailyHeight = Math.Max(ScaleInt(200, scale), (int)(remaining * 0.54));
        var modelHeight = Math.Max(ScaleInt(120, scale), remaining - dailyHeight);
        dailyChart.Bounds = new Rectangle(pad, chartsTop, contentWidth, dailyHeight);
        modelChart.Bounds = new Rectangle(pad, chartsTop + dailyHeight + gap, contentWidth, modelHeight);

        todayCard.LayoutScale = scale;
        monthCard.LayoutScale = scale;
        dailyChart.LayoutScale = scale;
        modelChart.LayoutScale = scale;

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

    private Font OwnFont(Font font)
    {
        ownedFonts.Add(font);
        return font;
    }

    private static int ScaleInt(int value, float scale)
    {
        return (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
    }

    // Shared process-lifetime fonts for custom paint paths that repaint at animation
    // rate; per-paint Font creation runs the font fallback chain each frame.
    private static readonly Font SharedBodyStrongFont = FluentTheme.BodyStrongFont(1f);
    private static readonly Font SharedCaptionFont = FluentTheme.CaptionFont(1f);
    private static readonly Font SharedSmallCaptionFont = FluentTheme.CaptionFont(0.85f);
    private static readonly Font SharedMetricValueFont = FluentTheme.SubtitleFont(0.95f);

    private static string MetricDetail(long tokens, decimal fastCost)
    {
        var text = FormatTokens(tokens);
        return fastCost > 0 ? $"{text} · fast {FormatUsd(fastCost)}" : text;
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
    private static string FormatAxisUsd(decimal value)
    {
        return $"${value:0.##}";
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

    /// <summary>Rounds up to the nearest 1/2/5 × 10^n so the axis maximum is a friendly number.</summary>
    private static decimal NiceCeiling(decimal value)
    {
        if (value <= 0)
        {
            return 1m;
        }

        var exponent = (int)Math.Floor(Math.Log10((double)value));
        var magnitude = (decimal)Math.Pow(10, exponent);
        var normalized = value / magnitude;
        var nice = normalized <= 1m ? 1m : normalized <= 2m ? 2m : normalized <= 5m ? 5m : 10m;
        return nice * magnitude;
    }

    private static float WrapPhase(float phase)
    {
        phase %= 1f;
        return phase < 0 ? phase + 1f : phase;
    }

    /// <summary>
    /// Calm Windows 11 skeleton: a rounded ControlFill placeholder whose opacity breathes
    /// sinusoidally between ~50% and 100% (no sweeping shine).
    /// </summary>
    private static void DrawPulsePlaceholder(Graphics graphics, RectangleF bounds, FluentTokens tokens, float phase, float radius)
    {
        if (bounds.Width <= 0f || bounds.Height <= 0f)
        {
            return;
        }

        var factor = 0.75f + (0.25f * MathF.Sin(phase * 2f * MathF.PI));
        var alpha = (int)Math.Round(Math.Clamp(factor, 0f, 1f) * tokens.ControlFill.A);
        if (alpha <= 0)
        {
            return;
        }

        using var brush = new SolidBrush(Color.FromArgb(alpha, tokens.ControlFill));
        using var path = FluentTheme.RoundedRect(bounds, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawEmpty(Graphics graphics, RectangleF bounds, string message, FluentTokens tokens)
    {
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(tokens.TextTertiary);
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        graphics.DrawString(message, SharedCaptionFont, brush, bounds, format);
    }

    /// <summary>
    /// Rounded rectangle with an independent radius per corner; pass 0 to keep a corner
    /// square. Used for chart bars (rounded tops) and bar end caps.
    /// </summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedCornersPath(
        RectangleF bounds,
        float topLeft,
        float topRight,
        float bottomRight,
        float bottomLeft)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        if (bounds.Width <= 0f || bounds.Height <= 0f)
        {
            return path;
        }

        var maxRadius = Math.Min(bounds.Width, bounds.Height) / 2f;
        topLeft = Math.Clamp(topLeft, 0f, maxRadius);
        topRight = Math.Clamp(topRight, 0f, maxRadius);
        bottomRight = Math.Clamp(bottomRight, 0f, maxRadius);
        bottomLeft = Math.Clamp(bottomLeft, 0f, maxRadius);

        path.StartFigure();
        path.AddLine(bounds.Left + topLeft, bounds.Top, bounds.Right - topRight, bounds.Top);
        if (topRight > 0f)
        {
            path.AddArc(bounds.Right - (topRight * 2f), bounds.Top, topRight * 2f, topRight * 2f, 270f, 90f);
        }

        path.AddLine(bounds.Right, bounds.Top + topRight, bounds.Right, bounds.Bottom - bottomRight);
        if (bottomRight > 0f)
        {
            path.AddArc(bounds.Right - (bottomRight * 2f), bounds.Bottom - (bottomRight * 2f), bottomRight * 2f, bottomRight * 2f, 0f, 90f);
        }

        path.AddLine(bounds.Right - bottomRight, bounds.Bottom, bounds.Left + bottomLeft, bounds.Bottom);
        if (bottomLeft > 0f)
        {
            path.AddArc(bounds.Left, bounds.Bottom - (bottomLeft * 2f), bottomLeft * 2f, bottomLeft * 2f, 90f, 90f);
        }

        path.AddLine(bounds.Left, bounds.Bottom - bottomLeft, bounds.Left, bounds.Top + topLeft);
        if (topLeft > 0f)
        {
            path.AddArc(bounds.Left, bounds.Top, topLeft * 2f, topLeft * 2f, 180f, 90f);
        }

        path.CloseFigure();
        return path;
    }

    private static void DrawCardChrome(Graphics graphics, Control control, FluentTokens tokens, float scale)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var strokeWidth = Math.Max(1f, scale);
        var bounds = new RectangleF(
            strokeWidth / 2f,
            strokeWidth / 2f,
            control.Width - strokeWidth,
            control.Height - strokeWidth);
        using var fillBrush = new SolidBrush(tokens.CardFill);
        using var borderPen = new Pen(tokens.CardStroke, strokeWidth);
        using var path = FluentTheme.RoundedRect(bounds, FluentTheme.CardCornerRadius * scale);
        graphics.FillPath(fillBrush, path);
        graphics.DrawPath(borderPen, path);
    }

    private static Color SpendCategoryColor(string label, FluentTokens theme)
    {
        var normalized = label.Replace(" fast", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (label.Contains("fast", StringComparison.OrdinalIgnoreCase))
        {
            // Hue-shifted accent, never Warning amber: with a gold system accent
            // the warning color makes every "fast" segment blend into the accent.
            return ChartSeriesAlt(theme);
        }

        if (normalized.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase) || normalized.Contains("claude-opus", StringComparison.OrdinalIgnoreCase) || normalized == "regular")
        {
            return theme.Accent;
        }

        if (normalized.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase) || normalized.Contains("claude-sonnet", StringComparison.OrdinalIgnoreCase))
        {
            return ChartSeriesAlt(theme);
        }

        if (normalized.Contains("gpt-5.3", StringComparison.OrdinalIgnoreCase) || normalized.Contains("claude-haiku", StringComparison.OrdinalIgnoreCase))
        {
            return theme.Success;
        }

        if (normalized.Contains("gpt-5.2", StringComparison.OrdinalIgnoreCase))
        {
            return theme.Danger;
        }

        var palette = ModelPalette(theme);
        return palette[StableColorIndex(normalized, palette.Length)];
    }

    private static Color[] ModelPalette(FluentTokens tokens) =>
    [
        tokens.Accent,
        tokens.Success,
        ChartSeriesAlt(tokens),
        tokens.Warning,
    ];

    /// <summary>
    /// Hue-rotated accent for the secondary chart series, so adjacent segments
    /// (opus vs sonnet, gpt-5.5 vs gpt-5.4) differ in hue rather than only lightness.
    /// </summary>
    private static Color ChartSeriesAlt(FluentTokens tokens)
    {
        var shifted = FluentTheme.ShiftHue(tokens.Accent, 60f);
        return tokens.IsDark ? FluentTheme.Lighten(shifted, 0.20f) : FluentTheme.Darken(shifted, 0.10f);
    }

    private static int StableColorIndex(string value, int length)
    {
        var hash = 17;
        foreach (var character in value)
        {
            hash = unchecked((hash * 31) + character);
        }

        return (hash & int.MaxValue) % Math.Max(1, length);
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

    /// <summary>Summary card: caption title, large value, caption detail line.</summary>
    private sealed class MetricCard : Control
    {
        private readonly string title;
        private string value = "--";
        private string detail = string.Empty;
        private bool loading;
        private float loadingPhase;
        private FluentTokens tokens = FluentTheme.Get(false, onBackdrop: false);

        public MetricCard(string title)
        {
            this.title = title;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        public float LayoutScale { get; set; } = 1f;

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

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;
            BackColor = palette.Background;
            Invalidate();
        }

        public void SetLoading()
        {
            loading = true;
            Invalidate();
        }

        public void SetValue(string newValue, string newDetail)
        {
            loading = false;
            value = newValue;
            detail = newDetail;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            DrawCardChrome(graphics, this, tokens, LayoutScale);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var pad = ScaleInt(16, LayoutScale);
            using (var titleBrush = new SolidBrush(tokens.TextSecondary))
            {
                graphics.DrawString(title, SharedCaptionFont, titleBrush, new RectangleF(pad, ScaleInt(12, LayoutScale), Width - (pad * 2), ScaleInt(16, LayoutScale)));
            }

            if (loading)
            {
                DrawPulsePlaceholder(
                    graphics,
                    new RectangleF(pad, ScaleInt(34, LayoutScale), Width * 0.45f, ScaleInt(22, LayoutScale)),
                    tokens,
                    loadingPhase,
                    ScaleInt(4, LayoutScale));
                DrawPulsePlaceholder(
                    graphics,
                    new RectangleF(pad, ScaleInt(62, LayoutScale), Width * 0.6f, ScaleInt(12, LayoutScale)),
                    tokens,
                    loadingPhase,
                    ScaleInt(4, LayoutScale));
                return;
            }

            using (var valueBrush = new SolidBrush(tokens.TextPrimary))
            {
                graphics.DrawString(value, SharedMetricValueFont, valueBrush, new RectangleF(pad - ScaleInt(2, LayoutScale), ScaleInt(30, LayoutScale), Width - (pad * 2), ScaleInt(28, LayoutScale)));
            }

            if (!string.IsNullOrEmpty(detail))
            {
                using var detailBrush = new SolidBrush(tokens.TextTertiary);
                graphics.DrawString(detail, SharedCaptionFont, detailBrush, new RectangleF(pad, ScaleInt(60, LayoutScale), Width - (pad * 2), ScaleInt(16, LayoutScale)));
            }
        }
    }

    /// <summary>
    /// Daily estimated-spend bar chart on a card: nice-number Y axis with gridlines and
    /// dollar labels, date labels along the X axis, stacked per-category bars with rounded
    /// tops, hover highlight + tooltip, and an ease-out entrance animation.
    /// </summary>
    private sealed class DailySpendChart : Control
    {
        private readonly ToolTip toolTip = new() { AutomaticDelay = 120, AutoPopDelay = 8000, ReshowDelay = 80 };
        private IReadOnlyList<ProviderDailyUsage> daily = [];
        private string? emptyMessage;
        private FluentTokens tokens = FluentTheme.Get(false, onBackdrop: false);
        private bool loading = true;
        private float loadingPhase;
        private int hoveredIndex = -1;
        private string? lastToolTipText;
        private double animationProgress = 1d;
        private IDisposable? entranceAnimation;

        public DailySpendChart()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        public float LayoutScale { get; set; } = 1f;

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

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;
            BackColor = palette.Background;
            Invalidate();
        }

        public void SetLoading()
        {
            loading = true;
            daily = [];
            emptyMessage = null;
            ResetHover();
            Invalidate();
        }

        public void SetData(IReadOnlyList<ProviderDailyUsage> data, string? message)
        {
            var hadData = !loading && daily.Count > 0;
            loading = false;
            daily = data;
            emptyMessage = message;
            ResetHover();

            // Animate only on first reveal (loading -> data); silent refreshes
            // while the window is open must not replay the entrance.
            if (!hadData && data.Count > 0 && Visible && FluentAnimator.AnimationsEnabled)
            {
                entranceAnimation?.Dispose();
                animationProgress = 0d;
                entranceAnimation = FluentAnimator.Animate(
                    0d,
                    1d,
                    350,
                    value =>
                    {
                        animationProgress = value;
                        if (!IsDisposed)
                        {
                            Invalidate();
                        }
                    });
            }
            else
            {
                animationProgress = 1d;
            }

            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                entranceAnimation?.Dispose();
                toolTip.Dispose();
            }

            base.Dispose(disposing);
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
                var categories = DailySpendCategories(day);
                var categoryLines = categories.Count == 0
                    ? string.Empty
                    : "\n" + string.Join("\n", categories
                        .OrderByDescending(category => category.EstimatedCostUsd)
                        .Take(4)
                        .Select(category => $"{ShortSpendLabel(category.Label)} {FormatUsd(category.EstimatedCostUsd)}"));
                var cacheCreateLine = day.CacheCreationTokens > 0
                    ? $", {FormatTokens(day.CacheCreationTokens)} cache create"
                    : string.Empty;
                var text = $"{day.Day:MMM d}: {FormatUsd(day.EstimatedCostUsd)}{categoryLines}\n{FormatTokens(day.TotalTokens)} total, {FormatTokens(day.OutputTokens)} output{cacheCreateLine}";
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
            ResetHover();
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            DrawCardChrome(graphics, this, tokens, LayoutScale);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var pad = ScaleInt(16, LayoutScale);
            using (var headerBrush = new SolidBrush(tokens.TextPrimary))
            {
                graphics.DrawString(
                    "Estimated spend by day",
                    SharedBodyStrongFont,
                    headerBrush,
                    new RectangleF(pad, ScaleInt(12, LayoutScale), Width * 0.6f, ScaleInt(20, LayoutScale)));
            }

            DrawLegend(graphics, pad);

            var plot = PlotBounds;
            if (loading)
            {
                DrawLoadingSkeleton(graphics, plot);
                return;
            }

            if (daily.Count == 0 || daily.All(day => day.EstimatedCostUsd <= 0))
            {
                DrawEmpty(graphics, plot, emptyMessage ?? "No spend data", tokens);
                return;
            }

            var rawMax = daily.Max(day => day.EstimatedCostUsd);
            var axisMax = NiceCeiling(rawMax);
            DrawGridAndAxis(graphics, plot, axisMax);
            DrawBars(graphics, plot, axisMax);
            DrawDateLabels(graphics, plot);
        }

        private void DrawLegend(Graphics graphics, int pad)
        {
            var categories = TopSpendCategories(daily);
            if (categories.Count == 0)
            {
                return;
            }

            using var textBrush = new SolidBrush(tokens.TextSecondary);
            var dot = ScaleInt(8, LayoutScale);
            var itemGap = ScaleInt(12, LayoutScale);
            var y = ScaleInt(15, LayoutScale);

            // Right-aligned: measure first, then draw left to right from the computed origin.
            var widths = new List<(string Label, int Width)>();
            foreach (var category in categories)
            {
                var label = ShortSpendLabel(category.Label);
                var textWidth = (int)Math.Ceiling(graphics.MeasureString(label, SharedSmallCaptionFont).Width);
                widths.Add((label, dot + ScaleInt(5, LayoutScale) + textWidth));
            }

            var total = widths.Sum(item => item.Width) + (itemGap * (widths.Count - 1));
            var x = Width - pad - total;
            for (var index = 0; index < categories.Count; index++)
            {
                using var dotBrush = new SolidBrush(SpendCategoryColor(categories[index].Label, tokens));
                graphics.FillEllipse(dotBrush, x, y + ScaleInt(2, LayoutScale), dot, dot);
                graphics.DrawString(widths[index].Label, SharedSmallCaptionFont, textBrush, x + dot + ScaleInt(5, LayoutScale), y);
                x += widths[index].Width + itemGap;
            }
        }

        private void DrawLoadingSkeleton(Graphics graphics, Rectangle plot)
        {
            // Ghost bars with deterministic pseudo-random heights so the skeleton
            // reads as "a chart is coming", pulsing in sync with the other cards.
            var slots = 12;
            var gap = ScaleInt(8, LayoutScale);
            var barWidth = Math.Max(4, (plot.Width - (gap * (slots - 1))) / slots);
            for (var index = 0; index < slots; index++)
            {
                var fraction = 0.25f + (((index * 37) % 67) / 100f);
                var barHeight = (int)(plot.Height * Math.Min(0.92f, fraction));
                var x = plot.Left + (index * (barWidth + gap));
                DrawPulsePlaceholder(
                    graphics,
                    new RectangleF(x, plot.Bottom - barHeight, barWidth, barHeight),
                    tokens,
                    loadingPhase,
                    Math.Max(2f, 3f * LayoutScale));
            }
        }

        private void DrawGridAndAxis(Graphics graphics, Rectangle plot, decimal axisMax)
        {
            var strokeWidth = Math.Max(1f, LayoutScale);
            using var gridPen = new Pen(tokens.MeterTrack, strokeWidth);
            using var baselinePen = new Pen(tokens.CardStroke, strokeWidth);
            using var labelBrush = new SolidBrush(tokens.TextTertiary);
            using var labelFormat = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center
            };

            var gutter = ScaleInt(8, LayoutScale);
            foreach (var fraction in new[] { 1m, 0.5m })
            {
                var y = plot.Bottom - (float)((double)fraction * plot.Height);
                graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                graphics.DrawString(
                    FormatAxisUsd(axisMax * fraction),
                    SharedSmallCaptionFont,
                    labelBrush,
                    new RectangleF(0, y - ScaleInt(8, LayoutScale), plot.Left - gutter, ScaleInt(16, LayoutScale)),
                    labelFormat);
            }

            var baselineY = plot.Bottom - (strokeWidth / 2f);
            graphics.DrawLine(baselinePen, plot.Left, baselineY, plot.Right, baselineY);
        }

        private void DrawBars(Graphics graphics, Rectangle plot, decimal axisMax)
        {
            var slotWidth = (float)plot.Width / daily.Count;
            var barWidth = Math.Clamp(slotWidth * 0.62f, 3f, 22f * LayoutScale);
            var topRadius = Math.Min(barWidth / 2f, 3f * LayoutScale);
            var minBarHeight = Math.Max(2, ScaleInt(2, LayoutScale));
            var progress = animationProgress;

            // Hovered slot highlight behind the bar, full plot height.
            if (hoveredIndex >= 0 && hoveredIndex < daily.Count)
            {
                var slotLeft = plot.Left + (hoveredIndex * slotWidth);
                using var hoverBrush = new SolidBrush(tokens.SubtleHover);
                using var hoverPath = FluentTheme.RoundedRect(
                    new RectangleF(slotLeft, plot.Top, slotWidth, plot.Height),
                    FluentTheme.ControlCornerRadius * LayoutScale);
                graphics.FillPath(hoverBrush, hoverPath);
            }

            for (var index = 0; index < daily.Count; index++)
            {
                var day = daily[index];
                if (day.EstimatedCostUsd <= 0)
                {
                    continue;
                }

                var x = plot.Left + (index * slotWidth) + ((slotWidth - barWidth) / 2f);
                var fullHeight = (int)Math.Round(plot.Height * (double)(day.EstimatedCostUsd / axisMax) * progress);
                var totalHeight = Math.Clamp(fullHeight, minBarHeight, plot.Height);
                var hovered = index == hoveredIndex;
                var categories = DailySpendCategories(day).Where(category => category.EstimatedCostUsd > 0).ToArray();

                if (categories.Length <= 1)
                {
                    var color = categories.Length == 1 ? SpendCategoryColor(categories[0].Label, tokens) : tokens.Accent;
                    if (hovered)
                    {
                        color = FluentTheme.Lighten(color, 0.15f);
                    }

                    using var brush = new SolidBrush(color);
                    using var path = RoundedCornersPath(
                        new RectangleF(x, plot.Bottom - totalHeight, barWidth, totalHeight),
                        topRadius,
                        topRadius,
                        0f,
                        0f);
                    graphics.FillPath(brush, path);
                    continue;
                }

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

                    var color = SpendCategoryColor(category.Label, tokens);
                    if (hovered)
                    {
                        color = FluentTheme.Lighten(color, 0.15f);
                    }

                    using var brush = new SolidBrush(color);
                    var segmentTop = plot.Bottom - paintedHeight - height;
                    var isTopSegment = paintedHeight + height >= totalHeight;
                    if (isTopSegment)
                    {
                        using var path = RoundedCornersPath(
                            new RectangleF(x, segmentTop, barWidth, height),
                            topRadius,
                            topRadius,
                            0f,
                            0f);
                        graphics.FillPath(brush, path);
                    }
                    else
                    {
                        graphics.FillRectangle(brush, new RectangleF(x, segmentTop, barWidth, height));
                    }

                    paintedHeight += height;
                }
            }
        }

        private void DrawDateLabels(Graphics graphics, Rectangle plot)
        {
            if (daily.Count == 0)
            {
                return;
            }

            using var labelBrush = new SolidBrush(tokens.TextTertiary);
            using var centered = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = StringAlignment.Center
            };

            var slotWidth = (float)plot.Width / daily.Count;
            var labelWidth = 44f * LayoutScale;
            var step = Math.Max(1, (int)Math.Ceiling(labelWidth / slotWidth));
            var labelTop = plot.Bottom + ScaleInt(6, LayoutScale);

            for (var index = 0; index < daily.Count; index += step)
            {
                // Leave room so a stepped label never collides with the right-aligned last one.
                if (index >= daily.Count - 1 || (plot.Left + ((index + 0.5f) * slotWidth) + (labelWidth / 2f)) > plot.Right - labelWidth)
                {
                    break;
                }

                var center = plot.Left + ((index + 0.5f) * slotWidth);
                graphics.DrawString(
                    daily[index].Day.ToString("MMM d"),
                    SharedSmallCaptionFont,
                    labelBrush,
                    new RectangleF(center - (labelWidth / 2f), labelTop, labelWidth, ScaleInt(14, LayoutScale)),
                    centered);
            }

            var last = daily[^1].Day.ToString("MMM d");
            var lastSize = graphics.MeasureString(last, SharedSmallCaptionFont);
            graphics.DrawString(last, SharedSmallCaptionFont, labelBrush, plot.Right - lastSize.Width, labelTop);
        }

        private Rectangle PlotBounds
        {
            get
            {
                var pad = ScaleInt(16, LayoutScale);
                var gutterLeft = ScaleInt(40, LayoutScale);
                var top = ScaleInt(44, LayoutScale);
                var bottom = ScaleInt(26, LayoutScale);
                return new Rectangle(
                    pad + gutterLeft,
                    top,
                    Math.Max(20, Width - (pad * 2) - gutterLeft),
                    Math.Max(20, Height - top - bottom - pad));
            }
        }

        private void ResetHover()
        {
            hoveredIndex = -1;
            lastToolTipText = null;
            toolTip.SetToolTip(this, null);
        }

        private int HitTest(Point point)
        {
            var plot = PlotBounds;
            if (daily.Count == 0 || !plot.Contains(point))
            {
                return -1;
            }

            var slotWidth = (float)plot.Width / daily.Count;
            var index = (int)((point.X - plot.Left) / slotWidth);
            return index >= 0 && index < daily.Count ? index : -1;
        }

        private int ScaleInt(int value, float scale = 0f)
        {
            return UsageGraphsForm.ScaleInt(value, scale > 0f ? scale : LayoutScale);
        }
    }

    /// <summary>
    /// Per-model spend breakdown on a card: one row per model (top spenders first) with a
    /// color dot, friendly name, right-aligned cost and a proportional rounded bar, plus an
    /// overflow caption when more models exist than fit.
    /// </summary>
    private sealed class ModelSpendChart : Control
    {
        private readonly ToolTip toolTip = new() { AutomaticDelay = 120, AutoPopDelay = 8000, ReshowDelay = 80 };
        private IReadOnlyList<ProviderModelUsage> models = [];
        private string? emptyMessage;
        private FluentTokens tokens = FluentTheme.Get(false, onBackdrop: false);
        private bool loading = true;
        private float loadingPhase;
        private int hoveredIndex = -1;
        private string? lastToolTipText;
        private double animationProgress = 1d;
        private IDisposable? entranceAnimation;

        public ModelSpendChart()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        public float LayoutScale { get; set; } = 1f;

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

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;
            BackColor = palette.Background;
            Invalidate();
        }

        public void SetLoading()
        {
            loading = true;
            models = [];
            emptyMessage = null;
            ResetHover();
            Invalidate();
        }

        public void SetData(IReadOnlyList<ProviderModelUsage> data, string? message)
        {
            var hadData = !loading && models.Count > 0;
            loading = false;
            models = data;
            emptyMessage = message;
            ResetHover();

            if (!hadData && data.Count > 0 && Visible && FluentAnimator.AnimationsEnabled)
            {
                entranceAnimation?.Dispose();
                animationProgress = 0d;
                entranceAnimation = FluentAnimator.Animate(
                    0d,
                    1d,
                    350,
                    value =>
                    {
                        animationProgress = value;
                        if (!IsDisposed)
                        {
                            Invalidate();
                        }
                    });
            }
            else
            {
                animationProgress = 1d;
            }

            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                entranceAnimation?.Dispose();
                toolTip.Dispose();
            }

            base.Dispose(disposing);
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
            var visible = VisibleModels;
            if (index >= 0 && index < visible.Count)
            {
                var model = visible[index];
                var fastLine = model.FastEstimatedCostUsd > 0
                    ? $"\nFast {FormatUsd(model.FastEstimatedCostUsd)}, regular {FormatUsd(model.RegularEstimatedCostUsd)}"
                    : string.Empty;
                var cacheCreateLine = model.CacheCreationTokens > 0
                    ? $", {FormatTokens(model.CacheCreationTokens)} cache create"
                    : string.Empty;
                var text = $"{model.Model}\nEstimated {FormatUsd(model.EstimatedCostUsd)}{fastLine}\n{FormatTokens(model.TotalTokens)} total, {FormatTokens(model.OutputTokens)} output{cacheCreateLine}";
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
            ResetHover();
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            DrawCardChrome(graphics, this, tokens, LayoutScale);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var pad = ScaleInt(16, LayoutScale);
            using (var headerBrush = new SolidBrush(tokens.TextPrimary))
            {
                graphics.DrawString(
                    "Estimated spend by model",
                    SharedBodyStrongFont,
                    headerBrush,
                    new RectangleF(pad, ScaleInt(12, LayoutScale), Width * 0.6f, ScaleInt(20, LayoutScale)));
            }

            using (var rangeBrush = new SolidBrush(tokens.TextTertiary))
            using (var farFormat = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far })
            {
                graphics.DrawString(
                    "Last 30 days",
                    SharedSmallCaptionFont,
                    rangeBrush,
                    new RectangleF(Width / 2f, ScaleInt(15, LayoutScale), (Width / 2f) - pad, ScaleInt(16, LayoutScale)),
                    farFormat);
            }

            var rowsTop = RowsTop;
            var rowHeight = RowHeight;
            if (loading)
            {
                for (var index = 0; index < Math.Min(3, Math.Max(1, (Height - rowsTop - pad) / rowHeight)); index++)
                {
                    var y = rowsTop + (index * rowHeight);
                    DrawPulsePlaceholder(
                        graphics,
                        new RectangleF(pad, y + ScaleInt(4, LayoutScale), Width * 0.4f, ScaleInt(12, LayoutScale)),
                        tokens,
                        loadingPhase,
                        ScaleInt(4, LayoutScale));
                    DrawPulsePlaceholder(
                        graphics,
                        new RectangleF(pad, y + ScaleInt(24, LayoutScale), Width - (pad * 2), ScaleInt(6, LayoutScale)),
                        tokens,
                        loadingPhase,
                        ScaleInt(3, LayoutScale));
                }

                return;
            }

            var ranked = RankedModels;
            if (ranked.Count == 0)
            {
                DrawEmpty(
                    graphics,
                    new RectangleF(pad, rowsTop, Width - (pad * 2), Math.Max(20, Height - rowsTop - pad)),
                    emptyMessage ?? "No model data",
                    tokens);
                return;
            }

            var visible = VisibleModels;
            var maxCost = Math.Max(0.01m, ranked[0].EstimatedCostUsd);
            var barAreaWidth = Width - (pad * 2);
            using var nameBrush = new SolidBrush(tokens.TextPrimary);
            using var costBrush = new SolidBrush(tokens.TextSecondary);
            using var costFormat = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far };

            for (var index = 0; index < visible.Count; index++)
            {
                var model = visible[index];
                var rowTop = rowsTop + (index * rowHeight);
                var hovered = index == hoveredIndex;

                if (hovered)
                {
                    using var hoverBrush = new SolidBrush(tokens.SubtleHover);
                    using var hoverPath = FluentTheme.RoundedRect(
                        new RectangleF(pad - ScaleInt(8, LayoutScale), rowTop, barAreaWidth + ScaleInt(16, LayoutScale), rowHeight - ScaleInt(4, LayoutScale)),
                        FluentTheme.ControlCornerRadius * LayoutScale);
                    graphics.FillPath(hoverBrush, hoverPath);
                }

                var color = SpendCategoryColor(model.Model, tokens);
                var dot = ScaleInt(8, LayoutScale);
                using (var dotBrush = new SolidBrush(color))
                {
                    graphics.FillEllipse(dotBrush, pad, rowTop + ScaleInt(7, LayoutScale), dot, dot);
                }

                graphics.DrawString(
                    FriendlyModelLabel(model.Model),
                    SharedCaptionFont,
                    nameBrush,
                    new RectangleF(pad + dot + ScaleInt(8, LayoutScale), rowTop + ScaleInt(3, LayoutScale), barAreaWidth * 0.6f, ScaleInt(16, LayoutScale)));

                graphics.DrawString(
                    FormatUsd(model.EstimatedCostUsd),
                    SharedCaptionFont,
                    costBrush,
                    new RectangleF(pad, rowTop + ScaleInt(3, LayoutScale), barAreaWidth, ScaleInt(16, LayoutScale)),
                    costFormat);

                var barTop = rowTop + ScaleInt(24, LayoutScale);
                var barHeight = Math.Max(4, ScaleInt(6, LayoutScale));
                var trackBounds = new RectangleF(pad, barTop, barAreaWidth, barHeight);
                using (var trackBrush = new SolidBrush(tokens.MeterTrack))
                using (var trackPath = FluentTheme.RoundedRect(trackBounds, barHeight / 2f))
                {
                    graphics.FillPath(trackBrush, trackPath);
                }

                var fraction = (double)(model.EstimatedCostUsd / maxCost) * animationProgress;
                var fillWidth = (float)Math.Max(barHeight, barAreaWidth * fraction);
                var fillColor = hovered ? FluentTheme.Lighten(color, 0.15f) : color;
                using (var fillBrush = new SolidBrush(fillColor))
                using (var fillPath = FluentTheme.RoundedRect(new RectangleF(pad, barTop, fillWidth, barHeight), barHeight / 2f))
                {
                    graphics.FillPath(fillBrush, fillPath);
                }
            }

            if (ranked.Count > visible.Count)
            {
                var rest = ranked.Skip(visible.Count).ToArray();
                var restCost = rest.Sum(model => model.EstimatedCostUsd);
                using var moreBrush = new SolidBrush(tokens.TextTertiary);
                graphics.DrawString(
                    $"+{rest.Length} more · {FormatUsd(restCost)}",
                    SharedSmallCaptionFont,
                    moreBrush,
                    new RectangleF(pad, rowsTop + (visible.Count * rowHeight), barAreaWidth, ScaleInt(16, LayoutScale)));
            }
        }

        private IReadOnlyList<ProviderModelUsage> RankedModels =>
            models.Where(model => model.EstimatedCostUsd > 0)
                .OrderByDescending(model => model.EstimatedCostUsd)
                .ToArray();

        private IReadOnlyList<ProviderModelUsage> VisibleModels
        {
            get
            {
                var ranked = RankedModels;
                var available = Height - RowsTop - ScaleInt(16, LayoutScale);
                var fit = Math.Max(1, available / RowHeight);
                return ranked.Take(Math.Min(6, fit)).ToArray();
            }
        }

        private int RowsTop => ScaleInt(40, LayoutScale);

        private int RowHeight => ScaleInt(44, LayoutScale);

        private void ResetHover()
        {
            hoveredIndex = -1;
            lastToolTipText = null;
            toolTip.SetToolTip(this, null);
        }

        private int HitTest(Point point)
        {
            var visible = VisibleModels;
            if (visible.Count == 0 || point.Y < RowsTop)
            {
                return -1;
            }

            var index = (point.Y - RowsTop) / Math.Max(1, RowHeight);
            return index >= 0 && index < visible.Count ? index : -1;
        }

        private int ScaleInt(int value, float scale = 0f)
        {
            return UsageGraphsForm.ScaleInt(value, scale > 0f ? scale : LayoutScale);
        }
    }
}
