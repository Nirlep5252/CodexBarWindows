using System.ComponentModel;

namespace CodexBarWindows;

/// <summary>
/// Slim Windows 11 style progress meter: a 4px fully-rounded track with an accent fill that
/// shifts to warning/danger at 70%/90% and animates value changes over 250ms.
/// </summary>
public sealed class UsageMeterControl : Control
{
    private double targetValue;
    private double displayedValue;
    private IDisposable? valueAnimation;
    private IDisposable? pulseAnimation;
    private double pulseStrength;
    private bool vibeRevealDone;
    private ProviderVibe? vibePalette;
    private System.Windows.Forms.Timer? dangerPulseTimer;
    private double dangerPhase;
    private Color trackColor = Color.FromArgb(237, 242, 247);
    private Color accentColor = Color.FromArgb(0, 95, 184);
    private Color warningColor = Color.FromArgb(202, 80, 16);
    private Color dangerColor = Color.FromArgb(196, 43, 28);

    public UsageMeterControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);

        Height = 4;
    }

    [DefaultValue(0d)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public double Value
    {
        get => targetValue;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            valueAnimation?.Dispose();
            valueAnimation = null;
            targetValue = clamped;

            if (!IsHandleCreated || !Visible || displayedValue == clamped)
            {
                displayedValue = clamped;
                Invalidate();
                return;
            }

            if (FluentTheme.VibesActive)
            {
                AnimateVibe(displayedValue, clamped, 450);
                UpdateDangerPulse();
                return;
            }

            valueAnimation = FluentAnimator.Animate(
                displayedValue,
                clamped,
                250,
                animated =>
                {
                    displayedValue = animated;
                    Invalidate();
                });
        }
    }

    /// <summary>
    /// Sets the value without any animation — used by interactive scrubbing (the settings
    /// limit preview) where the fill must track the pointer exactly.
    /// </summary>
    public void SetValueImmediate(double value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        valueAnimation?.Dispose();
        valueAnimation = null;
        vibeRevealDone = true;
        targetValue = clamped;
        displayedValue = clamped;
        UpdateDangerPulse();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateDangerPulse();
        if (!Visible)
        {
            vibeRevealDone = false;
            return;
        }

        // First appearance with vibes on: grow the fill from zero so opening the window
        // plays the reveal, then pulse when the value lands.
        if (FluentTheme.VibesActive && !vibeRevealDone && targetValue > 0 && IsHandleCreated)
        {
            ReplayFromZero();
        }
    }

    /// <summary>
    /// Vibes-only provider identity palette for the gradient fill; null uses the signature
    /// sweep. Ignored entirely while vibes are off.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ProviderVibe? VibePalette
    {
        get => vibePalette;
        set
        {
            vibePalette = value;
            if (FluentTheme.VibesActive)
            {
                Invalidate();
            }
        }
    }

    /// <summary>
    /// Vibes-only: restarts the fill animation from zero up to the current value.
    /// No-op (value snaps as usual) when vibes are off.
    /// </summary>
    public void ReplayFromZero()
    {
        if (!FluentTheme.VibesActive || targetValue <= 0 || !IsHandleCreated || !Visible)
        {
            return;
        }

        valueAnimation?.Dispose();
        AnimateVibe(0, targetValue, VibeTheme.RevealDurationMs);
    }

    private void AnimateVibe(double from, double to, int durationMs)
    {
        vibeRevealDone = true;
        var travelled = Math.Abs(to - from);
        valueAnimation = FluentAnimator.Animate(
            from,
            to,
            durationMs,
            animated =>
            {
                displayedValue = animated;
                Invalidate();
            },
            completed: () =>
            {
                if (travelled >= 5)
                {
                    StartPulse();
                }
            });
    }

    /// <summary>
    /// Vibes-only: a slow breathing glow once the meter enters the heat zone (70%+), growing
    /// stronger toward 100% — a motion cue that reads regardless of the provider hue
    /// (Claude's identity is already orange).
    /// </summary>
    private void UpdateDangerPulse()
    {
        var shouldPulse = FluentTheme.VibesActive &&
            Visible &&
            IsHandleCreated &&
            targetValue >= 70 &&
            FluentAnimator.AnimationsEnabled;

        if (!shouldPulse)
        {
            if (dangerPulseTimer is { } timer)
            {
                timer.Stop();
                timer.Dispose();
                dangerPulseTimer = null;
                Invalidate();
            }

            return;
        }

        if (dangerPulseTimer is not null)
        {
            return;
        }

        dangerPhase = 0d;
        dangerPulseTimer = new System.Windows.Forms.Timer { Interval = 50 };
        dangerPulseTimer.Tick += (_, _) =>
        {
            dangerPhase += 0.05d / 1.4d;
            Invalidate();
        };
        dangerPulseTimer.Start();
    }

    private void StartPulse()
    {
        pulseAnimation?.Dispose();
        pulseAnimation = FluentAnimator.Animate(
            1d,
            0d,
            VibeTheme.PulseDurationMs,
            animated =>
            {
                pulseStrength = animated;
                Invalidate();
            });
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor
    {
        get => trackColor;
        set
        {
            trackColor = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor
    {
        get => accentColor;
        set
        {
            accentColor = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color WarningColor
    {
        get => warningColor;
        set
        {
            warningColor = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DangerColor
    {
        get => dangerColor;
        set
        {
            dangerColor = value;
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            valueAnimation?.Dispose();
            valueAnimation = null;
            pulseAnimation?.Dispose();
            pulseAnimation = null;
            dangerPulseTimer?.Dispose();
            dangerPulseTimer = null;
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var backgroundBrush = new SolidBrush(TrackColor);

        var radius = bounds.Height / 2f;
        using var backgroundPath = FluentTheme.RoundedRect(bounds, radius);
        e.Graphics.FillPath(backgroundBrush, backgroundPath);

        if (displayedValue <= 0)
        {
            return;
        }

        // Never let the fill collapse below a fully-round nub; tiny percentages
        // would otherwise render a clipped sliver instead of a rounded cap.
        var fillWidth = Math.Max(
            bounds.Height,
            (int)Math.Round(bounds.Width * (displayedValue / 100d)));

        var fillBounds = new RectangleF(bounds.X, bounds.Y, Math.Min(fillWidth, bounds.Width), bounds.Height);
        using var fillPath = FluentTheme.RoundedRect(fillBounds, radius);

        if (FluentTheme.VibesActive)
        {
            PaintVibeFill(e.Graphics, bounds, fillBounds, fillPath);
            return;
        }

        using var fillBrush = new SolidBrush(GetFillColor());
        e.Graphics.FillPath(fillBrush, fillPath);
    }

    private void PaintVibeFill(
        Graphics graphics,
        Rectangle bounds,
        RectangleF fillBounds,
        System.Drawing.Drawing2D.GraphicsPath fillPath)
    {
        // The gradient spans the whole track and is clipped by the fill, so the sweep stays
        // anchored while the bar grows instead of stretching with it.
        using (var gradient = VibeTheme.FillBrush(bounds, targetValue, vibePalette))
        {
            graphics.FillPath(gradient, fillPath);
        }

        // Landing pulse and the heat-zone breathing glow share one overlay; the strongest
        // wins. The breath amplitude scales with heat: barely-there at 70%, unmistakable
        // at 100%.
        var glowStrength = pulseStrength * 0.45;
        if (dangerPulseTimer is not null)
        {
            var breath = 0.5 + (0.5 * Math.Sin(dangerPhase * Math.PI * 2d));
            var heat = VibeTheme.HeatLevel(targetValue);
            glowStrength = Math.Max(glowStrength, heat * (0.10 + (0.32 * breath)));
        }

        if (glowStrength > 0)
        {
            var (_, end) = VibeTheme.FillGradient(targetValue, vibePalette);
            using var glowBrush = new SolidBrush(VibeTheme.WithOpacity(end, glowStrength));
            var glowBounds = RectangleF.Inflate(fillBounds, 0f, Math.Min(2f, bounds.Height / 2f));
            using var glowPath = FluentTheme.RoundedRect(glowBounds, glowBounds.Height / 2f);
            graphics.FillPath(glowBrush, glowPath);
        }

        // A bright leading tip while the bar is below target keeps the growth legible.
        if (displayedValue < targetValue - 0.5)
        {
            var tipWidth = Math.Max(6f, fillBounds.Height * 2f);
            var tipBounds = new RectangleF(
                Math.Max(fillBounds.X, fillBounds.Right - tipWidth),
                fillBounds.Y,
                tipWidth,
                fillBounds.Height);
            using var tipBrush = new SolidBrush(VibeTheme.WithOpacity(Color.White, 0.35));
            using var tipPath = FluentTheme.RoundedRect(tipBounds, tipBounds.Height / 2f);
            graphics.FillPath(tipBrush, tipPath);
        }
    }

    private Color GetFillColor()
    {
        if (targetValue >= 90)
        {
            return DangerColor;
        }

        if (targetValue >= 70)
        {
            return WarningColor;
        }

        return AccentColor;
    }
}
