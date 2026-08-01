using System.Drawing.Drawing2D;
using System.Drawing.Text;

// These controls are constructed in code only (no WinForms designer), so designer
// code-serialization metadata for their properties is irrelevant.
#pragma warning disable WFO1000

namespace CodexBarWindows;

/// <summary>
/// Implemented by the reusable Fluent controls so a window can restyle its entire control tree
/// in place (without rebuilding it) when the user switches the app theme.
/// </summary>
public interface IFluentThemeable
{
    void ApplyTheme(FluentTokens tokens);
}

/// <summary>Shared GDI+ helpers for the Fluent control set.</summary>
internal static class FluentControlPaint
{
    /// <summary>Single-line, ellipsis-trimmed GDI+ text with AntiAliasGridFit hinting.</summary>
    public static void DrawText(
        Graphics graphics,
        string text,
        Font font,
        Color color,
        RectangleF bounds,
        StringAlignment horizontalAlignment)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0f || bounds.Height <= 0f)
        {
            return;
        }

        var previousHint = graphics.TextRenderingHint;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        try
        {
            using var brush = new SolidBrush(color);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = horizontalAlignment,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            };
            graphics.DrawString(text, font, brush, bounds, format);
        }
        finally
        {
            graphics.TextRenderingHint = previousHint;
        }
    }

    /// <summary>
    /// Re-themes a hosted control and gives it the host surface color so non-self-painting
    /// controls blend into the card they sit on. Plain containers are walked recursively;
    /// Fluent controls own their interior and are not descended into.
    /// </summary>
    public static void ApplySurface(Control control, FluentTokens tokens, Color surface)
    {
        if (control is IFluentThemeable themeable)
        {
            themeable.ApplyTheme(tokens);
            control.BackColor = surface;
            return;
        }

        control.BackColor = surface;
        foreach (Control child in control.Controls)
        {
            ApplySurface(child, tokens, surface);
        }
    }
}

/// <summary>
/// Windows 11 Settings-style row: optional left glyph, a title with optional description, and a
/// right-docked action control slot, on a CardFill surface with a 1px CardStroke at radius 4.
/// Extra child controls may be added directly (use <see cref="TopAlignContent"/> for tall cards).
/// </summary>
public sealed class SettingsCard : Control, IFluentThemeable
{
    private readonly Font bodyFont = FluentTheme.BodyFont(1f);
    private readonly Font captionFont = FluentTheme.CaptionFont(1f);
    private readonly Font iconFont = FluentIcons.CreateFont(15f);
    private FluentTokens tokens;
    private string title = string.Empty;
    private string description = string.Empty;
    private string? glyph;
    private Control? actionControl;

    public SettingsCard(FluentTokens tokens)
    {
        this.tokens = tokens;
        BackColor = tokens.Background;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
    }

    public string Title
    {
        get => title;
        set
        {
            title = value ?? string.Empty;
            Invalidate();
        }
    }

    public string Description
    {
        get => description;
        set
        {
            description = value ?? string.Empty;
            Invalidate();
        }
    }

    /// <summary>Segoe Fluent Icons glyph drawn at 20px on the left edge, or null for none.</summary>
    public string? Glyph
    {
        get => glyph;
        set
        {
            glyph = value;
            Invalidate();
        }
    }

    /// <summary>Top-aligns the title block instead of centering it (for cards with extra content).</summary>
    public bool TopAlignContent { get; set; }

    /// <summary>Control docked to the right edge, vertically centered, 16px from the edge.</summary>
    public Control? ActionControl
    {
        get => actionControl;
        set
        {
            if (actionControl == value)
            {
                return;
            }

            if (actionControl is not null)
            {
                Controls.Remove(actionControl);
            }

            actionControl = value;
            if (value is not null)
            {
                Controls.Add(value);
            }

            PerformLayout();
            Invalidate();
        }
    }

    public void ApplyTheme(FluentTokens palette)
    {
        tokens = palette;
        BackColor = palette.Background;
        foreach (Control child in Controls)
        {
            FluentControlPaint.ApplySurface(child, palette, palette.CardFill);
        }

        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bodyFont.Dispose();
            captionFont.Dispose();
            iconFont.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (actionControl is null)
        {
            return;
        }

        var scale = DeviceDpi / 96f;
        var x = Width - (int)Math.Round(16f * scale) - actionControl.Width;
        var y = Math.Max(0, (Height - actionControl.Height) / 2);
        actionControl.Location = new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var scale = DeviceDpi / 96f;
        var strokeWidth = Math.Max(1f, scale);
        var bounds = new RectangleF(
            strokeWidth / 2f,
            strokeWidth / 2f,
            Width - strokeWidth,
            Height - strokeWidth);
        using (var path = FluentTheme.RoundedRect(bounds, FluentTheme.ControlCornerRadius * scale))
        using (var fillBrush = new SolidBrush(tokens.CardFill))
        using (var strokePen = new Pen(tokens.CardStroke, strokeWidth))
        {
            graphics.FillPath(fillBrush, path);
            graphics.DrawPath(strokePen, path);
        }

        var leftPadding = 16f * scale;
        var textX = leftPadding;
        if (!string.IsNullOrEmpty(glyph))
        {
            var iconSize = 20f * scale;
            var iconTop = TopAlignContent ? 16f * scale : (Height - iconSize) / 2f;
            var iconBounds = new RectangleF(leftPadding, iconTop, iconSize, iconSize);
            FluentIcons.Draw(
                graphics,
                glyph,
                iconFont,
                Enabled ? tokens.TextPrimary : tokens.TextDisabled,
                iconBounds);
            textX += iconSize + (16f * scale);
        }

        var rightLimit = actionControl?.Left ?? Width - (16f * scale);
        var availableWidth = Math.Max(0f, rightLimit - (12f * scale) - textX);
        var titleHeight = bodyFont.GetHeight(graphics);
        var descriptionHeight = captionFont.GetHeight(graphics);
        var hasDescription = !string.IsNullOrEmpty(description);
        var blockHeight = hasDescription
            ? titleHeight + (2f * scale) + descriptionHeight
            : titleHeight;
        var top = TopAlignContent ? 14f * scale : (Height - blockHeight) / 2f;

        FluentControlPaint.DrawText(
            graphics,
            title,
            bodyFont,
            Enabled ? tokens.TextPrimary : tokens.TextDisabled,
            new RectangleF(textX, top, availableWidth, titleHeight),
            StringAlignment.Near);

        if (hasDescription)
        {
            FluentControlPaint.DrawText(
                graphics,
                description,
                captionFont,
                Enabled ? tokens.TextTertiary : tokens.TextDisabled,
                new RectangleF(textX, top + titleHeight + (2f * scale), availableWidth, descriptionHeight),
                StringAlignment.Near);
        }
    }
}

/// <summary>
/// Sub-row hosted inside a <see cref="SettingsExpander"/>: indented title/description block,
/// a right-aligned action control, and a 1px CardStroke separator along its top edge. The row
/// background is transparent so the expander's rounded card surface shows through.
/// </summary>
public sealed class SettingsExpanderRow : Control, IFluentThemeable
{
    private readonly Font bodyFont = FluentTheme.BodyFont(1f);
    private readonly Font captionFont = FluentTheme.CaptionFont(1f);
    private FluentTokens tokens;
    private string title = string.Empty;
    private string description = string.Empty;
    private Control? actionControl;

    public SettingsExpanderRow(FluentTokens tokens)
    {
        this.tokens = tokens;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        BackColor = Color.Transparent;
    }

    public string Title
    {
        get => title;
        set
        {
            title = value ?? string.Empty;
            Invalidate();
        }
    }

    public string Description
    {
        get => description;
        set
        {
            description = value ?? string.Empty;
            Invalidate();
        }
    }

    /// <summary>Control right-aligned with the expander's header control (left of the chevron column).</summary>
    public Control? ActionControl
    {
        get => actionControl;
        set
        {
            if (actionControl == value)
            {
                return;
            }

            if (actionControl is not null)
            {
                Controls.Remove(actionControl);
            }

            actionControl = value;
            if (value is not null)
            {
                Controls.Add(value);
            }

            PerformLayout();
            Invalidate();
        }
    }

    public void ApplyTheme(FluentTokens palette)
    {
        tokens = palette;
        foreach (Control child in Controls)
        {
            FluentControlPaint.ApplySurface(child, palette, palette.CardFill);
        }

        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bodyFont.Dispose();
            captionFont.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (actionControl is null)
        {
            return;
        }

        var scale = DeviceDpi / 96f;
        var x = Width - (int)Math.Round(48f * scale) - actionControl.Width;
        var y = Math.Max(0, (Height - actionControl.Height) / 2);
        actionControl.Location = new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        var scale = DeviceDpi / 96f;
        var strokeWidth = Math.Max(1f, scale);

        using (var separatorPen = new Pen(tokens.CardStroke, strokeWidth))
        {
            graphics.DrawLine(
                separatorPen,
                strokeWidth,
                strokeWidth / 2f,
                Width - strokeWidth,
                strokeWidth / 2f);
        }

        var textX = 52f * scale;
        var rightLimit = actionControl?.Left ?? Width - (48f * scale);
        var availableWidth = Math.Max(0f, rightLimit - (12f * scale) - textX);
        var titleHeight = bodyFont.GetHeight(graphics);
        var descriptionHeight = captionFont.GetHeight(graphics);
        var hasDescription = !string.IsNullOrEmpty(description);
        var blockHeight = hasDescription
            ? titleHeight + (2f * scale) + descriptionHeight
            : titleHeight;
        var top = (Height - blockHeight) / 2f;

        FluentControlPaint.DrawText(
            graphics,
            title,
            bodyFont,
            Enabled ? tokens.TextPrimary : tokens.TextDisabled,
            new RectangleF(textX, top, availableWidth, titleHeight),
            StringAlignment.Near);

        if (hasDescription)
        {
            FluentControlPaint.DrawText(
                graphics,
                description,
                captionFont,
                Enabled ? tokens.TextTertiary : tokens.TextDisabled,
                new RectangleF(textX, top + titleHeight + (2f * scale), availableWidth, descriptionHeight),
                StringAlignment.Near);
        }
    }
}

/// <summary>
/// Windows 11 Settings-style expander: a card header (glyph, title/description, action control,
/// chevron) that expands a list of <see cref="SettingsExpanderRow"/> sub-rows separated by 1px
/// CardStroke lines. Expansion animates the control height (~150ms ease-out).
/// </summary>
public sealed class SettingsExpander : Control, IFluentThemeable
{
    private const float HeaderHeight96 = 64f;

    private readonly Font bodyFont = FluentTheme.BodyFont(1f);
    private readonly Font captionFont = FluentTheme.CaptionFont(1f);
    private readonly Font iconFont = FluentIcons.CreateFont(15f);
    private readonly Font chevronFont = FluentIcons.CreateFont(9f);
    private readonly List<SettingsExpanderRow> rows = [];
    private FluentTokens tokens;
    private string title = string.Empty;
    private string description = string.Empty;
    private string? glyph;
    private Control? headerControl;
    private bool expanded;
    private bool hoveringHeader;
    private IDisposable? heightAnimation;

    public event EventHandler? ExpandedChanged;

    public SettingsExpander(FluentTokens tokens)
    {
        this.tokens = tokens;
        BackColor = tokens.Background;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
    }

    public string Title
    {
        get => title;
        set
        {
            title = value ?? string.Empty;
            Invalidate();
        }
    }

    public string Description
    {
        get => description;
        set
        {
            description = value ?? string.Empty;
            Invalidate();
        }
    }

    /// <summary>Segoe Fluent Icons glyph drawn at 20px on the left edge of the header, or null.</summary>
    public string? Glyph
    {
        get => glyph;
        set
        {
            glyph = value;
            Invalidate();
        }
    }

    /// <summary>Control docked in the header to the left of the chevron, vertically centered.</summary>
    public Control? HeaderControl
    {
        get => headerControl;
        set
        {
            if (headerControl == value)
            {
                return;
            }

            if (headerControl is not null)
            {
                Controls.Remove(headerControl);
            }

            headerControl = value;
            if (value is not null)
            {
                Controls.Add(value);
            }

            PerformLayout();
            Invalidate();
        }
    }

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
            if (expanded)
            {
                foreach (var row in rows)
                {
                    row.Visible = true;
                }
            }

            heightAnimation?.Dispose();
            heightAnimation = null;

            var target = expanded ? ExpandedHeightDevice : HeaderHeightDevice;
            if (!IsHandleCreated)
            {
                Height = target;
                if (!expanded)
                {
                    foreach (var row in rows)
                    {
                        row.Visible = false;
                    }
                }
            }
            else
            {
                heightAnimation = FluentAnimator.Animate(Height, target, 150, animated =>
                {
                    Height = (int)Math.Round(animated);
                }, () =>
                {
                    if (!expanded)
                    {
                        foreach (var row in rows)
                        {
                            row.Visible = false;
                        }
                    }
                });
            }

            Invalidate();
            ExpandedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int HeaderHeightDevice => (int)Math.Round(HeaderHeight96 * DeviceDpi / 96f);

    private int ExpandedHeightDevice => HeaderHeightDevice + rows.Sum(row => row.Height);

    public void AddRow(SettingsExpanderRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        rows.Add(row);
        row.Visible = expanded;
        Controls.Add(row);
        if (expanded && heightAnimation is null)
        {
            Height = ExpandedHeightDevice;
        }

        PerformLayout();
    }

    public void ApplyTheme(FluentTokens palette)
    {
        tokens = palette;
        BackColor = palette.Background;
        if (headerControl is not null)
        {
            FluentControlPaint.ApplySurface(headerControl, palette, palette.CardFill);
        }

        foreach (var row in rows)
        {
            row.ApplyTheme(palette);
        }

        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            heightAnimation?.Dispose();
            bodyFont.Dispose();
            captionFont.Dispose();
            iconFont.Dispose();
            chevronFont.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var scale = DeviceDpi / 96f;
        var headerHeight = HeaderHeightDevice;

        if (headerControl is not null)
        {
            var x = Width - (int)Math.Round(48f * scale) - headerControl.Width;
            var y = Math.Max(0, (headerHeight - headerControl.Height) / 2);
            headerControl.Location = new Point(x, y);
        }

        var rowTop = headerHeight;
        foreach (var row in rows)
        {
            row.SetBounds(0, rowTop, Width, row.Height);
            rowTop += row.Height;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var overHeader = e.Y < HeaderHeightDevice;
        if (overHeader != hoveringHeader)
        {
            hoveringHeader = overHeader;
            Cursor = overHeader ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hoveringHeader)
        {
            hoveringHeader = false;
            Invalidate();
        }

        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.Y < HeaderHeightDevice)
        {
            Expanded = !Expanded;
        }

        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var scale = DeviceDpi / 96f;
        var strokeWidth = Math.Max(1f, scale);
        var headerHeight = HeaderHeightDevice;
        var bounds = new RectangleF(
            strokeWidth / 2f,
            strokeWidth / 2f,
            Width - strokeWidth,
            Height - strokeWidth);
        using var path = FluentTheme.RoundedRect(bounds, FluentTheme.ControlCornerRadius * scale);
        using (var fillBrush = new SolidBrush(tokens.CardFill))
        using (var strokePen = new Pen(tokens.CardStroke, strokeWidth))
        {
            graphics.FillPath(fillBrush, path);
            graphics.DrawPath(strokePen, path);
        }

        if (hoveringHeader && Enabled)
        {
            var state = graphics.Save();
            graphics.SetClip(path);
            using var hoverBrush = new SolidBrush(tokens.SubtleHover);
            graphics.FillRectangle(
                hoverBrush,
                strokeWidth,
                strokeWidth,
                Width - (2f * strokeWidth),
                headerHeight - (2f * strokeWidth));
            graphics.Restore(state);
        }

        var leftPadding = 16f * scale;
        var textX = leftPadding;
        if (!string.IsNullOrEmpty(glyph))
        {
            var iconSize = 20f * scale;
            var iconBounds = new RectangleF(leftPadding, (headerHeight - iconSize) / 2f, iconSize, iconSize);
            FluentIcons.Draw(
                graphics,
                glyph,
                iconFont,
                Enabled ? tokens.TextPrimary : tokens.TextDisabled,
                iconBounds);
            textX += iconSize + (16f * scale);
        }

        var chevronSize = 16f * scale;
        var chevronBounds = new RectangleF(
            Width - (16f * scale) - chevronSize,
            (headerHeight - chevronSize) / 2f,
            chevronSize,
            chevronSize);
        FluentIcons.Draw(
            graphics,
            expanded ? FluentIcons.ChevronUp : FluentIcons.ChevronDown,
            chevronFont,
            Enabled ? tokens.TextSecondary : tokens.TextDisabled,
            chevronBounds);

        var rightLimit = headerControl?.Left ?? chevronBounds.Left;
        var availableWidth = Math.Max(0f, rightLimit - (12f * scale) - textX);
        var titleHeight = bodyFont.GetHeight(graphics);
        var descriptionHeight = captionFont.GetHeight(graphics);
        var hasDescription = !string.IsNullOrEmpty(description);
        var blockHeight = hasDescription
            ? titleHeight + (2f * scale) + descriptionHeight
            : titleHeight;
        var top = (headerHeight - blockHeight) / 2f;

        FluentControlPaint.DrawText(
            graphics,
            title,
            bodyFont,
            Enabled ? tokens.TextPrimary : tokens.TextDisabled,
            new RectangleF(textX, top, availableWidth, titleHeight),
            StringAlignment.Near);

        if (hasDescription)
        {
            FluentControlPaint.DrawText(
                graphics,
                description,
                captionFont,
                Enabled ? tokens.TextTertiary : tokens.TextDisabled,
                new RectangleF(textX, top + titleHeight + (2f * scale), availableWidth, descriptionHeight),
                StringAlignment.Near);
        }
    }
}

/// <summary>
/// Fluent-styled drop-down (32px tall): the wrapper paints the WinUI field chrome and current
/// value while a hosted native <see cref="ComboBox"/> (made invisible via an empty window
/// region) provides the drop-down list, keyboard handling and owner-drawn 32px items.
/// </summary>
public sealed class FluentComboBox : Control, IFluentThemeable
{
    private readonly Font chevronFont = FluentIcons.CreateFont(9f);
    private FluentTokens tokens;
    private bool hovering;
    private bool flyoutOpen;
    private int selectedIndex = -1;
    private int flyoutClosedTick;
    private ComboFlyout? activeFlyout;

    public event EventHandler? SelectedIndexChanged;

    public FluentComboBox(FluentTokens tokens)
    {
        this.tokens = tokens;
        Items = new ItemCollection(this);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        AccessibleRole = AccessibleRole.ComboBox;
        Cursor = Cursors.Hand;
        TabStop = true;

        ApplyTheme(tokens);
    }

    public ItemCollection Items { get; }

    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            var clamped = Math.Clamp(value, -1, Items.Count - 1);
            if (selectedIndex == clamped)
            {
                return;
            }

            selectedIndex = clamped;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyTheme(FluentTokens palette)
    {
        tokens = palette;
        BackColor = palette.Background;
        ForeColor = palette.TextPrimary;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            activeFlyout?.Close();
            activeFlyout?.Dispose();
            activeFlyout = null;
            chevronFont.Dispose();
        }

        base.Dispose(disposing);
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
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
        {
            Focus();

            // When the flyout is open, the click that lands here is the same click its
            // AutoClose just dismissed it for — the tick guard stops an instant reopen.
            if (!flyoutOpen && Items.Count > 0 && Environment.TickCount - flyoutClosedTick > 250)
            {
                OpenFlyout();
            }
        }

        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && Items.Count > 0)
        {
            switch (e.KeyCode)
            {
                case Keys.Up:
                    SelectedIndex = Math.Max(0, (selectedIndex < 0 ? Items.Count : selectedIndex) - 1);
                    e.Handled = true;
                    break;
                case Keys.Down when e.Alt:
                case Keys.Enter:
                case Keys.Space:
                    if (!flyoutOpen)
                    {
                        OpenFlyout();
                    }

                    e.Handled = true;
                    break;
                case Keys.Down:
                    SelectedIndex = Math.Min(Items.Count - 1, selectedIndex + 1);
                    e.Handled = true;
                    break;
                case Keys.Home:
                    SelectedIndex = 0;
                    e.Handled = true;
                    break;
                case Keys.End:
                    SelectedIndex = Items.Count - 1;
                    e.Handled = true;
                    break;
            }
        }

        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var scale = DeviceDpi / 96f;
        var strokeWidth = Math.Max(1f, scale);
        var radius = FluentTheme.ControlCornerRadius * scale;
        var bounds = new RectangleF(
            strokeWidth / 2f,
            strokeWidth / 2f,
            Width - strokeWidth,
            Height - strokeWidth);
        using var path = FluentTheme.RoundedRect(bounds, radius);

        var fill = !Enabled
            ? tokens.ControlFillDisabled
            : flyoutOpen
                ? tokens.ControlFillPressed
                : hovering ? tokens.ControlFillHover : tokens.ControlFill;
        using (var fillBrush = new SolidBrush(fill))
        using (var strokePen = new Pen(tokens.ControlStroke, strokeWidth))
        {
            graphics.FillPath(fillBrush, path);
            graphics.DrawPath(strokePen, path);
        }

        if (Enabled && !flyoutOpen)
        {
            using var bottomPen = new Pen(tokens.ControlStrokeBottom, strokeWidth);
            graphics.DrawLine(
                bottomPen,
                bounds.Left + radius,
                bounds.Bottom,
                bounds.Right - radius,
                bounds.Bottom);
        }

        if (Enabled && Focused && ShowFocusCues && !flyoutOpen)
        {
            var focusWidth = Math.Max(2f, 2f * scale);
            using var focusPen = new Pen(tokens.Accent, focusWidth);
            var inset = focusWidth / 2f;
            var focusBounds = new RectangleF(inset, inset, Width - (inset * 2f), Height - (inset * 2f));
            using var focusPath = FluentTheme.RoundedRect(focusBounds, radius);
            graphics.DrawPath(focusPen, focusPath);
        }

        var chevronSize = 12f * scale;
        var chevronBounds = new RectangleF(
            Width - (12f * scale) - chevronSize,
            (Height - chevronSize) / 2f,
            chevronSize,
            chevronSize);
        FluentIcons.Draw(
            graphics,
            FluentIcons.ChevronDown,
            chevronFont,
            Enabled ? tokens.TextSecondary : tokens.TextDisabled,
            chevronBounds);

        var text = selectedIndex >= 0 && selectedIndex < Items.Count ? Items[selectedIndex] : string.Empty;
        var textBounds = new RectangleF(
            12f * scale,
            0f,
            Math.Max(0f, chevronBounds.Left - (8f * scale) - (12f * scale)),
            Height);
        FluentControlPaint.DrawText(
            graphics,
            text,
            Font,
            Enabled ? tokens.TextPrimary : tokens.TextDisabled,
            textBounds,
            StringAlignment.Near);
    }

    private void OpenFlyout()
    {
        var scale = DeviceDpi / 96f;
        var flyout = new ComboFlyout(tokens, Items.Snapshot, selectedIndex, Font, scale, Width);
        activeFlyout = flyout;
        flyoutOpen = true;
        Invalidate();

        flyout.ItemPicked += index => SelectedIndex = index;
        flyout.Closed += (_, _) =>
        {
            flyoutOpen = false;
            flyoutClosedTick = Environment.TickCount;
            if (ReferenceEquals(activeFlyout, flyout))
            {
                activeFlyout = null;
            }

            Invalidate();
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(flyout.Dispose);
            }
        };

        // WinUI placement: the flyout overlaps the field with the selected item sitting
        // directly on top of it, clamped to the working area of the current monitor.
        var anchorIndex = Math.Max(0, selectedIndex);
        var fieldScreen = PointToScreen(Point.Empty);
        var desiredY = fieldScreen.Y
            - flyout.EdgePadding
            - (anchorIndex * flyout.ItemHeight)
            - ((flyout.ItemHeight - Height) / 2);
        var workingArea = Screen.FromControl(this).WorkingArea;
        var x = Math.Clamp(
            fieldScreen.X,
            workingArea.Left + 4,
            Math.Max(workingArea.Left + 4, workingArea.Right - flyout.Width - 4));
        var y = Math.Clamp(
            desiredY,
            workingArea.Top + 4,
            Math.Max(workingArea.Top + 4, workingArea.Bottom - flyout.Height - 4));
        flyout.Show(new Point(x, y));
    }

    public sealed class ItemCollection
    {
        private readonly FluentComboBox owner;
        private readonly List<string> items = [];

        internal ItemCollection(FluentComboBox owner)
        {
            this.owner = owner;
        }

        public int Count => items.Count;

        public string this[int index] => items[index];

        internal IReadOnlyList<string> Snapshot => items;

        public void Add(string item)
        {
            items.Add(item);
            owner.Invalidate();
        }

        public void AddRange(params string[] range)
        {
            items.AddRange(range);
            owner.Invalidate();
        }

        public void Clear()
        {
            items.Clear();
            owner.selectedIndex = -1;
            owner.Invalidate();
        }
    }

    /// <summary>
    /// The drop-down surface: a borderless ToolStripDropDown (AutoClose handles outside-click
    /// and deactivation) hosting a custom-painted item list. On Windows 11 DWM supplies the
    /// rounded corners and shadow; pre-Win11 falls back to the classic drop shadow.
    /// </summary>
    private sealed class ComboFlyout : ToolStripDropDown
    {
        private readonly FlyoutList list;

        public event Action<int>? ItemPicked;

        public ComboFlyout(
            FluentTokens tokens,
            IReadOnlyList<string> items,
            int selectedIndex,
            Font textFont,
            float scale,
            int width)
        {
            ItemHeight = (int)Math.Round(36f * scale);
            EdgePadding = (int)Math.Round(4f * scale);

            AutoClose = true;
            AutoSize = false;
            DoubleBuffered = true;
            DropShadowEnabled = !WindowEffects.IsWindows11;
            Margin = Padding.Empty;
            Padding = Padding.Empty;

            list = new FlyoutList(tokens, items, selectedIndex, textFont, scale, ItemHeight, EdgePadding);
            BackColor = list.BackColor;

            var size = new Size(Math.Max(width, ItemHeight), (items.Count * ItemHeight) + (EdgePadding * 2));
            var host = new ToolStripControlHost(list)
            {
                AutoSize = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Size = size
            };
            Items.Add(host);
            Size = size;
            list.Size = size;

            list.ItemPicked += index =>
            {
                ItemPicked?.Invoke(index);
                Close(ToolStripDropDownCloseReason.ItemClicked);
            };
            list.DismissRequested += () => Close(ToolStripDropDownCloseReason.Keyboard);
        }

        public int ItemHeight { get; }

        public int EdgePadding { get; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowEffects.SetRoundedCorners(Handle, round: true);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            list.Focus();
        }
    }

    private sealed class FlyoutList : Control
    {
        private readonly IReadOnlyList<string> items;
        private readonly float scale;
        private readonly int itemHeight;
        private readonly int edgePadding;
        private readonly int selectedIndex;
        private readonly Color flyoutFill;
        private readonly Color flyoutStroke;
        private readonly Color hoverFill;
        private readonly Color selectedFill;
        private readonly Color textColor;
        private readonly Color pillColor;
        private int hotIndex;

        public event Action<int>? ItemPicked;

        public event Action? DismissRequested;

        public FlyoutList(
            FluentTokens tokens,
            IReadOnlyList<string> items,
            int selectedIndex,
            Font textFont,
            float scale,
            int itemHeight,
            int edgePadding)
        {
            this.items = items;
            this.selectedIndex = selectedIndex;
            this.scale = scale;
            this.itemHeight = itemHeight;
            this.edgePadding = edgePadding;
            hotIndex = selectedIndex;

            // WinUI solid flyout fallback colors. The flyout is its own opaque top-level
            // window, so these never composite over a backdrop and can stay hardcoded.
            flyoutFill = tokens.IsDark ? Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C) : Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9);
            flyoutStroke = tokens.IsDark ? Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A) : Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC);
            hoverFill = tokens.IsDark ? Color.FromArgb(0xFF, 0x38, 0x38, 0x38) : Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA);
            selectedFill = tokens.IsDark ? Color.FromArgb(0xFF, 0x33, 0x33, 0x33) : Color.FromArgb(0xFF, 0xEF, 0xEF, 0xEF);
            textColor = tokens.IsDark ? Color.White : Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B);
            pillColor = tokens.Accent;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.UserPaint,
                true);
            BackColor = flyoutFill;
            Font = textFont;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Enter or Keys.Escape
                || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up:
                    MoveHot(-1);
                    e.Handled = true;
                    break;
                case Keys.Down:
                    MoveHot(1);
                    e.Handled = true;
                    break;
                case Keys.Home:
                    hotIndex = 0;
                    Invalidate();
                    e.Handled = true;
                    break;
                case Keys.End:
                    hotIndex = items.Count - 1;
                    Invalidate();
                    e.Handled = true;
                    break;
                case Keys.Enter:
                case Keys.Space:
                    if (hotIndex >= 0)
                    {
                        ItemPicked?.Invoke(hotIndex);
                    }
                    else
                    {
                        DismissRequested?.Invoke();
                    }

                    e.Handled = true;
                    break;
                case Keys.Escape:
                    DismissRequested?.Invoke();
                    e.Handled = true;
                    break;
            }

            base.OnKeyDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var index = HitTest(e.Location);
            if (index != hotIndex)
            {
                hotIndex = index;
                Invalidate();
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hotIndex != selectedIndex)
            {
                hotIndex = selectedIndex;
                Invalidate();
            }

            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var index = HitTest(e.Location);
                if (index >= 0)
                {
                    ItemPicked?.Invoke(index);
                }
            }

            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (var backBrush = new SolidBrush(flyoutFill))
            {
                graphics.FillRectangle(backBrush, ClientRectangle);
            }

            for (var index = 0; index < items.Count; index++)
            {
                var rowBounds = RowBounds(index);
                var isHot = index == hotIndex;
                var isCurrent = index == selectedIndex;

                if (isHot || isCurrent)
                {
                    using var rowBrush = new SolidBrush(isHot ? hoverFill : selectedFill);
                    using var rowPath = FluentTheme.RoundedRect(rowBounds, FluentTheme.ControlCornerRadius * scale);
                    graphics.FillPath(rowBrush, rowPath);
                }

                if (isCurrent)
                {
                    var pillWidth = 3f * scale;
                    var pillHeight = 16f * scale;
                    var pillBounds = new RectangleF(
                        rowBounds.Left,
                        rowBounds.Top + ((rowBounds.Height - pillHeight) / 2f),
                        pillWidth,
                        pillHeight);
                    using var pillBrush = new SolidBrush(pillColor);
                    using var pillPath = FluentTheme.RoundedRect(pillBounds, pillWidth / 2f);
                    graphics.FillPath(pillBrush, pillPath);
                }

                var textBounds = new RectangleF(
                    rowBounds.Left + (12f * scale),
                    rowBounds.Top,
                    Math.Max(0f, rowBounds.Width - (20f * scale)),
                    rowBounds.Height);
                FluentControlPaint.DrawText(graphics, items[index], Font, textColor, textBounds, StringAlignment.Near);
            }

            var strokeWidth = Math.Max(1f, scale);
            using var borderPen = new Pen(flyoutStroke, strokeWidth);
            var borderBounds = new RectangleF(
                strokeWidth / 2f,
                strokeWidth / 2f,
                Width - strokeWidth,
                Height - strokeWidth);
            using var borderPath = FluentTheme.RoundedRect(borderBounds, FluentTheme.OverlayCornerRadius * scale);
            graphics.DrawPath(borderPen, borderPath);
        }

        private RectangleF RowBounds(int index)
        {
            var inset = 2f * scale;
            return new RectangleF(
                edgePadding,
                edgePadding + (index * itemHeight) + inset,
                Width - (edgePadding * 2),
                itemHeight - (inset * 2f));
        }

        private void MoveHot(int delta)
        {
            if (items.Count == 0)
            {
                return;
            }

            hotIndex = hotIndex < 0
                ? delta > 0 ? 0 : items.Count - 1
                : Math.Clamp(hotIndex + delta, 0, items.Count - 1);
            Invalidate();
        }

        private int HitTest(Point point)
        {
            if (point.X < 0 || point.X >= Width || point.Y < edgePadding)
            {
                return -1;
            }

            var index = (point.Y - edgePadding) / Math.Max(1, itemHeight);
            return index >= 0 && index < items.Count ? index : -1;
        }
    }
}

/// <summary>
/// Windows 11-style slider: 4px rounded track (accent-filled to the left of the thumb), a 20px
/// thumb with an accent inner dot that grows from 12px to 14px on hover/drag. Supports mouse
/// drag, click-to-position and Left/Right/Home/End keys.
/// </summary>
public sealed class FluentSlider : Control, IFluentThemeable
{
    private const double DotRestDiameter = 12d;
    private const double DotActiveDiameter = 14d;
    private const int DotGrowDurationMs = 120;

    private FluentTokens tokens;
    private int minimum;
    private int maximum = 100;
    private int value;
    private bool hovering;
    private bool dragging;
    private double dotDiameter = DotRestDiameter;
    private IDisposable? dotAnimation;

    public event EventHandler? ValueChanged;

    public FluentSlider(FluentTokens tokens)
    {
        this.tokens = tokens;
        BackColor = tokens.Background;
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.UserPaint,
            true);
    }

    public int Minimum
    {
        get => minimum;
        set
        {
            minimum = Math.Min(value, maximum);
            Value = this.value;
            Invalidate();
        }
    }

    public int Maximum
    {
        get => maximum;
        set
        {
            maximum = Math.Max(value, minimum);
            Value = this.value;
            Invalidate();
        }
    }

    public int Value
    {
        get => value;
        set
        {
            var clamped = Math.Clamp(value, minimum, maximum);
            if (clamped == this.value)
            {
                return;
            }

            this.value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
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
            dotAnimation?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End
            || base.IsInputKey(keyData);
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
        if (Enabled)
        {
            AnimateDot(DotActiveDiameter);
        }

        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovering = false;
        if (!dragging)
        {
            AnimateDot(DotRestDiameter);
        }

        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
        {
            Focus();
            dragging = true;
            AnimateDot(DotActiveDiameter);
            SetValueFromPosition(e.X);
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging)
        {
            SetValueFromPosition(e.X);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (dragging)
        {
            dragging = false;
            if (!hovering)
            {
                AnimateDot(DotRestDiameter);
            }
        }

        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled)
        {
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Down:
                    Value -= 1;
                    e.Handled = true;
                    break;
                case Keys.Right:
                case Keys.Up:
                    Value += 1;
                    e.Handled = true;
                    break;
                case Keys.Home:
                    Value = minimum;
                    e.Handled = true;
                    break;
                case Keys.End:
                    Value = maximum;
                    e.Handled = true;
                    break;
            }
        }

        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var scale = DeviceDpi / 96f;
        var strokeWidth = Math.Max(1f, scale);
        var thumbDiameter = 20f * scale;
        var padding = (thumbDiameter / 2f) + strokeWidth;
        var trackHeight = 4f * scale;
        var trackTop = (Height - trackHeight) / 2f;
        var usableWidth = Math.Max(1f, Width - (2f * padding));
        var ratio = maximum > minimum
            ? (value - minimum) / (float)(maximum - minimum)
            : 0f;
        var thumbCenterX = padding + (usableWidth * ratio);
        var thumbCenterY = Height / 2f;

        var accent = Enabled ? tokens.Accent : tokens.TextDisabled;

        // Unfilled remainder of the track (strong stroke at ~40% alpha over the surface).
        var restColor = Color.FromArgb(0x66, tokens.ControlStrongStroke);
        using (var restPath = FluentTheme.RoundedRect(
            new RectangleF(padding, trackTop, usableWidth, trackHeight),
            trackHeight / 2f))
        using (var restBrush = new SolidBrush(restColor))
        {
            graphics.FillPath(restBrush, restPath);
        }

        // Filled (left) portion of the track.
        var filledWidth = thumbCenterX - padding;
        if (filledWidth > 0f)
        {
            using var filledPath = FluentTheme.RoundedRect(
                new RectangleF(padding, trackTop, filledWidth, trackHeight),
                trackHeight / 2f);
            using var filledBrush = new SolidBrush(accent);
            graphics.FillPath(filledBrush, filledPath);
        }

        // Thumb: opaque outer circle with a 1px stroke and an accent inner dot.
        var outerBounds = new RectangleF(
            thumbCenterX - (thumbDiameter / 2f),
            thumbCenterY - (thumbDiameter / 2f),
            thumbDiameter,
            thumbDiameter);
        using (var outerBrush = new SolidBrush(tokens.ControlFill))
        using (var outerPen = new Pen(tokens.ControlStroke, strokeWidth))
        {
            graphics.FillEllipse(outerBrush, outerBounds);
            graphics.DrawEllipse(outerPen, outerBounds);
        }

        var dotSize = (float)(dotDiameter * scale);
        var dotBounds = new RectangleF(
            thumbCenterX - (dotSize / 2f),
            thumbCenterY - (dotSize / 2f),
            dotSize,
            dotSize);
        using var dotBrush = new SolidBrush(accent);
        graphics.FillEllipse(dotBrush, dotBounds);
    }

    private void SetValueFromPosition(int x)
    {
        var scale = DeviceDpi / 96f;
        var strokeWidth = Math.Max(1f, scale);
        var padding = (20f * scale / 2f) + strokeWidth;
        var usableWidth = Math.Max(1f, Width - (2f * padding));
        var ratio = Math.Clamp((x - padding) / usableWidth, 0f, 1f);
        Value = minimum + (int)Math.Round(ratio * (maximum - minimum));
    }

    private void AnimateDot(double target)
    {
        dotAnimation?.Dispose();
        dotAnimation = null;
        if (!IsHandleCreated)
        {
            dotDiameter = target;
            Invalidate();
            return;
        }

        dotAnimation = FluentAnimator.Animate(dotDiameter, target, DotGrowDurationMs, value =>
        {
            dotDiameter = value;
            Invalidate();
        });
    }
}

public enum FluentButtonKind
{
    Primary,
    Secondary
}

/// <summary>
/// Fluent button (32px tall, radius 4): accent fill with a darker bottom edge for Primary,
/// ControlFill with ControlStroke/Bottom for Secondary. Activates on Space/Enter.
/// </summary>
public sealed class FluentButton : Control, IFluentThemeable
{
    private readonly FluentButtonKind kind;
    private FluentTokens tokens;
    private bool hovering;
    private bool pressing;

    public FluentButton(FluentTokens tokens, FluentButtonKind kind)
    {
        this.tokens = tokens;
        this.kind = kind;
        BackColor = tokens.Background;
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.UserPaint,
            true);
    }

    public void ApplyTheme(FluentTokens palette)
    {
        tokens = palette;
        BackColor = palette.Background;
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
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
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var scale = DeviceDpi / 96f;
        var strokeWidth = Math.Max(1f, scale);
        var radius = FluentTheme.ControlCornerRadius * scale;
        var bounds = new RectangleF(
            strokeWidth / 2f,
            strokeWidth / 2f,
            Width - strokeWidth,
            Height - strokeWidth);
        using var path = FluentTheme.RoundedRect(bounds, radius);

        var fill = ButtonFill();
        using (var fillBrush = new SolidBrush(fill))
        using (var strokePen = new Pen(ButtonStroke(fill), strokeWidth))
        {
            graphics.FillPath(fillBrush, path);
            graphics.DrawPath(strokePen, path);
        }

        if (Enabled && !pressing)
        {
            using var bottomPen = new Pen(BottomEdgeColor(fill), strokeWidth);
            graphics.DrawLine(
                bottomPen,
                bounds.Left + radius,
                bounds.Bottom,
                bounds.Right - radius,
                bounds.Bottom);
        }

        var textPadding = 12f * scale;
        var textBounds = new RectangleF(
            bounds.Left + textPadding,
            bounds.Top,
            Math.Max(0f, bounds.Width - (textPadding * 2f)),
            bounds.Height);
        FluentControlPaint.DrawText(graphics, Text, Font, ButtonTextColor(), textBounds, StringAlignment.Center);
    }

    private Color ButtonFill()
    {
        if (!Enabled)
        {
            return tokens.ControlFillDisabled;
        }

        if (kind == FluentButtonKind.Primary)
        {
            return pressing ? tokens.AccentPressed : hovering ? tokens.AccentHover : tokens.Accent;
        }

        return pressing ? tokens.ControlFillPressed : hovering ? tokens.ControlFillHover : tokens.ControlFill;
    }

    private Color ButtonStroke(Color fill)
    {
        if (!Enabled)
        {
            return tokens.ControlStroke;
        }

        return kind == FluentButtonKind.Primary ? fill : tokens.ControlStroke;
    }

    private Color BottomEdgeColor(Color fill)
    {
        return kind == FluentButtonKind.Primary
            ? FluentTheme.Darken(fill, 0.2f)
            : tokens.ControlStrokeBottom;
    }

    private Color ButtonTextColor()
    {
        if (!Enabled)
        {
            return tokens.TextDisabled;
        }

        return kind == FluentButtonKind.Primary ? tokens.TextOnAccent : tokens.TextPrimary;
    }
}

/// <summary>
/// Fluent text field: rounded 4px chrome with ControlStroke plus a stronger bottom edge that
/// becomes a 2px accent underline on focus, hosting a borderless native <see cref="TextBox"/>.
/// </summary>
public sealed class FluentTextField : UserControl, IFluentThemeable
{
    private readonly TextBox textBox;
    private FluentTokens tokens;
    private bool focused;
    private bool hovering;

    public FluentTextField(FluentTokens tokens)
    {
        this.tokens = tokens;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        textBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Location = new Point(12, 6),
            Size = new Size(Math.Max(10, Width - 24), 20)
        };
        textBox.GotFocus += (_, _) =>
        {
            focused = true;
            UpdateVisualState();
        };
        textBox.LostFocus += (_, _) =>
        {
            focused = false;
            UpdateVisualState();
        };
        textBox.MouseEnter += (_, _) =>
        {
            hovering = true;
            UpdateVisualState();
        };
        textBox.MouseLeave += (_, _) =>
        {
            hovering = false;
            UpdateVisualState();
        };

        Controls.Add(textBox);
        ApplyTheme(tokens);
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

    [System.ComponentModel.Browsable(true)]
    [System.ComponentModel.DefaultValue(false)]
    public bool UseSystemPasswordChar
    {
        get => textBox.UseSystemPasswordChar;
        set => textBox.UseSystemPasswordChar = value;
    }

    public void ApplyTheme(FluentTokens palette)
    {
        tokens = palette;
        BackColor = palette.Background;
        textBox.ForeColor = palette.TextPrimary;
        UpdateVisualState();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        textBox.Enabled = Enabled;
        UpdateVisualState();
        base.OnEnabledChanged(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovering = true;
        UpdateVisualState();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovering = false;
        UpdateVisualState();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        textBox.Focus();
        base.OnClick(e);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        PositionInnerTextBox();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var scale = DeviceDpi / 96f;
        var strokeWidth = Math.Max(1f, scale);
        var radius = FluentTheme.ControlCornerRadius * scale;
        var bounds = new RectangleF(
            strokeWidth / 2f,
            strokeWidth / 2f,
            Width - strokeWidth,
            Height - strokeWidth);
        using var path = FluentTheme.RoundedRect(bounds, radius);

        using (var fillBrush = new SolidBrush(CurrentFill()))
        using (var strokePen = new Pen(tokens.ControlStroke, strokeWidth))
        {
            graphics.FillPath(fillBrush, path);
            graphics.DrawPath(strokePen, path);
        }

        if (focused)
        {
            var underline = 2f * scale;
            var state = graphics.Save();
            graphics.SetClip(path);
            using var underlineBrush = new SolidBrush(tokens.Accent);
            graphics.FillRectangle(
                underlineBrush,
                bounds.Left,
                bounds.Bottom - underline,
                bounds.Width,
                underline + strokeWidth);
            graphics.Restore(state);
        }
        else if (Enabled)
        {
            using var bottomPen = new Pen(tokens.ControlStrokeBottom, strokeWidth);
            graphics.DrawLine(
                bottomPen,
                bounds.Left + radius,
                bounds.Bottom,
                bounds.Right - radius,
                bounds.Bottom);
        }
    }

    private void PositionInnerTextBox()
    {
        var padding = (int)Math.Round(12f * DeviceDpi / 96f);
        var inner = new Rectangle(
            padding,
            Math.Max(0, (Height - textBox.Height) / 2),
            Math.Max(10, Width - (padding * 2)),
            textBox.Height);
        if (textBox.Bounds != inner)
        {
            textBox.Bounds = inner;
        }
    }

    private Color CurrentFill()
    {
        if (!Enabled)
        {
            return tokens.ControlFillDisabled;
        }

        return hovering && !focused ? tokens.ControlFillHover : tokens.ControlFill;
    }

    private void UpdateVisualState()
    {
        var fill = CurrentFill();
        if (textBox.BackColor != fill)
        {
            textBox.BackColor = fill;
        }

        Invalidate();
    }
}

/// <summary>
/// WinUI toggle switch (40x20): rounded track (accent when on, strong-stroke outline when off)
/// with a knob that slides over ~150ms and grows from 12px to 14px on hover.
/// </summary>
public sealed class FluentToggle : Control, IFluentThemeable
{
    private const double KnobRestDiameter = 12d;
    private const double KnobHoverDiameter = 14d;
    private const int KnobSlideDurationMs = 150;
    private const int KnobGrowDurationMs = 120;

    private FluentTokens tokens;
    private bool isChecked;
    private bool hovering;
    private double knobPosition;
    private double knobDiameter = KnobRestDiameter;
    private IDisposable? slideAnimation;
    private IDisposable? growAnimation;

    public event EventHandler? CheckedChanged;

    public FluentToggle(FluentTokens tokens)
    {
        this.tokens = tokens;
        BackColor = tokens.Background;
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
            AnimateKnobPosition(value ? 1d : 0d);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
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
            slideAnimation?.Dispose();
            growAnimation?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovering = true;
        AnimateKnobDiameter(KnobHoverDiameter);
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovering = false;
        AnimateKnobDiameter(KnobRestDiameter);
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
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var scale = DeviceDpi / 96f;
        var strokeWidth = Math.Max(1f, scale);
        var trackBounds = new RectangleF(
            strokeWidth / 2f,
            strokeWidth / 2f,
            Width - strokeWidth,
            Height - strokeWidth);
        using var trackPath = FluentTheme.RoundedRect(trackBounds, trackBounds.Height / 2f);

        var trackFill = Checked
            ? hovering ? tokens.AccentHover : tokens.Accent
            : hovering ? tokens.ControlFillHover : tokens.ControlFill;
        // Off state needs the strong stroke: ControlFill is identical to the card fill in the
        // opaque token set, so without it the track is invisible (WinUI uses a strong outline).
        var trackStroke = Checked ? trackFill : tokens.ControlStrongStroke;
        using (var trackBrush = new SolidBrush(trackFill))
        using (var trackPen = new Pen(trackStroke, strokeWidth))
        {
            graphics.FillPath(trackBrush, trackPath);
            graphics.DrawPath(trackPen, trackPath);
        }

        var restDiameter = (float)(KnobRestDiameter * scale);
        var travelInset = ((trackBounds.Height - restDiameter) / 2f) + (restDiameter / 2f);
        var offCenterX = trackBounds.Left + travelInset;
        var onCenterX = trackBounds.Right - travelInset;
        var centerX = offCenterX + ((onCenterX - offCenterX) * (float)knobPosition);
        var centerY = trackBounds.Top + (trackBounds.Height / 2f);
        var knobSize = (float)(knobDiameter * scale);
        var knobBounds = new RectangleF(
            centerX - (knobSize / 2f),
            centerY - (knobSize / 2f),
            knobSize,
            knobSize);
        using var knobBrush = new SolidBrush(Checked ? tokens.TextOnAccent : tokens.TextSecondary);
        graphics.FillEllipse(knobBrush, knobBounds);
    }

    private void AnimateKnobPosition(double target)
    {
        slideAnimation?.Dispose();
        slideAnimation = null;
        if (!IsHandleCreated)
        {
            knobPosition = target;
            Invalidate();
            return;
        }

        slideAnimation = FluentAnimator.Animate(knobPosition, target, KnobSlideDurationMs, value =>
        {
            knobPosition = value;
            Invalidate();
        });
    }

    private void AnimateKnobDiameter(double target)
    {
        growAnimation?.Dispose();
        growAnimation = null;
        if (!IsHandleCreated)
        {
            knobDiameter = target;
            Invalidate();
            return;
        }

        growAnimation = FluentAnimator.Animate(knobDiameter, target, KnobGrowDurationMs, value =>
        {
            knobDiameter = value;
            Invalidate();
        });
    }
}
