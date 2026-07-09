using Microsoft.Win32;

// This UI is constructed in code only (no WinForms designer), so designer
// code-serialization metadata for control properties is irrelevant.
#pragma warning disable WFO1000

namespace CodexBarWindows;

public sealed class UsagePopupForm : Form
{
    // Base (96-dpi) layout metrics, all kept on a 4px grid and scaled via ScaleInt.
    private const int BaseWidth = 420;
    private const int OuterMargin = 16;
    private const int HeaderTitleTop = 12;
    private const int UsageCardTop = 68;
    private const int UsageRowHeight = 76;
    private const int CardGap = 8;
    private const int StatusHeight = 16;
    private const int BottomMargin = 12;

    private readonly Label titleLabel;
    private readonly List<ProviderTabButton> tabButtons = [];
    private readonly List<ProviderDescriptor> providers = [];
    private readonly Dictionary<string, ProviderUsageLookupResult> usageByProvider = [];
    private readonly Label planLabel;
    private readonly Label statusLabel;
    private readonly UsageCardPanel usageCard;
    private readonly UsageSection fiveHourSection;
    private readonly UsageSection weeklySection;
    private readonly UsageSection tertiarySection;
    private readonly GlyphButton graphsButton;
    private readonly GlyphButton closeButton;
    private readonly List<Font> ownedFonts = [];
    private UiSettings uiSettings;
    private FluentTokens tokens;
    private IDisposable? entranceAnimation;
    private bool backdropActive;
    private bool anchorToBottom = true;
    private string selectedProviderKey = CodexProviderKey("default");

    public event EventHandler<string>? SelectedProviderChanged;

    /// <summary>Raised when the user clicks the header history button to open the usage graphs window.</summary>
    public event EventHandler? UsageGraphsRequested;

    public UsagePopupForm()
    {
        uiSettings = UiSettings.Load();
        tokens = FluentTheme.Get(uiSettings.ResolveIsDark(), onBackdrop: false);

        AutoScaleMode = AutoScaleMode.None;
        BackColor = tokens.Background;
        ClientSize = new Size(BaseWidth, UsageCardTop + (UsageRowHeight * 2));
        ControlBox = false;
        DoubleBuffered = true;
        Font = OwnFont(FluentTheme.CaptionFont(1f));
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Codex limits";
        TopMost = true;

        closeButton = new GlyphButton(FluentIcons.Close, "Close")
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TabIndex = 0
        };
        closeButton.Click += (_, _) => Hide();

        graphsButton = new GlyphButton(FluentIcons.History, "Usage graphs")
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TabIndex = 1
        };
        graphsButton.Click += (_, _) =>
        {
            Hide();
            UsageGraphsRequested?.Invoke(this, EventArgs.Empty);
        };

        titleLabel = new FluentLabel
        {
            AutoSize = false,
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Font = OwnFont(FluentTheme.SubtitleFont(1f)),
            Text = "Codex rate limits",
            UseCompatibleTextRendering = true
        };

        planLabel = new FluentLabel
        {
            AutoSize = false,
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Font = OwnFont(FluentTheme.CaptionFont(1f)),
            UseCompatibleTextRendering = true
        };

        usageCard = new UsageCardPanel();
        fiveHourSection = new UsageSection("5 hour limit");
        weeklySection = new UsageSection("Weekly limit") { ShowSeparator = true };
        tertiarySection = new UsageSection("API") { ShowSeparator = true };
        usageCard.Controls.Add(fiveHourSection);
        usageCard.Controls.Add(weeklySection);
        usageCard.Controls.Add(tertiarySection);

        statusLabel = new FluentLabel
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = OwnFont(FluentTheme.CaptionFont(1f)),
            UseCompatibleTextRendering = true
        };

        Controls.Add(titleLabel);
        Controls.Add(planLabel);
        Controls.Add(usageCard);
        Controls.Add(statusLabel);
        Controls.Add(graphsButton);
        Controls.Add(closeButton);

        EnableDragMove(this);
        EnableDragMove(titleLabel);
        EnableDragMove(planLabel);
        EnableDragMove(statusLabel);
        EnableDragMove(usageCard);
        fiveHourSection.EnableDragMove();
        weeklySection.EnableDragMove();
        tertiarySection.EnableDragMove();

        Deactivate += (_, _) => Hide();
        FormClosing += OnFormClosing;
        UiSettings.Changed += OnUiSettingsChanged;

        ConfigureProviders([new ProviderDescriptor(CodexProviderKey("default"), "Codex", UsageProvider.Codex), ClaudeProvider, CursorProvider]);
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
            const int wsExToolWindow = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow;
            if (!WindowEffects.IsWindows11)
            {
                // Pre-Win11 fallback only: on Windows 11 the DWM rounded-corner
                // preference supplies the standard popup shadow.
                cp.ClassStyle |= csDropShadow;
            }

            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        ApplyBackdropMaterial();
        WindowEffects.SetRoundedCorners(Handle, round: true);
        RefreshTheme(force: true);
        ApplyScaledLayout();
        UpdateWindowRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var strokeWidth = Math.Max(1f, DpiScale);
        using var borderPen = new Pen(tokens.CardStroke, strokeWidth);
        var borderRect = new RectangleF(
            strokeWidth / 2f,
            strokeWidth / 2f,
            Width - strokeWidth,
            Height - strokeWidth);
        using var path = FluentTheme.RoundedRect(borderRect, FluentTheme.OverlayCornerRadius * DpiScale);
        e.Graphics.DrawPath(borderPen, path);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // On the backdrop path alpha-0 pixels reveal the DWM material; the optional
        // theme tint then composites over it (0 = pure material, 100 = solid).
        if (!backdropActive)
        {
            e.Graphics.Clear(tokens.Background);
            return;
        }

        e.Graphics.Clear(Color.Transparent);
        var tintPercent = Math.Clamp(uiSettings.TintOpacityPercent, 0, 100);
        if (tintPercent > 0)
        {
            var alpha = (int)Math.Round(255 * (tintPercent / 100.0));
            using var tintBrush = new SolidBrush(Color.FromArgb(alpha, tokens.Background));
            e.Graphics.FillRectangle(tintBrush, ClientRectangle);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateWindowRegion();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            // Stop the entrance slide as soon as the flyout hides so no
            // animation timer keeps running behind an invisible window.
            entranceAnimation?.Dispose();
            entranceAnimation = null;
        }
    }

    private void UpdateWindowRegion()
    {
        // Windows 11 rounds (and shadows) the frameless popup via DWM; a custom
        // region there causes corner artifacts. Pre-Win11 keeps the legacy region.
        if (WindowEffects.IsWindows11 || Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = FluentTheme.RoundedRect(
            new RectangleF(0f, 0f, Width, Height),
            FluentTheme.OverlayCornerRadius * DpiScale);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    public void ShowNear(Point anchor)
    {
        // DpiScale is 1.0 until the handle exists, so the first-ever open would lay out
        // and position at 96-dpi metrics and only grow after Show() — off-screen at >100%.
        if (!IsHandleCreated)
        {
            _ = Handle;
        }

        RefreshTheme();
        ApplyScaledLayout();
        var target = CalculateLocation(anchor);

        // Entrance: slide up ~12px while keeping the window fully opaque.
        // Never animate Opacity here - WS_EX_LAYERED disables the DWM backdrop.
        entranceAnimation?.Dispose();
        var slideDistance = ScaleInt(12, DpiScale);
        entranceAnimation = FluentAnimator.Animate(
            slideDistance,
            0d,
            150,
            offset =>
            {
                if (!IsDisposed)
                {
                    Location = new Point(target.X, target.Y + (int)Math.Round(offset));
                }
            });

        Show();
        Activate();

        if (uiSettings.Material != BackdropMaterial.Solid)
        {
            ApplyBackdropMaterial();
            NudgeSizeForBackdrop();
        }
    }

    /// <summary>
    /// Forces DWM to attach the backdrop visual. A backdrop applied while the window is
    /// hidden does not composite until the visible window genuinely resizes (a frame-changed
    /// SetWindowPos is not enough — switching to the taller Cursor tab "fixed" it, switching
    /// between same-height tabs did not). Grow 1px now and restore on the next message-pump
    /// pass so DWM sees two real size changes; visually imperceptible.
    /// </summary>
    private void NudgeSizeForBackdrop()
    {
        if (!IsHandleCreated || !Visible || !backdropActive)
        {
            return;
        }

        var size = ClientSize;
        ClientSize = new Size(size.Width, size.Height + 1);
        BeginInvoke(new Action(() =>
        {
            if (!IsDisposed && IsHandleCreated)
            {
                ClientSize = size;
            }
        }));
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
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

        if (m.Msg is wmSettingChange or wmDpiChanged or wmThemeChanged or wmDwmColorizationColorChanged)
        {
            RefreshTheme();
            ApplyScaledLayout();
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
        ApplyBackdropMaterial();
        RefreshTheme(force: true);
        ApplyScaledLayout();
        NudgeSizeForBackdrop();
    }

    private void ApplyBackdropMaterial()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        if (uiSettings.Material == BackdropMaterial.Solid)
        {
            // Solid keeps the DWM material off and paints an opaque themed body.
            WindowEffects.TryApplyBackdrop(Handle, SystemBackdrop.None);
            backdropActive = false;
            return;
        }

        var backdrop = uiSettings.Material switch
        {
            BackdropMaterial.Mica => SystemBackdrop.Mica,
            BackdropMaterial.MicaAlt => SystemBackdrop.Tabbed,
            _ => SystemBackdrop.Acrylic
        };

        // The frame must already be extended when the backdrop attribute is set,
        // otherwise DWM may not composite the material until the next frame change.
        if (WindowEffects.IsBackdropSupported)
        {
            WindowEffects.ExtendFrameIntoClientArea(Handle);
        }

        backdropActive = WindowEffects.TryApplyBackdrop(Handle, backdrop);
    }

    private void ApplyScaledLayout()
    {
        var scale = DpiScale;
        var selectedProvider = GetProvider(selectedProviderKey);
        var showTertiary = ShouldShowTertiary(selectedProvider);
        var rowCount = showTertiary ? 3 : 2;

        var rowHeight = ScaleInt(UsageRowHeight, scale);
        var cardTop = ScaleInt(UsageCardTop, scale);
        var cardHeight = rowHeight * rowCount;
        var gap = ScaleInt(CardGap, scale);
        var statusTop = cardTop + cardHeight + gap;
        var clientHeight = statusTop + ScaleInt(StatusHeight, scale) + ScaleInt(BottomMargin, scale);

        SuspendLayout();

        var previousBottom = Top + Height;
        ClientSize = new Size(ScaleInt(BaseWidth, scale), clientHeight);
        if (Visible && IsHandleCreated && anchorToBottom)
        {
            // Keep the flyout's bottom edge pinned to the taskbar anchor while
            // expanding/collapsing content; it grows upward like system flyouts.
            Top = previousBottom - Height;
        }

        closeButton.Bounds = ScaleRect(374, 14, 32, 32, scale);
        graphsButton.Bounds = ScaleRect(338, 14, 32, 32, scale);
        var firstTabLeft = LayoutTabButtons(scale);
        var titleLeft = ScaleInt(OuterMargin, scale);
        var headerTextWidth = Math.Max(ScaleInt(140, scale), firstTabLeft - titleLeft - ScaleInt(8, scale));
        titleLabel.Bounds = new Rectangle(titleLeft, ScaleInt(HeaderTitleTop, scale), headerTextWidth, ScaleInt(28, scale));
        planLabel.Bounds = new Rectangle(titleLeft, ScaleInt(42, scale), headerTextWidth, ScaleInt(16, scale));

        usageCard.Bounds = new Rectangle(
            ScaleInt(OuterMargin, scale),
            cardTop,
            ScaleInt(BaseWidth - (OuterMargin * 2), scale),
            cardHeight);
        var cardWidth = usageCard.Width;
        fiveHourSection.Bounds = new Rectangle(0, 0, cardWidth, rowHeight);
        weeklySection.Bounds = new Rectangle(0, rowHeight, cardWidth, rowHeight);
        tertiarySection.Visible = showTertiary;
        tertiarySection.Bounds = new Rectangle(0, rowHeight * 2, cardWidth, rowHeight);

        statusLabel.Bounds = new Rectangle(
            ScaleInt(OuterMargin + 4, scale),
            statusTop,
            ScaleInt(BaseWidth - (OuterMargin * 2) - 8, scale),
            ScaleInt(StatusHeight, scale));

        usageCard.ApplyLayoutScale(scale);
        fiveHourSection.ApplyLayoutScale(scale);
        weeklySection.ApplyLayoutScale(scale);
        tertiarySection.ApplyLayoutScale(scale);

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
            .Select(entry => new ProviderDescriptor(CodexProviderKey(entry.Id), entry.Name, UsageProvider.Codex))
            .Append(ClaudeProvider)
            .Append(CursorProvider)
            .ToList();

        ConfigureProviders(descriptors);
    }

    public void UpdateUsage(string providerKey, ProviderUsageLookupResult result)
    {
        usageByProvider[providerKey] = result;

        if (providerKey == selectedProviderKey)
        {
            ApplyScaledLayout();
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
            fiveHourSection.SetLoading(provider.IsCursor ? "Total" : "5 hour limit");
            weeklySection.SetLoading(provider.IsCursor ? "Auto" : "Weekly limit");
            tertiarySection.SetLoading(provider.IsCursor ? "API" : "Fable 5 limit");
            tertiarySection.Visible = ShouldShowTertiary(provider);
            statusLabel.Text = provider.IsClaude
                ? "Reading from Claude Code OAuth..."
                : provider.IsCursor
                    ? "Reading from cursor.com..."
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
        tertiarySection.Visible = ShouldShowTertiary(provider);
        titleLabel.Text = $"{provider.Name} rate limits";
        var result = GetProviderUsage(selectedProviderKey);

        if (result.Snapshot is not { } snapshot)
        {
            planLabel.Text = provider.IsCursor
                ? "Waiting for Cursor usage data"
                : $"Waiting for local {provider.Name} usage data";
            fiveHourSection.SetUnavailable(provider.IsCursor ? "Total" : "5 hour limit");
            weeklySection.SetUnavailable(provider.IsCursor ? "Auto" : "Weekly limit");
            tertiarySection.SetUnavailable(provider.IsCursor ? "API" : "Fable 5 limit");
            statusLabel.Text = result.Error ?? "No usage data found.";
            return;
        }

        planLabel.Text = provider.IsCursor
            ? CursorPlanText(snapshot)
            : string.IsNullOrWhiteSpace(snapshot.PlanType)
                ? provider.IsClaude ? "Claude Code usage data" : "Local Codex session data"
                : $"{ToTitleCase(snapshot.PlanType)} plan";

        fiveHourSection.SetUsage(snapshot.Primary);
        if (snapshot.Secondary is { } secondary)
        {
            weeklySection.SetUsage(secondary);
        }
        else
        {
            weeklySection.SetUnavailable(provider.IsCursor ? "Auto" : snapshot.Primary.WindowMinutes == 10080 ? "5 hour limit" : "Weekly limit");
        }

        if (provider.IsCursor || provider.IsClaude)
        {
            if (snapshot.Tertiary is { } tertiary)
            {
                tertiarySection.SetUsage(tertiary);
            }
            else
            {
                tertiarySection.SetUnavailable(provider.IsCursor ? "API" : "Fable 5 limit");
            }
        }

        statusLabel.Text = provider.IsCursor ? CursorStatusText(snapshot) : string.Empty;
    }

    private ProviderUsageLookupResult GetProviderUsage(string providerKey)
    {
        return usageByProvider.TryGetValue(providerKey, out var result)
            ? result
            : new ProviderUsageLookupResult(null, "Usage has not been loaded yet.");
    }

    private bool ShouldShowTertiary(ProviderDescriptor provider)
    {
        return provider.IsCursor ||
            (provider.IsClaude && GetProviderUsage(provider.Key).Snapshot?.Tertiary is not null);
    }

    private ProviderDescriptor GetProvider(string providerKey)
    {
        return providers.FirstOrDefault(provider => provider.Key == providerKey)
            ?? providers.FirstOrDefault()
            ?? new ProviderDescriptor(CodexProviderKey("default"), "Codex", UsageProvider.Codex);
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

            var tabButton = new ProviderTabButton(provider.Name, provider.Key, provider.Provider)
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AccessibleName = provider.Name
            };
            tabButton.Click += (_, _) => SelectProvider(provider.Key);
            tabButton.ApplyTheme(tokens);
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
        var right = ScaleInt(330, scale);
        var width = ScaleInt(32, scale);
        var gap = ScaleInt(4, scale);
        var firstLeft = right;
        for (var index = tabButtons.Count - 1; index >= 0; index--)
        {
            firstLeft = right - width;
            tabButtons[index].Bounds = new Rectangle(firstLeft, ScaleInt(14, scale), width, ScaleInt(32, scale));
            tabButtons[index].BringToFront();
            right -= width + gap;
        }

        graphsButton.BringToFront();
        closeButton.BringToFront();
        return firstLeft;
    }

    public static string CodexProviderKey(string id)
    {
        return $"codex:{id}";
    }

    public const string ClaudeProviderKey = "claude";
    public const string CursorProviderKey = "cursor";
    private static readonly ProviderDescriptor ClaudeProvider = new(ClaudeProviderKey, "Claude", UsageProvider.Claude);
    private static readonly ProviderDescriptor CursorProvider = new(CursorProviderKey, "Cursor", UsageProvider.Cursor);
    private sealed record ProviderDescriptor(string Key, string Name, UsageProvider Provider)
    {
        public bool IsClaude => Provider == UsageProvider.Claude;
        public bool IsCursor => Provider == UsageProvider.Cursor;
    }

    private Point CalculateLocation(Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        var workingArea = screen.WorkingArea;

        var x = Math.Clamp(anchor.X - Width + 20, workingArea.Left + 8, workingArea.Right - Width - 8);
        anchorToBottom = anchor.Y >= workingArea.Top + (workingArea.Height / 2);
        var y = anchorToBottom
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

    private static string CursorPlanText(ProviderUsageSnapshot snapshot)
    {
        var plan = string.IsNullOrWhiteSpace(snapshot.PlanType) ? "Cursor usage data" : snapshot.PlanType;
        return string.IsNullOrWhiteSpace(snapshot.AccountEmail)
            ? plan
            : $"{plan} · {snapshot.AccountEmail}";
    }

    private static string CursorStatusText(ProviderUsageSnapshot snapshot)
    {
        if (snapshot.Cost is not { } cost)
        {
            return $"Updated {FormatObservedAt(snapshot.ObservedAt)}";
        }

        var used = FormatCurrency(cost.Used, cost.CurrencyCode);
        var budget = cost.Limit is { } limit && limit > 0
            ? $" / {FormatCurrency(limit, cost.CurrencyCode)}"
            : string.Empty;
        return $"On-demand {used}{budget} · updated {FormatObservedAt(snapshot.ObservedAt)}";
    }

    private static string FormatCurrency(decimal value, string currencyCode)
    {
        if (!string.Equals(currencyCode, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return $"{value:0.##} {currencyCode}";
        }

        return value <= 0 ? "$0.00" : value < 0.01m ? "<$0.01" : $"${value:0.00}";
    }

    private void RefreshTheme(bool force = false)
    {
        FluentTheme.RefreshAccent();
        var updated = FluentTheme.Get(uiSettings.ResolveIsDark(), onBackdrop: backdropActive);
        if (!force && updated == tokens)
        {
            return;
        }

        tokens = updated;
        ApplyTheme();
    }

    private static bool IsSystemDarkTheme()
    {
        var isLight = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1) as int? ?? 1;

        return isLight == 0;
    }

    private void ApplyTheme()
    {
        BackColor = tokens.Background;
        titleLabel.ForeColor = tokens.TextPrimary;
        planLabel.ForeColor = tokens.TextTertiary;
        statusLabel.ForeColor = tokens.TextSecondary;

        graphsButton.ApplyTheme(tokens);
        closeButton.ApplyTheme(tokens);
        foreach (var tabButton in tabButtons)
        {
            tabButton.ApplyTheme(tokens);
        }

        usageCard.ApplyTheme(tokens);
        fiveHourSection.ApplyTheme(tokens);
        weeklySection.ApplyTheme(tokens);
        tertiarySection.ApplyTheme(tokens);

        if (IsHandleCreated)
        {
            WindowEffects.SetImmersiveDarkMode(Handle, tokens.IsDark);
        }

        Invalidate(true);
    }

    private Font OwnFont(Font font)
    {
        ownedFonts.Add(font);
        return font;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle rectangle, float radius)
    {
        return FluentTheme.RoundedRect(rectangle, radius);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UiSettings.Changed -= OnUiSettingsChanged;
        }

        base.Dispose(disposing);
        if (disposing)
        {
            entranceAnimation?.Dispose();
            entranceAnimation = null;
            foreach (var font in ownedFonts)
            {
                font.Dispose();
            }

            ownedFonts.Clear();
        }
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

    /// <summary>
    /// Label whose GDI+ text is forced to AntiAliasGridFit: the ambient ClearType hint
    /// assumes an opaque destination and produces sub-pixel fringing over the
    /// alpha-composited acrylic backdrop.
    /// </summary>
    private sealed class FluentLabel : Label
    {
        public FluentLabel()
        {
            BackColor = Color.Transparent;
            UseCompatibleTextRendering = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            base.OnPaint(e);
        }
    }

    /// <summary>
    /// The single grouped surface that hosts the usage rows: one rounded card
    /// (CardFill + 1px CardStroke); the rows inside draw their own separators.
    /// </summary>
    private sealed class UsageCardPanel : Panel
    {
        private FluentTokens tokens = FluentTheme.Get(IsSystemDarkTheme(), onBackdrop: false);
        private float layoutScale = 1f;

        public UsageCardPanel()
        {
            BackColor = Color.Transparent;
            DoubleBuffered = true;
        }

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;
            Invalidate(true);
        }

        public void ApplyLayoutScale(float scale)
        {
            layoutScale = Math.Max(1f, scale);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var strokeWidth = Math.Max(1f, layoutScale);
            var bounds = new RectangleF(
                strokeWidth / 2f,
                strokeWidth / 2f,
                Width - strokeWidth,
                Height - strokeWidth);
            using var fillBrush = new SolidBrush(tokens.CardFill);
            using var borderPen = new Pen(tokens.CardStroke, strokeWidth);
            using var path = FluentTheme.RoundedRect(bounds, FluentTheme.CardCornerRadius * layoutScale);

            e.Graphics.FillPath(fillBrush, path);
            e.Graphics.DrawPath(borderPen, path);

            base.OnPaint(e);
        }
    }

    /// <summary>
    /// One usage row inside the grouped card: name + percent on the first line,
    /// a slim 4px meter, then remaining/reset captions. Transparent surface; the
    /// hosting <see cref="UsageCardPanel"/> provides the card chrome.
    /// </summary>
    private sealed class UsageSection : Panel
    {
        private readonly Label nameLabel;
        private readonly Label percentLabel;
        private readonly Label remainingLabel;
        private readonly Label resetLabel;
        private readonly UsageMeterControl meter;
        private readonly Font strongFont = FluentTheme.BodyStrongFont(1f);
        private readonly Font detailFont = FluentTheme.CaptionFont(1f);
        private FluentTokens tokens = FluentTheme.Get(IsSystemDarkTheme(), onBackdrop: false);
        private float layoutScale = 1f;
        private bool showSeparator;

        public UsageSection(string name)
        {
            BackColor = Color.Transparent;
            DoubleBuffered = true;

            nameLabel = new FluentLabel
            {
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Font = strongFont,
                Text = name,
                UseCompatibleTextRendering = true
            };

            percentLabel = new FluentLabel
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = strongFont,
                TextAlign = ContentAlignment.TopRight,
                UseCompatibleTextRendering = true
            };

            meter = new UsageMeterControl
            {
                BackColor = Color.Transparent
            };

            remainingLabel = new FluentLabel
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = detailFont,
                UseCompatibleTextRendering = true
            };

            resetLabel = new FluentLabel
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = detailFont,
                TextAlign = ContentAlignment.TopRight,
                UseCompatibleTextRendering = true
            };

            Controls.Add(nameLabel);
            Controls.Add(percentLabel);
            Controls.Add(meter);
            Controls.Add(remainingLabel);
            Controls.Add(resetLabel);
            ApplyTheme(tokens);
            UpdateChildLayout();
        }

        /// <summary>Draws a full-width 1px CardStroke separator along the row's top edge.</summary>
        public bool ShowSeparator
        {
            get => showSeparator;
            set
            {
                if (showSeparator == value)
                {
                    return;
                }

                showSeparator = value;
                Invalidate();
            }
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

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;

            // The card chrome lives on the parent UsageCardPanel; every surface in
            // the row stays transparent so the backdrop path composites correctly.
            BackColor = Color.Transparent;
            foreach (Control control in Controls)
            {
                control.BackColor = Color.Transparent;
            }

            nameLabel.ForeColor = tokens.TextPrimary;
            percentLabel.ForeColor = tokens.AccentText;
            remainingLabel.ForeColor = tokens.TextSecondary;
            resetLabel.ForeColor = tokens.TextSecondary;

            meter.TrackColor = tokens.MeterTrack;
            meter.AccentColor = tokens.Accent;
            meter.WarningColor = tokens.Warning;
            meter.DangerColor = tokens.Danger;

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
            percentLabel.Text = "--";
            meter.Value = 0;
            remainingLabel.Text = "-- remaining";
            resetLabel.Text = "Reset unknown";
        }

        public void SetLoading(string title)
        {
            nameLabel.Text = title;
            percentLabel.Text = "…";
            meter.Value = 0;
            remainingLabel.Text = "Fetching usage…";
            resetLabel.Text = "Reset pending";
        }

        public void SetUsage(ProviderUsageWindow usage)
        {
            nameLabel.Text = usage.Title;
            percentLabel.Text = $"{usage.UsedPercent:0.#}%";
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
            if (showSeparator)
            {
                var strokeWidth = Math.Max(1f, layoutScale);
                using var separatorPen = new Pen(tokens.CardStroke, strokeWidth);
                var y = strokeWidth / 2f;
                e.Graphics.DrawLine(separatorPen, strokeWidth, y, Width - strokeWidth, y);
            }

            base.OnPaint(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateChildLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                strongFont.Dispose();
                detailFont.Dispose();
            }

            base.Dispose(disposing);
        }

        private void UpdateChildLayout()
        {
            var pad = ScaleInt(16, layoutScale);
            var line1Top = ScaleInt(14, layoutScale);
            var line1Height = ScaleInt(20, layoutScale);
            var percentWidth = ScaleInt(96, layoutScale);
            var meterTop = ScaleInt(42, layoutScale);
            var meterHeight = Math.Max(3, ScaleInt(4, layoutScale));
            var line3Top = ScaleInt(52, layoutScale);
            var line3Height = ScaleInt(16, layoutScale);

            nameLabel.Bounds = new Rectangle(
                pad,
                line1Top,
                Math.Max(ScaleInt(120, layoutScale), Width - (pad * 2) - percentWidth - ScaleInt(8, layoutScale)),
                line1Height);

            percentLabel.Bounds = new Rectangle(
                Math.Max(pad, Width - pad - percentWidth),
                line1Top,
                percentWidth,
                line1Height);

            meter.Bounds = new Rectangle(
                pad,
                meterTop,
                Math.Max(ScaleInt(80, layoutScale), Width - (pad * 2)),
                meterHeight);

            remainingLabel.Bounds = new Rectangle(
                pad,
                line3Top,
                Math.Max(ScaleInt(120, layoutScale), (Width / 2) - pad),
                line3Height);

            resetLabel.Bounds = new Rectangle(
                Width / 2,
                line3Top,
                Math.Max(ScaleInt(120, layoutScale), (Width / 2) - pad),
                line3Height);
        }
    }

    private sealed class ProviderTabButton : Control
    {
        private const string ClaudeSymbolPathData =
            "m19.6 66.5 19.7-11 .3-1-.3-.5h-1l-3.3-.2-11.2-.3L14 53l-9.5-.5-2.4-.5L0 49l.2-1.5 2-1.3 2.9.2 6.3.5 9.5.6 6.9.4L38 49.1h1.6l.2-.7-.5-.4-.4-.4L29 41l-10.6-7-5.6-4.1-3-2-1.5-2-.6-4.2 2.7-3 3.7.3.9.2 3.7 2.9 8 6.1L37 36l1.5 1.2.6-.4.1-.3-.7-1.1L33 25l-6-10.4-2.7-4.3-.7-2.6c-.3-1-.4-2-.4-3l3-4.2L28 0l4.2.6L33.8 2l2.6 6 4.1 9.3L47 29.9l2 3.8 1 3.4.3 1h.7v-.5l.5-7.2 1-8.7 1-11.2.3-3.2 1.6-3.8 3-2L61 2.6l2 2.9-.3 1.8-1.1 7.7L59 27.1l-1.5 8.2h.9l1-1.1 4.1-5.4 6.9-8.6 3-3.5L77 13l2.3-1.8h4.3l3.1 4.7-1.4 4.9-4.4 5.6-3.7 4.7-5.3 7.1-3.2 5.7.3.4h.7l12-2.6 6.4-1.1 7.6-1.3 3.5 1.6.4 1.6-1.4 3.4-8.2 2-9.6 2-14.3 3.3-.2.1.2.3 6.4.6 2.8.2h6.8l12.6 1 3.3 2 1.9 2.7-.3 2-5.1 2.6-6.8-1.6-16-3.8-5.4-1.3h-.8v.4l4.6 4.5 8.3 7.5L89 80.1l.5 2.4-1.3 2-1.4-.2-9.2-7-3.6-3-8-6.8h-.5v.7l1.8 2.7 9.8 14.7.5 4.5-.7 1.4-2.6 1-2.7-.6-5.8-8-6-9-4.7-8.2-.5.4-2.9 30.2-1.3 1.5-3 1.2-2.5-2-1.4-3 1.4-6.2 1.6-8 1.3-6.4 1.2-7.9.7-2.6v-.2H49L43 72l-9 12.3-7.2 7.6-1.7.7-3-1.5.3-2.8L24 86l10-12.8 6-7.9 4-4.6-.1-.5h-.3L17.2 77.4l-4.7.6-2-2 .2-3 1-1 8-5.5Z";
        private const string CursorSymbolPathData =
            "M84.0704 28.9353L51.9066 10.4454C50.8738 9.85153 49.5994 9.85153 48.5666 10.4454L16.4043 28.9353C15.536 29.4345 15 30.3576 15 31.3575V68.6425C15 69.6424 15.536 70.5655 16.4043 71.0647L48.5681 89.5546C49.6009 90.1485 50.8753 90.1485 51.9081 89.5546L84.0719 71.0647C84.9402 70.5655 85.4762 69.6424 85.4762 68.6425V31.3575C85.4762 30.3576 84.9402 29.4345 84.0719 28.9353H84.0704ZM82.0501 32.8519L51.0006 86.4003C50.7907 86.7611 50.2366 86.6138 50.2366 86.1958V51.1329C50.2366 50.4322 49.8606 49.7842 49.2506 49.4324L18.7553 31.9017C18.3929 31.6927 18.5409 31.141 18.9606 31.141H81.0595C81.9414 31.141 82.4925 32.0927 82.0516 32.8534H82.0501V32.8519Z";

        private static readonly string OpenAiWhiteLogoPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "OpenAICodexLogoWhite.png");

        private static readonly string OpenAiBlackLogoPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "OpenAICodexLogoBlack.png");

        private FluentTokens tokens = FluentTheme.Get(IsSystemDarkTheme(), onBackdrop: false);
        private bool hovering;
        private bool pressing;
        private bool selected;

        public ProviderTabButton(string text, string providerKey, UsageProvider provider)
        {
            Text = text;
            ProviderKey = providerKey;
            Provider = provider;
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string ProviderKey { get; }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public UsageProvider Provider { get; }

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

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;
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

            var dpiScale = DeviceDpi / 96f;
            var cornerRadius = FluentTheme.ControlCornerRadius * dpiScale;

            // Idle tabs are fully transparent (acrylic shows through); hover and
            // pressed use the Subtle fills, the selected tab gets ControlFill plus
            // a small accent pill indicator under the icon.
            if (selected)
            {
                using var fillBrush = new SolidBrush(tokens.ControlFill);
                using var borderPen = new Pen(tokens.ControlStroke);
                using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), cornerRadius);
                e.Graphics.FillPath(fillBrush, path);
                e.Graphics.DrawPath(borderPen, path);
            }
            else if (pressing || hovering)
            {
                using var fillBrush = new SolidBrush(pressing ? tokens.SubtlePressed : tokens.SubtleHover);
                using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), cornerRadius);
                e.Graphics.FillPath(fillBrush, path);
            }

            var iconSize = Math.Max(16, Math.Min(Width, Height) - Math.Max(8, Height / 3));
            var iconBounds = new Rectangle(Width / 2 - iconSize / 2, Height / 2 - iconSize / 2, iconSize, iconSize);
            if (Provider == UsageProvider.Claude)
            {
                DrawClaudeLogo(e.Graphics, iconBounds);
            }
            else if (Provider == UsageProvider.Cursor)
            {
                DrawCursorLogo(e.Graphics, iconBounds, tokens.IsDark ? Color.White : tokens.TextPrimary);
            }
            else
            {
                DrawOpenAiLogo(e.Graphics, iconBounds);

                if (!string.Equals(Text, "Codex", StringComparison.OrdinalIgnoreCase))
                {
                    e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using var font = new Font(Font.FontFamily, Math.Max(6f, Font.Size - 2f), FontStyle.Bold);
                    using var brush = new SolidBrush(tokens.TextPrimary);
                    var label = Text.Length <= 2 ? Text : Text[^1..];
                    e.Graphics.DrawString(label, font, brush, Width - ScaleInt(12, dpiScale), Height - ScaleInt(13, dpiScale));
                }
            }

            if (selected)
            {
                DrawSelectionPill(e.Graphics, dpiScale);
            }
        }

        private void DrawSelectionPill(Graphics graphics, float dpiScale)
        {
            var pillWidth = ScaleInt(16, dpiScale);
            var pillHeight = Math.Max(2, ScaleInt(3, dpiScale));
            var pillBounds = new RectangleF(
                (Width - pillWidth) / 2f,
                Height - pillHeight - Math.Max(1, ScaleInt(2, dpiScale)),
                pillWidth,
                pillHeight);
            using var pillBrush = new SolidBrush(tokens.Accent);
            using var pillPath = FluentTheme.RoundedRect(pillBounds, pillHeight / 2f);
            graphics.FillPath(pillBrush, pillPath);
        }

        private void DrawOpenAiLogo(Graphics graphics, Rectangle bounds)
        {
            var preferredPath = tokens.IsDark ? OpenAiWhiteLogoPath : OpenAiBlackLogoPath;
            var fallbackPath = preferredPath == OpenAiBlackLogoPath ? OpenAiWhiteLogoPath : OpenAiBlackLogoPath;

            if ((GetCachedLogo(preferredPath) ?? GetCachedLogo(fallbackPath)) is { } image)
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(image, bounds);
                return;
            }

            var fallbackColor = tokens.IsDark ? Color.White : tokens.TextPrimary;
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

        private static readonly Dictionary<string, Image?> LogoCache = [];

        private static Image? GetCachedLogo(string path)
        {
            // Loaded once per process and kept for the app lifetime; repaint-heavy
            // paths (hover, loading pulse) make per-paint Image.FromFile too costly.
            if (LogoCache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            Image? image = null;
            try
            {
                if (File.Exists(path))
                {
                    image = Image.FromFile(path);
                }
            }
            catch (Exception exception) when (exception is IOException or OutOfMemoryException or UnauthorizedAccessException)
            {
                image = null;
            }

            LogoCache[path] = image;
            return image;
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

        private static void DrawCursorLogo(Graphics graphics, Rectangle bounds, Color color)
        {
            using var brush = new SolidBrush(color);
            try
            {
                using var path = CreateSvgPath(CursorSymbolPathData);
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

    private sealed class GlyphButton : Control
    {
        private readonly string glyph;
        private FluentTokens tokens = FluentTheme.Get(IsSystemDarkTheme(), onBackdrop: false);
        private bool hovering;
        private bool pressing;

        public GlyphButton(string glyph, string accessibleName)
        {
            this.glyph = glyph;
            AccessibleName = accessibleName;
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
        }

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;
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

            // Idle state stays fully transparent so the backdrop shows through.
            if (pressing || hovering)
            {
                using var fillBrush = new SolidBrush(pressing ? tokens.SubtlePressed : tokens.SubtleHover);
                using var path = RoundedPath(
                    new Rectangle(0, 0, Width - 1, Height - 1),
                    FluentTheme.ControlCornerRadius * (DeviceDpi / 96f));
                e.Graphics.FillPath(fillBrush, path);
            }

            using var iconFont = FluentIcons.CreateFont(9f);
            FluentIcons.Draw(
                e.Graphics,
                glyph,
                iconFont,
                tokens.TextSecondary,
                ClientRectangle);
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
    }

    private static class NativeMethods
    {
        public const int WmNclButtonDown = 0x00A1;
        public const int HtCaption = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    }
}
