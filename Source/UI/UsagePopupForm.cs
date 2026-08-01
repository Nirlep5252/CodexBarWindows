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
    private const int ResetRowHeight = 56;
    private const int HeaderButtonRight = 374;
    private const int HeaderButtonPitch = 36;
    private const int HeaderButtonGap = 8;

    /// <summary>
    /// Used-percent above which a reset clearly has something to reset. This only adapts the
    /// wording — redeeming stays available at any usage, because whether a credit is worth
    /// spending is the account holder's call, not ours.
    /// </summary>
    private const double ResetEligibleUsedPercent = 95;

    /// <summary>Window within which an unspent credit is close enough to expiry to flag.</summary>
    private static readonly TimeSpan ResetExpiryWarningWindow = TimeSpan.FromDays(2);

    private readonly Label titleLabel;
    private readonly List<ProviderTabButton> tabButtons = [];
    private readonly List<ProviderDescriptor> providers = [];
    private readonly Dictionary<string, ProviderUsageLookupResult> usageByProvider = [];
    private readonly Label planLabel;
    private readonly Label statusLabel;
    private readonly UsageCardPanel usageCard;
    private readonly List<UsageSection> usageSections = [];
    private readonly ResetCreditSection resetCreditRow;
    private readonly GlyphButton graphsButton;
    private readonly GlyphButton settingsButton;
    private readonly IReadOnlyList<GlyphButton> headerButtons;
    private IReadOnlyList<CodexCliEntry> configuredCodexEntries = [];
    private readonly GlyphButton closeButton;
    private readonly List<Font> ownedFonts = [];
    private UiSettings uiSettings;
    private FluentTokens tokens;
    private IDisposable? entranceAnimation;
    private IDisposable? entranceFade;
    private IDisposable? paletteAnimation;
    // Vibes only: the provider-identity palette currently on screen. During a provider
    // switch this is a blend of the outgoing and incoming palettes; ConfigureProviders
    // snaps it to the selected provider, so the Codex default here never survives long.
    private ProviderVibe blendedVibe = VibeTheme.CodexVibe;
    private bool backdropActive;
    private bool anchorToBottom = true;
    private string selectedProviderKey = CodexProviderKey("default");
    // Keyed per Codex account: two accounts can each have a redeem in flight, and neither
    // may drop the other's guard or overwrite its outcome.
    private readonly HashSet<string> resetCreditBusyKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Message, bool ClearOnNextSnapshot)> resetCreditMessages = new(StringComparer.Ordinal);

    public event EventHandler<string>? SelectedProviderChanged;

    /// <summary>Raised when the user clicks the header history button to open the usage graphs window.</summary>
    public event EventHandler? UsageGraphsRequested;

    /// <summary>Raised when the user clicks the header settings button.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Raised once the user has confirmed spending a specific banked reset credit on a
    /// specific Codex account. Acting on this consumes a real, non-refundable credit.
    /// </summary>
    public event EventHandler<CodexResetRedeemRequest>? ResetCreditRedeemRequested;

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
        // The popup stays open on purpose so changes made in the graphs or settings windows
        // can be watched live side by side.
        graphsButton.Click += (_, _) => UsageGraphsRequested?.Invoke(this, EventArgs.Empty);

        // Hidden: Settings is reached from the tray context menu. The control stays wired up
        // so the header can offer it again by flipping Visible back on.
        settingsButton = new GlyphButton(FluentIcons.Settings, "Settings")
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TabIndex = 2,
            Visible = false
        };
        settingsButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

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
        EnsureUsageSectionCount(3);

        // Lives inside the usage card as its final row, so the flyout stays one grouped
        // surface instead of stacking a second card underneath the first.
        resetCreditRow = new ResetCreditSection { Visible = false };
        resetCreditRow.RedeemConfirmed += OnResetCreditConfirmed;
        resetCreditRow.EnableDragMove();
        usageCard.Controls.Add(resetCreditRow);

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
        Controls.Add(settingsButton);
        Controls.Add(closeButton);

        // Right-to-left packing order for the header chrome. settingsButton is intentionally
        // absent (Settings lives in the tray menu); add it back here to restore it.
        headerButtons = [closeButton, graphsButton];

        EnableDragMove(this);
        EnableDragMove(titleLabel);
        EnableDragMove(planLabel);
        EnableDragMove(statusLabel);
        EnableDragMove(usageCard);

        // Losing focus to another window of THIS process (settings, graphs, combo dropdown
        // flyouts, slider thumbs...) keeps the popup open so changes can be watched live;
        // losing focus to anything else dismisses it like a normal flyout. Form.ActiveForm is
        // NOT usable here: combo dropdowns are top-level non-Form windows, so it reads null
        // mid-interaction and would hide the popup while the user is changing settings. The
        // foreground window's owning process is the reliable signal, checked after the
        // activation change settles (hence BeginInvoke).
        Deactivate += (_, _) => HideIfFocusLeftProcess();
        FormClosing += OnFormClosing;
        UiSettings.Changed += OnUiSettingsChanged;

        ConfigureProviders([
            new ProviderDescriptor(CodexProviderKey("default"), "Codex", UsageProvider.Codex),
            ClaudeProvider,
            GrokProvider,
            CursorProvider]);
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

        if (FluentTheme.VibesActive)
        {
            // Signature-sweep hairline separating the header from the usage card.
            var scale = DpiScale;
            var hairline = new RectangleF(
                ScaleInt(OuterMargin, scale),
                ScaleInt(62, scale),
                Width - (ScaleInt(OuterMargin, scale) * 2),
                Math.Max(2f, 2f * scale));
            using var hairlineBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                RectangleF.Inflate(hairline, 1f, 0f),
                VibeTheme.WithOpacity(blendedVibe.GradientStart, 0.6),
                VibeTheme.WithOpacity(blendedVibe.GradientEnd, 0.6),
                System.Drawing.Drawing2D.LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(hairlineBrush, hairline);
        }
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
            entranceFade?.Dispose();
            entranceFade = null;
            if (paletteAnimation is not null)
            {
                // A palette tween mid-flight when the flyout hides must not keep ticking
                // behind an invisible window; land on the selected provider's identity.
                paletteAnimation.Dispose();
                paletteAnimation = null;
                SetBlendedVibe(VibeTheme.ForProvider(selectedProviderKey));
            }

            if (Opacity < 1d)
            {
                // Only the vibes fade ever lowers Opacity; hiding mid-fade must not leave
                // the window translucent (or stuck layered) on its next open.
                Opacity = 1d;
            }

            // A pending "spend this credit?" confirm must not be waiting one click away
            // the next time the flyout opens, and last session's outcome notes are stale.
            resetCreditRow.Reset();
            resetCreditMessages.Clear();
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

        // The backdrop must be applied BEFORE tokens are resolved: RefreshTheme picks
        // opaque or translucent fills based on backdropActive, and on the first open the
        // flag is still false — cards would paint opaque over a translucent body and the
        // window would look different from every later open.
        if (uiSettings.EffectiveMaterial != BackdropMaterial.Solid)
        {
            ApplyBackdropMaterial();
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

        // Vibes only, and only on the solid-material path: fading Opacity makes the window
        // layered, which would strip the DWM backdrop mid-show. Off-path stays untouched.
        entranceFade?.Dispose();
        entranceFade = null;
        if (Visible && Opacity < 1d)
        {
            // A re-entrant show mid-fade must land fully opaque, not restart from zero.
            Opacity = 1d;
        }

        if (FluentTheme.VibesActive && !Visible && uiSettings.EffectiveMaterial == BackdropMaterial.Solid)
        {
            entranceFade = FluentAnimator.Animate(
                0d,
                1d,
                160,
                opacity =>
                {
                    if (!IsDisposed)
                    {
                        Opacity = opacity;
                    }
                });
        }

        Show();
        Activate();

        if (uiSettings.EffectiveMaterial != BackdropMaterial.Solid)
        {
            // Backdrop was applied before Show; the nudge (which needs a visible window)
            // forces DWM to actually composite it.
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
        // nudgePending makes overlapping nudges safe: without it two interleaved calls each
        // grow by 1px but only restore once, leaving the flyout permanently taller.
        if (!IsHandleCreated || !Visible || !backdropActive || nudgePending)
        {
            return;
        }

        nudgePending = true;
        var size = ClientSize;
        var nudged = new Size(size.Width, size.Height + 1);
        ClientSize = nudged;
        BeginInvoke(new Action(() =>
        {
            nudgePending = false;
            // Only restore if nothing else resized us meanwhile, so a legitimate
            // ApplyScaledLayout that landed in between is not stomped back.
            if (!IsDisposed && IsHandleCreated && ClientSize == nudged)
            {
                ClientSize = size;
            }
        }));
    }

    private bool nudgePending;

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

        var previousSettings = uiSettings;
        uiSettings = UiSettings.Load();

        // A tool being enabled or disabled changes which tabs exist.
        if (previousSettings.CodexEnabled != uiSettings.CodexEnabled ||
            previousSettings.ClaudeEnabled != uiSettings.ClaudeEnabled ||
            previousSettings.GrokEnabled != uiSettings.GrokEnabled ||
            previousSettings.CursorEnabled != uiSettings.CursorEnabled)
        {
            ConfigureCodexEntries(configuredCodexEntries);
        }

        // Re-attaching the DWM backdrop forces a visible recomposition (the window flashes
        // solid, then glassy again), so it only happens when the effective material actually
        // changed. Theme/tint/vibe changes repaint in place without touching the backdrop.
        var materialChanged = appliedMaterial != uiSettings.EffectiveMaterial;
        if (materialChanged)
        {
            ApplyBackdropMaterial();
        }

        RefreshTheme(force: true);
        ApplyScaledLayout();

        // Repaint synchronously so a tint change is not deferred to idle, then force DWM to
        // recomposite. Re-attaching the backdrop attribute is what caused the solid-then-glass
        // flash 536b2fa removed, so that stays gated above — the 1px nudge does not flash.
        Update();
        if (backdropActive && Visible)
        {
            NudgeSizeForBackdrop();
        }
    }

    private BackdropMaterial? appliedMaterial;

    private void ApplyBackdropMaterial()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        appliedMaterial = uiSettings.EffectiveMaterial;

        if (uiSettings.EffectiveMaterial == BackdropMaterial.Solid)
        {
            // Solid keeps the DWM material off and paints an opaque themed body.
            WindowEffects.TryApplyBackdrop(Handle, SystemBackdrop.None);
            backdropActive = false;
            return;
        }

        var backdrop = uiSettings.EffectiveMaterial switch
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
        var rowCount = GetUsageRowCount(selectedProvider);
        EnsureUsageSectionCount(rowCount);

        var rowHeight = ScaleInt(UsageRowHeight, scale);
        var cardTop = ScaleInt(UsageCardTop, scale);
        var gap = ScaleInt(CardGap, scale);
        var showResetCredits = ShouldShowResetCredits(selectedProvider);
        var usageHeight = rowHeight * rowCount;
        var resetHeight = showResetCredits ? ScaleInt(ResetRowHeight, scale) : 0;
        var cardHeight = usageHeight + resetHeight;
        var statusTop = cardTop + cardHeight + gap;
        var clientHeight = statusTop + ScaleInt(StatusHeight, scale) + ScaleInt(BottomMargin, scale);

        SuspendLayout();

        // Pin the bottom edge to wherever the window currently sits rather than to the screen,
        // so switching providers grows/shrinks upward in place. Re-deriving it from the working
        // area instead teleported a flyout the user had dragged elsewhere back down to the
        // taskbar on every tab switch.
        var previousBottom = Bottom;
        var repin = Visible && IsHandleCreated && anchorToBottom;

        ClientSize = new Size(ScaleInt(BaseWidth, scale), clientHeight);
        if (repin)
        {
            Top = previousBottom - Height;
        }

        // Header chrome packs right-to-left so a hidden button yields its slot and the
        // remaining buttons (and the tab strip) close the gap instead of leaving a hole.
        // headerButtons holds only the buttons meant to show: Control.Visible reports the
        // EFFECTIVE visibility, so while the form itself is still hidden (construction and
        // the first layout pass) every child reads false — testing it here left the whole
        // header unpositioned on the first open.
        var slot = HeaderButtonRight;
        foreach (var button in headerButtons)
        {
            button.Bounds = ScaleRect(slot, 14, 32, 32, scale);
            slot -= HeaderButtonPitch;
        }

        var firstTabLeft = LayoutTabButtons(scale, slot + HeaderButtonPitch - HeaderButtonGap);
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
        for (var index = 0; index < usageSections.Count; index++)
        {
            var section = usageSections[index];
            section.Visible = index < rowCount;
            section.Bounds = new Rectangle(0, rowHeight * index, cardWidth, rowHeight);
            section.ApplyLayoutScale(scale);
        }

        resetCreditRow.Visible = showResetCredits;
        if (showResetCredits)
        {
            resetCreditRow.ShowSeparator = rowCount > 0;
            resetCreditRow.Bounds = new Rectangle(0, usageHeight, cardWidth, resetHeight);
            resetCreditRow.ApplyLayoutScale(scale);
        }

        statusLabel.Bounds = new Rectangle(
            ScaleInt(OuterMargin + 4, scale),
            statusTop,
            ScaleInt(BaseWidth - (OuterMargin * 2) - 8, scale),
            ScaleInt(StatusHeight, scale));

        usageCard.ApplyLayoutScale(scale);

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
        configuredCodexEntries = codexEntries;
        var descriptors = codexEntries
            .Select(entry => new ProviderDescriptor(CodexProviderKey(entry.Id), entry.Name, UsageProvider.Codex))
            .Append(ClaudeProvider)
            .Append(GrokProvider)
            .Append(CursorProvider)
            .Where(descriptor => uiSettings.IsProviderEnabled(descriptor.Provider))
            .ToList();

        // Everything disabled would leave a chrome-only flyout with no way back, so the tab
        // strip always keeps at least Codex.
        if (descriptors.Count == 0)
        {
            descriptors.Add(new ProviderDescriptor(CodexProviderKey("default"), "Codex", UsageProvider.Codex));
        }

        ConfigureProviders(descriptors);
    }

    public void UpdateUsage(string providerKey, ProviderUsageLookupResult result)
    {
        var previousSnapshot = FluentTheme.VibesActive ? GetProviderUsage(providerKey).Snapshot : null;
        usageByProvider[providerKey] = result;

        if (result.HasSnapshot &&
            resetCreditMessages.TryGetValue(providerKey, out var pending) &&
            pending.ClearOnNextSnapshot)
        {
            // The post-reset numbers have landed; the row's own inventory is now the report.
            resetCreditMessages.Remove(providerKey);
        }

        if (providerKey == selectedProviderKey)
        {
            ApplyScaledLayout();
            RenderSelectedProvider();
            CelebrateIfReset(previousSnapshot, result.Snapshot);
        }
    }

    /// <summary>
    /// Fires a sparkle burst on the primary meter when a fresh snapshot shows its window
    /// dropping sharply — the signature of a rate-limit window reset landing. Vibes only,
    /// and never on the first snapshot after opening: there is nothing to compare against.
    /// </summary>
    private void CelebrateIfReset(ProviderUsageSnapshot? previous, ProviderUsageSnapshot? current)
    {
        if (!FluentTheme.VibesActive || !Visible || previous is null || current is null)
        {
            return;
        }

        if (previous.Windows.Count == 0 || current.Windows.Count == 0 || usageSections.Count == 0)
        {
            return;
        }

        if (previous.Windows[0].UsedPercent - current.Windows[0].UsedPercent > 15d)
        {
            usageSections[0].Celebrate();
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
            var titles = DefaultUsageTitles(provider);
            for (var index = 0; index < GetUsageRowCount(provider); index++)
            {
                usageSections[index].SetLoading(titles[index]);
            }
            statusLabel.Text = provider.IsClaude
                ? "Reading from Claude Code OAuth..."
                : provider.IsGrok
                    ? "Reading from Grok CLI billing..."
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
        resetCreditRow.Reset();
        TransitionVibePalette(providerKey);
        ApplyScaledLayout();
        RenderSelectedProvider();
        ReplayVisibleMeters();
        SelectedProviderChanged?.Invoke(this, providerKey);
    }

    /// <summary>
    /// Vibes only: glides the on-screen palette from wherever it currently sits to the new
    /// provider's identity, retinting the hairline, tab indicators and percent labels each
    /// tick. While the flyout is hidden there is nothing to glide, so the palette snaps.
    /// </summary>
    private void TransitionVibePalette(string providerKey)
    {
        if (!FluentTheme.VibesActive)
        {
            return;
        }

        paletteAnimation?.Dispose();
        paletteAnimation = null;

        var target = VibeTheme.ForProvider(providerKey);
        if (target == blendedVibe)
        {
            return;
        }

        if (!Visible || !IsHandleCreated)
        {
            SetBlendedVibe(target);
            return;
        }

        // A mid-flight restart tweens from the current blend, not the old provider,
        // so rapid tab clicks glide instead of jumping back.
        var from = blendedVibe;
        paletteAnimation = FluentAnimator.Animate(
            0d,
            1d,
            VibeTheme.PaletteTransitionMs,
            amount =>
            {
                if (!IsDisposed)
                {
                    SetBlendedVibe(VibeTheme.Lerp(from, target, amount));
                }
            });
    }

    /// <summary>Applies the blended palette to every surface that renders it. Vibes only.</summary>
    private void SetBlendedVibe(ProviderVibe vibe)
    {
        blendedVibe = vibe;

        // The hairline is drawn by the form itself; tabs and sections invalidate themselves.
        Invalidate();
        foreach (var tabButton in tabButtons)
        {
            tabButton.VibeAccent = vibe.Accent;
        }

        // Lightened toward white for legibility: the raw accent is tuned for fills,
        // and small percent digits need more contrast against the dark canvas.
        var accentText = VibeTheme.LerpColor(vibe.Accent, Color.White, 0.25);
        foreach (var section in usageSections)
        {
            section.SetVibeAccentText(accentText);
        }
    }

    /// <summary>
    /// Vibes only: re-grows the newly shown provider's meters from zero so the bars sweep
    /// in wearing the incoming identity. Skipped while hidden — ReplayFromZero would start
    /// timers nothing can see.
    /// </summary>
    private void ReplayVisibleMeters()
    {
        if (!FluentTheme.VibesActive || !Visible)
        {
            return;
        }

        foreach (var section in usageSections)
        {
            if (section.Visible)
            {
                section.ReplayMeter();
            }
        }
    }

    private void RenderSelectedProvider()
    {
        foreach (var tabButton in tabButtons)
        {
            tabButton.Selected = tabButton.ProviderKey == selectedProviderKey;
        }

        var provider = GetProvider(selectedProviderKey);
        if (FluentTheme.VibesActive)
        {
            // Meters wear the target identity outright (not the blend): they are only
            // visible for the selected provider, so there is nothing to cross-fade from.
            var vibe = VibeTheme.ForProvider(selectedProviderKey);
            foreach (var section in usageSections)
            {
                section.SetVibePalette(vibe);
            }
        }

        titleLabel.Text = $"{provider.Name} rate limits";
        var result = GetProviderUsage(selectedProviderKey);
        RenderResetCredits(provider);

        if (result.Snapshot is not { } snapshot)
        {
            planLabel.Text = provider.IsCursor
                ? "Waiting for Cursor usage data"
                : provider.IsGrok
                    ? "Waiting for Grok usage data"
                    : $"Waiting for local {provider.Name} usage data";
            var titles = DefaultUsageTitles(provider);
            for (var index = 0; index < GetUsageRowCount(provider); index++)
            {
                usageSections[index].SetUnavailable(titles[index]);
            }
            statusLabel.Text = result.Error ?? "No usage data found.";
            return;
        }

        planLabel.Text = provider.IsCursor
            ? CursorPlanText(snapshot)
            : provider.IsGrok
                ? GrokPlanText(snapshot)
                : string.IsNullOrWhiteSpace(snapshot.PlanType)
                    ? provider.IsClaude ? "Claude Code usage data" : "Codex CLI usage data"
                    : $"{ProviderPlanFormatter.DisplayName(provider.Provider, snapshot.PlanType)} plan";

        var windows = snapshot.Windows;
        for (var index = 0; index < windows.Count; index++)
        {
            usageSections[index].SetUsage(windows[index]);
        }

        // Subtle freshness line. A retained (stale) snapshot says so explicitly, so an error
        // paired with old numbers cannot read as if the numbers were just fetched.
        var fetched = $"Updated {FormatObservedAt(snapshot.ObservedAt)}";
        var baseStatus = provider.IsCursor
            ? CursorStatusText(snapshot)
            : provider.IsGrok
                ? GrokStatusText(snapshot)
                : string.Empty;
        statusLabel.Text = !string.IsNullOrWhiteSpace(result.Error)
            ? result.IsStale
                ? $"{result.Error} · showing limits from {FormatObservedAt(snapshot.ObservedAt)}"
                : result.Error
            : string.IsNullOrWhiteSpace(baseStatus) ? fetched : $"{baseStatus} · {fetched}";
    }

    private ProviderUsageLookupResult GetProviderUsage(string providerKey)
    {
        return usageByProvider.TryGetValue(providerKey, out var result)
            ? result
            : new ProviderUsageLookupResult(null, "Usage has not been loaded yet.");
    }

    /// <summary>
    /// Marks a Codex account's reset redemption as in flight, so its row stays visible and
    /// non-interactive across the refreshes the redeem triggers.
    /// </summary>
    public void SetResetCreditBusy(string providerKey)
    {
        resetCreditBusyKeys.Add(providerKey);
        resetCreditMessages.Remove(providerKey);
        ApplyScaledLayout();
        RenderSelectedProvider();
    }

    /// <summary>Reports the outcome of a finished redemption on the owning account's row.</summary>
    /// <param name="clearOnNextSnapshot">
    /// True when refreshed usage tells the story better than the message does — the case after a
    /// reset actually lands. False keeps an explanation on screen that the numbers cannot give.
    /// </param>
    public void SetResetCreditMessage(string providerKey, string message, bool clearOnNextSnapshot = false)
    {
        resetCreditBusyKeys.Remove(providerKey);
        resetCreditMessages[providerKey] = (message, clearOnNextSnapshot);
        ApplyScaledLayout();
        RenderSelectedProvider();

        // clearOnNextSnapshot is only true when a redeem genuinely landed, which makes it
        // the one clean success signal worth a moment of celebration.
        if (FluentTheme.VibesActive && clearOnNextSnapshot && Visible &&
            providerKey == selectedProviderKey && resetCreditRow.Visible)
        {
            resetCreditRow.Celebrate();
        }
    }

    private CodexResetCredits? GetResetCredits(string providerKey)
    {
        return GetProviderUsage(providerKey).Snapshot?.ResetCredits;
    }

    private bool ShouldShowResetCredits(ProviderDescriptor provider)
    {
        if (provider.Provider != UsageProvider.Codex)
        {
            return false;
        }

        return resetCreditBusyKeys.Contains(provider.Key) ||
               resetCreditMessages.ContainsKey(provider.Key) ||
               GetResetCredits(provider.Key) is { HasAny: true };
    }

    private void RenderResetCredits(ProviderDescriptor provider)
    {
        if (!ShouldShowResetCredits(provider))
        {
            return;
        }

        if (resetCreditBusyKeys.Contains(provider.Key))
        {
            resetCreditRow.ShowBusy();
            return;
        }

        var credits = GetResetCredits(provider.Key) ?? CodexResetCredits.None;
        var message = resetCreditMessages.TryGetValue(provider.Key, out var pending)
            ? pending.Message
            : null;

        resetCreditRow.ShowInventory(credits, IsNearLimit(provider.Key), message);
    }

    /// <summary>
    /// Whether any usage window is close enough to exhaustion for a reset to have something
    /// to reset. Every window matters: the weekly cap blocks work just as the 5 hour one does.
    /// </summary>
    private bool IsNearLimit(string providerKey)
    {
        return GetProviderUsage(providerKey).Snapshot is { } snapshot &&
               snapshot.Windows.Any(window => window.UsedPercent >= ResetEligibleUsedPercent);
    }

    private void OnResetCreditConfirmed(object? sender, CodexResetCredit credit)
    {
        var providerKey = selectedProviderKey;

        if (resetCreditBusyKeys.Contains(providerKey))
        {
            return;
        }

        // Re-check against the snapshot the row was rendered from: a refresh may have landed
        // between render and confirm, and the credit must belong to the account being charged.
        if (GetProvider(providerKey).Provider != UsageProvider.Codex ||
            GetResetCredits(providerKey)?.Find(credit.Id) is null)
        {
            SetResetCreditMessage(providerKey, "That reset is no longer available. Refreshing…");
            return;
        }

        SetResetCreditBusy(providerKey);
        ResetCreditRedeemRequested?.Invoke(this, new CodexResetRedeemRequest(providerKey, credit));
    }

    private int GetUsageRowCount(ProviderDescriptor provider)
    {
        return GetProviderUsage(provider.Key).Snapshot is { } snapshot
            ? snapshot.Windows.Count
            : provider.IsCursor ? 3 : provider.IsGrok ? 1 : 2;
    }

    private static IReadOnlyList<string> DefaultUsageTitles(ProviderDescriptor provider)
    {
        return provider.IsCursor
            ? ["Total", "Auto", "API"]
            : provider.IsGrok
                ? ["Weekly limit"]
                : ["5 hour limit", "Weekly limit"];
    }

    private void EnsureUsageSectionCount(int count)
    {
        while (usageSections.Count < count)
        {
            var section = new UsageSection("Usage limit")
            {
                ShowSeparator = usageSections.Count > 0
            };
            section.EnableDragMove();
            section.ApplyTheme(tokens);
            usageSections.Add(section);
            usageCard.Controls.Add(section);
        }
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

        if (FluentTheme.VibesActive)
        {
            // The tab buttons were just recreated (and the selection may have moved), so
            // snap — never animate — the palette onto the fresh chrome.
            paletteAnimation?.Dispose();
            paletteAnimation = null;
            SetBlendedVibe(VibeTheme.ForProvider(selectedProviderKey));
        }

        ApplyScaledLayout();
        RenderSelectedProvider();
    }

    private int LayoutTabButtons(float scale, int rightEdge)
    {
        var right = ScaleInt(rightEdge, scale);
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
        settingsButton.BringToFront();
        closeButton.BringToFront();
        return firstLeft;
    }

    public static string CodexProviderKey(string id)
    {
        return $"codex:{id}";
    }

    public const string ClaudeProviderKey = "claude";
    public const string GrokProviderKey = "grok";
    public const string CursorProviderKey = "cursor";
    private static readonly ProviderDescriptor ClaudeProvider = new(ClaudeProviderKey, "Claude", UsageProvider.Claude);
    private static readonly ProviderDescriptor GrokProvider = new(GrokProviderKey, "Grok", UsageProvider.Grok);
    private static readonly ProviderDescriptor CursorProvider = new(CursorProviderKey, "Cursor", UsageProvider.Cursor);
    private sealed record ProviderDescriptor(string Key, string Name, UsageProvider Provider)
    {
        public bool IsClaude => Provider == UsageProvider.Claude;
        public bool IsGrok => Provider == UsageProvider.Grok;
        public bool IsCursor => Provider == UsageProvider.Cursor;
    }

    /// <summary>
    /// Dismisses the flyout when focus has left this process entirely.
    /// </summary>
    /// <remarks>
    /// The popup's own <see cref="Form.Deactivate"/> only fires while the popup is the active
    /// window, so once focus moves to a sibling window (settings, graphs) the popup goes inactive
    /// and never hears about focus changes again — leaving a topmost flyout stranded over
    /// unrelated applications, which also kept the refresh timer alive. Those siblings therefore
    /// call this on their own deactivation to re-arm the check.
    /// </remarks>
    public void HideIfFocusLeftProcess()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        // Checked after the activation change settles, so the new foreground window is known.
        BeginInvoke(new Action(() =>
        {
            if (!IsDisposed && Visible && !ForegroundWindowBelongsToThisProcess())
            {
                Hide();
            }
        }));
    }

    private static bool ForegroundWindowBelongsToThisProcess()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private Point CalculateLocation(Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        var workingArea = screen.WorkingArea;

        var x = Math.Clamp(anchor.X - Width + 20, workingArea.Left + 8, workingArea.Right - Width - 8);
        anchorToBottom = anchor.Y >= workingArea.Top + (workingArea.Height / 2);
        // Hug the tray: the bottom edge sits just above the taskbar and the window is only
        // as tall as the current provider needs, so switching tabs moves the top edge.
        // Reserving room for the tallest provider instead left the flyout floating far from
        // the tray whenever the selected provider was shorter than the tallest one.
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

    private static string CursorPlanText(ProviderUsageSnapshot snapshot)
    {
        var plan = string.IsNullOrWhiteSpace(snapshot.PlanType) ? "Cursor usage data" : snapshot.PlanType;
        return string.IsNullOrWhiteSpace(snapshot.AccountEmail)
            ? plan
            : $"{plan} · {snapshot.AccountEmail}";
    }

    private static string GrokPlanText(ProviderUsageSnapshot snapshot)
    {
        // Never show account email — privacy and clutter. Tier only when the API reports one.
        return string.IsNullOrWhiteSpace(snapshot.PlanType)
            ? "Grok usage data"
            : $"{ProviderPlanFormatter.DisplayName(UsageProvider.Grok, snapshot.PlanType)} plan";
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

    private static string GrokStatusText(ProviderUsageSnapshot snapshot)
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
        settingsButton.ApplyTheme(tokens);
        closeButton.ApplyTheme(tokens);
        foreach (var tabButton in tabButtons)
        {
            tabButton.ApplyTheme(tokens);
        }

        usageCard.ApplyTheme(tokens);
        foreach (var section in usageSections)
        {
            section.ApplyTheme(tokens);
        }

        resetCreditRow.ApplyTheme(tokens);

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
            entranceFade?.Dispose();
            entranceFade = null;
            paletteAnimation?.Dispose();
            paletteAnimation = null;
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
        private SparkleField? sparkles;
        private bool cascadingInvalidate;
        private Color? vibeAccentText;
        private double? lastUsedPercent;

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
            percentLabel.ForeColor = ResolvePercentColor();
            remainingLabel.ForeColor = tokens.TextSecondary;
            resetLabel.ForeColor = tokens.TextSecondary;

            meter.TrackColor = tokens.MeterTrack;
            meter.AccentColor = tokens.Accent;
            meter.WarningColor = tokens.Warning;
            meter.DangerColor = tokens.Danger;
            // Vibes may have just been toggled, which gates the danger pulse without changing the
            // meter's value or visibility — the events that otherwise re-evaluate it.
            meter.RefreshAnimationState();

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
            lastUsedPercent = null;
            meter.Value = 0;
            remainingLabel.Text = "-- remaining";
            resetLabel.Text = "Reset unknown";
        }

        public void SetLoading(string title)
        {
            nameLabel.Text = title;
            percentLabel.Text = "…";
            lastUsedPercent = null;
            meter.Value = 0;
            remainingLabel.Text = "Fetching usage…";
            resetLabel.Text = "Reset pending";
        }

        /// <summary>Vibes only: the provider-identity gradient the meter fills with.</summary>
        public void SetVibePalette(ProviderVibe? palette)
        {
            meter.VibePalette = palette;
        }

        /// <summary>
        /// Vibes only: retints the percent figure with the blended provider accent so the
        /// number glides between identities alongside the rest of the chrome.
        /// </summary>
        public void SetVibeAccentText(Color? accent)
        {
            if (vibeAccentText == accent)
            {
                return;
            }

            vibeAccentText = accent;
            percentLabel.ForeColor = ResolvePercentColor();
        }

        /// <summary>
        /// Vibes: the number itself carries limit heat — amber from 70%, red from 90% — a cue
        /// independent of the provider hue. Off: the stock accent text, untouched.
        /// </summary>
        private Color ResolvePercentColor()
        {
            if (!FluentTheme.VibesActive)
            {
                return tokens.AccentText;
            }

            return lastUsedPercent switch
            {
                { } percent when percent >= 90 => VibeTheme.HeatDanger,
                { } percent when percent >= 70 => VibeTheme.HeatWarn,
                _ => vibeAccentText ?? tokens.AccentText
            };
        }

        /// <summary>Vibes only: re-grows the meter from zero (no-op when vibes are off).</summary>
        public void ReplayMeter()
        {
            meter.ReplayFromZero();
        }

        /// <summary>Vibes-only sparkle burst at the meter's far end, e.g. when a window resets.</summary>
        public void Celebrate()
        {
            if (!FluentTheme.VibesActive || !IsHandleCreated || !Visible)
            {
                return;
            }

            sparkles ??= new SparkleField(this);
            sparkles.Burst(new PointF(
                meter.Right - ScaleInt(12, layoutScale),
                meter.Top + (meter.Height / 2f)));
        }

        public void SetUsage(ProviderUsageWindow usage)
        {
            nameLabel.Text = usage.Title;
            percentLabel.Text = $"{usage.UsedPercent:0.#}%";
            lastUsedPercent = usage.UsedPercent;
            if (FluentTheme.VibesActive)
            {
                percentLabel.ForeColor = ResolvePercentColor();
            }

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
            sparkles?.Render(e.Graphics);
        }

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            base.OnInvalidated(e);

            // While sparkles animate over the row, the transparent child labels must
            // repaint too or they hold stale particle frames. Their Invalidate bounces
            // back here through the transparency simulation, hence the guard.
            if (!cascadingInvalidate && sparkles is { IsActive: true })
            {
                cascadingInvalidate = true;
                try
                {
                    foreach (Control child in Controls)
                    {
                        child.Invalidate();
                    }
                }
                finally
                {
                    cascadingInvalidate = false;
                }
            }
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
                sparkles?.Dispose();
                sparkles = null;
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

    /// <summary>
    /// The banked-reset row, rendered as the usage card's final row: an accent count badge and
    /// inventory on the left, the redeem action on the right. Spending a credit is irreversible,
    /// so the action always takes a second deliberate click through an inline confirm — a modal
    /// would blur the popup and dismiss it.
    /// </summary>
    private sealed class ResetCreditSection : Panel
    {
        private readonly Label titleLabel;
        private readonly Label detailLabel;
        private readonly PillButton redeemButton;
        private readonly PillButton confirmButton;
        private readonly PillButton cancelButton;
        private readonly Font strongFont = FluentTheme.BodyStrongFont(1f);
        private readonly Font detailFont = FluentTheme.CaptionFont(1f);
        private FluentTokens tokens = FluentTheme.Get(IsSystemDarkTheme(), onBackdrop: false);
        private float layoutScale = 1f;
        private CodexResetCredit? selectedCredit;
        private CodexResetCredits inventory = CodexResetCredits.None;
        private string? outcomeMessage;
        private bool nearLimit;
        private bool expiringSoon;
        private bool confirming;
        private bool busy;
        private bool showSeparator;
        private bool showBadge = true;
        private SparkleField? sparkles;
        private bool cascadingInvalidate;

        /// <summary>Raised only after the user confirms; the argument is the credit to spend.</summary>
        public event EventHandler<CodexResetCredit>? RedeemConfirmed;

        public ResetCreditSection()
        {
            BackColor = Color.Transparent;
            DoubleBuffered = true;

            titleLabel = new FluentLabel
            {
                AutoSize = false,
                AutoEllipsis = true,
                Font = strongFont
            };

            detailLabel = new FluentLabel
            {
                AutoSize = false,
                AutoEllipsis = true,
                Font = detailFont
            };

            // Neutral, not accent: this opens a confirm rather than committing, and a loud
            // primary button next to an irreversible spend invites the click we do not want.
            redeemButton = new PillButton("Use reset");
            redeemButton.Click += (_, _) =>
            {
                if (selectedCredit is not null)
                {
                    confirming = true;
                    UpdateContent();
                }
            };

            confirmButton = new PillButton("Use it") { Accent = true, Visible = false };
            confirmButton.Click += (_, _) =>
            {
                if (selectedCredit is { } credit)
                {
                    RedeemConfirmed?.Invoke(this, credit);
                }
            };

            cancelButton = new PillButton("Cancel") { Visible = false };
            cancelButton.Click += (_, _) =>
            {
                confirming = false;
                UpdateContent();
            };

            Controls.Add(titleLabel);
            Controls.Add(detailLabel);
            Controls.Add(redeemButton);
            Controls.Add(confirmButton);
            Controls.Add(cancelButton);
            ApplyTheme(tokens);
        }

        /// <summary>Drops any half-finished confirm, e.g. when the user switches account tabs.</summary>
        public void Reset()
        {
            if (IsDisposed || !confirming)
            {
                return;
            }

            confirming = false;
            UpdateContent();
        }

        /// <summary>
        /// Lets the row's chrome drag the flyout like the rest of the popup. The buttons are
        /// deliberately excluded so a click on them can never be swallowed by a drag.
        /// </summary>
        public void EnableDragMove()
        {
            HandleCreated += (_, _) =>
            {
                if (FindForm() is not UsagePopupForm popup)
                {
                    return;
                }

                popup.EnableDragMove(this);
                popup.EnableDragMove(titleLabel);
                popup.EnableDragMove(detailLabel);
            };
        }

        /// <summary>Draws the same 1px separator the usage rows use along the row's top edge.</summary>
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

        public void ShowInventory(CodexResetCredits credits, bool isNearLimit, string? message)
        {
            var previous = selectedCredit;
            busy = false;
            inventory = credits;
            nearLimit = isNearLimit;
            outcomeMessage = message;
            selectedCredit = credits.NextExpiring;
            expiringSoon = selectedCredit?.ExpiresAt is { } expiry &&
                           expiry - DateTimeOffset.Now <= ResetExpiryWarningWindow;

            // A refresh that replaces the offered credit must not leave a confirm on screen
            // naming one credit while a different one would actually be spent.
            if (confirming && (selectedCredit is null || previous?.Id != selectedCredit.Id))
            {
                confirming = false;
            }

            UpdateContent();
        }

        public void ShowBusy()
        {
            confirming = false;
            busy = true;
            showBadge = false;
            titleLabel.Text = "Applying reset…";
            detailLabel.Text = "Asking Codex to redeem this credit";
            detailLabel.ForeColor = tokens.TextSecondary;
            redeemButton.Visible = false;
            confirmButton.Visible = false;
            cancelButton.Visible = false;
            UpdateChildLayout();
            Invalidate();
        }

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;

            BackColor = Color.Transparent;
            titleLabel.BackColor = Color.Transparent;
            detailLabel.BackColor = Color.Transparent;
            titleLabel.ForeColor = tokens.TextPrimary;

            redeemButton.ApplyTheme(tokens);
            confirmButton.ApplyTheme(tokens);
            cancelButton.ApplyTheme(tokens);

            // Re-derives the detail colour, which is warning-tinted in some states.
            UpdateContent();
            Invalidate(true);
        }

        public void ApplyLayoutScale(float scale)
        {
            layoutScale = Math.Max(1f, scale);
            UpdateChildLayout();
        }

        /// <summary>Vibes-only sparkle burst over the row when a redeem genuinely lands.</summary>
        public void Celebrate()
        {
            if (!FluentTheme.VibesActive || !IsHandleCreated || !Visible)
            {
                return;
            }

            sparkles ??= new SparkleField(this);
            sparkles.Burst(new PointF(ScaleInt(40, layoutScale), Height / 2f));
        }

        private void UpdateContent()
        {
            if (busy)
            {
                // A refresh or theme change must not pull the in-flight state off screen.
                ShowBusy();
                return;
            }

            if (confirming && selectedCredit is { } pending)
            {
                showBadge = false;
                titleLabel.Text = $"Use \"{pending.DisplayTitle}\"?";

                // Below the eligibility mark the spend may reset nothing, which is the one
                // thing worth saying at the moment of commitment.
                detailLabel.Text = nearLimit
                    ? "This can't be undone"
                    : "Nothing's near a limit — may reset nothing";
                detailLabel.ForeColor = nearLimit ? tokens.TextSecondary : tokens.Warning;

                redeemButton.Visible = false;
                confirmButton.Visible = true;
                cancelButton.Visible = true;
                UpdateChildLayout();
                return;
            }

            showBadge = true;
            titleLabel.Text = inventory.AvailableCount == 1 ? "reset available" : "resets available";
            detailLabel.Text = outcomeMessage ?? DescribeInventory();
            detailLabel.ForeColor = outcomeMessage is null && expiringSoon
                ? tokens.Warning
                : tokens.TextSecondary;

            redeemButton.Visible = true;
            confirmButton.Visible = false;
            cancelButton.Visible = false;

            // Spending below the eligibility mark is the account holder's call, so the only
            // thing that can disable this is having no id to charge.
            redeemButton.Enabled = selectedCredit is not null;
            redeemButton.AccessibleDescription = "Spend one banked reset to clear this account's usage windows";
            UpdateChildLayout();
        }

        private string DescribeInventory()
        {
            if (selectedCredit is null)
            {
                // Count without detail rows: there is no id to charge, so redeeming has to
                // happen in the Codex CLI rather than here.
                return "Redeem these from the Codex CLI";
            }

            return selectedCredit.ExpiresAt is { } expiry
                ? $"Next expires {FormatExpiry(expiry)}"
                : "These don't expire";
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                strongFont.Dispose();
                detailFont.Dispose();
                sparkles?.Dispose();
                sparkles = null;
            }

            base.Dispose(disposing);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateChildLayout();
        }

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            base.OnInvalidated(e);

            // Same cascade the usage rows use: transparent children must repaint while
            // sparkles animate, and their Invalidate bounces back here, hence the guard.
            if (!cascadingInvalidate && sparkles is { IsActive: true })
            {
                cascadingInvalidate = true;
                try
                {
                    foreach (Control child in Controls)
                    {
                        child.Invalidate();
                    }
                }
                finally
                {
                    cascadingInvalidate = false;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (showSeparator)
            {
                var strokeWidth = Math.Max(1f, layoutScale);
                using var separatorPen = new Pen(tokens.CardStroke, strokeWidth);
                var y = strokeWidth / 2f;
                e.Graphics.DrawLine(separatorPen, strokeWidth, y, Width - strokeWidth, y);
            }

            if (showBadge && BadgeBounds() is { Width: > 0 } badge)
            {
                // A WinUI-style accent count badge: the one place a saturated colour reads as
                // information rather than decoration, and it keeps the number off the button.
                var fill = expiringSoon ? tokens.Warning : tokens.Accent;
                using var badgeBrush = new SolidBrush(fill);
                using var badgePath = FluentTheme.RoundedRect(badge, badge.Height / 2f);
                e.Graphics.FillPath(badgeBrush, badgePath);

                TextRenderer.DrawText(
                    e.Graphics,
                    BadgeText(),
                    detailFont,
                    badge,
                    ContrastingTextOn(fill),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            base.OnPaint(e);
            sparkles?.Render(e.Graphics);
        }

        /// <summary>Picks black or white text for a badge fill the user's accent may have made bright.</summary>
        private static Color ContrastingTextOn(Color fill)
        {
            var luminance = ((0.2126f * fill.R) + (0.7152f * fill.G) + (0.0722f * fill.B)) / 255f;
            return luminance > 0.6f ? Color.FromArgb(0xF2, 0, 0, 0) : Color.White;
        }

        private string BadgeText()
        {
            return inventory.AvailableCount > 99 ? "99+" : inventory.AvailableCount.ToString();
        }

        private Rectangle BadgeBounds()
        {
            if (!showBadge)
            {
                return Rectangle.Empty;
            }

            var height = ScaleInt(18, layoutScale);
            var baseWidth = BadgeText().Length switch
            {
                <= 1 => 20,
                2 => 26,
                _ => 32
            };

            return new Rectangle(
                ScaleInt(16, layoutScale),
                ScaleInt(10, layoutScale),
                ScaleInt(baseWidth, layoutScale),
                height);
        }

        private void UpdateChildLayout()
        {
            var pad = ScaleInt(16, layoutScale);
            var buttonHeight = ScaleInt(28, layoutScale);
            var buttonTop = Math.Max(0, (Height - buttonHeight) / 2);
            var buttonGap = ScaleInt(6, layoutScale);

            var right = Width - pad;
            if (confirmButton.Visible)
            {
                var cancelWidth = ScaleInt(62, layoutScale);
                var confirmWidth = ScaleInt(58, layoutScale);
                cancelButton.Bounds = new Rectangle(right - cancelWidth, buttonTop, cancelWidth, buttonHeight);
                confirmButton.Bounds = new Rectangle(
                    right - cancelWidth - buttonGap - confirmWidth,
                    buttonTop,
                    confirmWidth,
                    buttonHeight);
                right = confirmButton.Left;
            }
            else if (redeemButton.Visible)
            {
                var redeemWidth = ScaleInt(86, layoutScale);
                redeemButton.Bounds = new Rectangle(right - redeemWidth, buttonTop, redeemWidth, buttonHeight);
                right = redeemButton.Left;
            }

            var badge = BadgeBounds();
            var textLeft = badge.Width > 0 ? badge.Right + ScaleInt(8, layoutScale) : pad;
            var textWidth = Math.Max(ScaleInt(80, layoutScale), right - textLeft - ScaleInt(8, layoutScale));

            titleLabel.Bounds = new Rectangle(textLeft, ScaleInt(10, layoutScale), textWidth, ScaleInt(18, layoutScale));
            detailLabel.Bounds = new Rectangle(pad, ScaleInt(30, layoutScale), Math.Max(ScaleInt(80, layoutScale), right - pad - ScaleInt(8, layoutScale)), ScaleInt(16, layoutScale));
            Invalidate();
        }
    }

    /// <summary>Compact Fluent-styled text button used inside the reset-credit row.</summary>
    private sealed class PillButton : Control
    {
        private readonly Font font = FluentTheme.CaptionFont(1f);
        private FluentTokens tokens = FluentTheme.Get(IsSystemDarkTheme(), onBackdrop: false);
        private bool hovering;
        private bool pressing;

        public PillButton(string text)
        {
            Text = text;
            AccessibleName = text;
            AccessibleRole = AccessibleRole.PushButton;
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

        /// <summary>Draws as the accent (primary) button rather than a neutral one.</summary>
        public bool Accent { get; init; }

        public void ApplyTheme(FluentTokens palette)
        {
            tokens = palette;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            hovering = false;
            pressing = false;
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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && e.KeyCode is Keys.Enter or Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var scale = DeviceDpi / 96f;
            var strokeWidth = Math.Max(1f, scale);
            var bounds = new RectangleF(
                strokeWidth / 2f,
                strokeWidth / 2f,
                Width - strokeWidth,
                Height - strokeWidth);
            using var path = FluentTheme.RoundedRect(bounds, FluentTheme.ControlCornerRadius * scale);

            var (fill, text) = ResolveColors();
            using var fillBrush = new SolidBrush(fill);
            e.Graphics.FillPath(fillBrush, path);

            if (!Accent || !Enabled)
            {
                using var borderPen = new Pen(Enabled ? tokens.ControlStroke : tokens.ControlFillDisabled, strokeWidth);
                e.Graphics.DrawPath(borderPen, path);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                font,
                ClientRectangle,
                text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private (Color Fill, Color Text) ResolveColors()
        {
            if (!Enabled)
            {
                return (tokens.ControlFillDisabled, tokens.TextDisabled);
            }

            if (Accent)
            {
                var fill = pressing ? tokens.AccentPressed : hovering ? tokens.AccentHover : tokens.Accent;
                return (fill, tokens.TextOnAccent);
            }

            var neutral = pressing
                ? tokens.ControlFillPressed
                : hovering ? tokens.ControlFillHover : tokens.ControlFill;
            return (neutral, tokens.TextPrimary);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                font.Dispose();
            }

            base.Dispose(disposing);
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
        private Color? vibeAccent;

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

        /// <summary>
        /// Vibes only: the blended provider accent for the selection indicator. The form
        /// retints this every transition tick; unselected tabs never render it.
        /// </summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color? VibeAccent
        {
            get => vibeAccent;
            set
            {
                if (vibeAccent == value)
                {
                    return;
                }

                vibeAccent = value;
                if (selected)
                {
                    Invalidate();
                }
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
            else if (Provider == UsageProvider.Grok)
            {
                DrawGrokLogo(e.Graphics, iconBounds, tokens.IsDark ? Color.White : tokens.TextPrimary);
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
            var pillColor = FluentTheme.VibesActive && vibeAccent is { } vibe ? vibe : tokens.Accent;
            using var pillBrush = new SolidBrush(pillColor);
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

        private static void DrawGrokLogo(Graphics graphics, Rectangle bounds, Color color)
        {
            // Match the WinUI mark: a thinner geometric X, not a heavy bar.
            var inset = Math.Max(2.5f, bounds.Width * 0.22f);
            var thickness = Math.Max(1.6f, bounds.Width * 0.11f);
            var left = bounds.Left + inset;
            var top = bounds.Top + inset;
            var right = bounds.Right - inset;
            var bottom = bounds.Bottom - inset;
            using var pen = new Pen(color, thickness)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };
            graphics.DrawLine(pen, left, top, right, bottom);
            graphics.DrawLine(pen, right, top, left, bottom);
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
