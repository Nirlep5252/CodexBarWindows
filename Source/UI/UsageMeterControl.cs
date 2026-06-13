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
        using var fillBrush = new SolidBrush(GetFillColor());

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
        e.Graphics.FillPath(fillBrush, fillPath);
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
