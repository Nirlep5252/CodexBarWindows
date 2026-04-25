using System.ComponentModel;

namespace CodexBarWindows;

public sealed class UsageMeterControl : Control
{
    private double value;
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
            ControlStyles.UserPaint,
            true);

        Height = 10;
    }

    [DefaultValue(0d)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public double Value
    {
        get => value;
        set
        {
            this.value = Math.Clamp(value, 0, 100);
            Invalidate();
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

        var radius = bounds.Height;
        using var backgroundPath = CreateRoundedPath(bounds, radius);
        e.Graphics.FillPath(backgroundBrush, backgroundPath);

        var fillWidth = (int)Math.Round(bounds.Width * (Value / 100));
        if (fillWidth <= 0)
        {
            return;
        }

        var fillBounds = new Rectangle(bounds.X, bounds.Y, fillWidth, bounds.Height);
        using var fillPath = CreateRoundedPath(fillBounds, radius);
        e.Graphics.FillPath(fillBrush, fillPath);
    }

    private Color GetFillColor()
    {
        if (Value >= 90)
        {
            return DangerColor;
        }

        if (Value >= 70)
        {
            return WarningColor;
        }

        return AccentColor;
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedPath(Rectangle rectangle, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height));

        if (diameter <= 1)
        {
            path.AddRectangle(rectangle);
            return path;
        }

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
}
