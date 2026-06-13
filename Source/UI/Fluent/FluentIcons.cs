using System.Drawing.Text;

namespace CodexBarWindows;

/// <summary>
/// Segoe Fluent Icons glyphs plus helpers to create the icon font and draw glyphs with GDI+.
/// </summary>
public static class FluentIcons
{
    public const string ChevronDown = "\uE70D";
    public const string ChevronUp = "\uE70E";
    public const string ChevronRight = "\uE76C";
    public const string Close = "\uE8BB";
    public const string Settings = "\uE713";
    public const string Refresh = "\uE72C";
    public const string More = "\uE712";
    public const string Add = "\uE710";
    public const string Delete = "\uE74D";
    public const string Edit = "\uE70F";
    public const string Copy = "\uE8C8";
    public const string Warning = "\uE7BA";
    public const string Info = "\uE946";
    public const string History = "\uE81C";

    private const string FluentFamily = "Segoe Fluent Icons";
    private const string Mdl2Family = "Segoe MDL2 Assets";

    /// <summary>
    /// Creates the icon font at the given size in points: "Segoe Fluent Icons" (Windows 11),
    /// falling back to "Segoe MDL2 Assets" (Windows 10). Caller owns disposal.
    /// </summary>
    public static Font CreateFont(float emSizePoints)
    {
        return TryCreateFont(FluentFamily, emSizePoints)
            ?? TryCreateFont(Mdl2Family, emSizePoints)
            ?? new Font(FontFamily.GenericSansSerif, emSizePoints, FontStyle.Regular, GraphicsUnit.Point);
    }

    /// <summary>
    /// Draws a glyph centered in <paramref name="bounds"/> using GDI+ (never TextRenderer — GDI
    /// text writes alpha 0 and punches holes in backdrop-extended windows) with AntiAliasGridFit.
    /// </summary>
    public static void Draw(Graphics g, string glyph, Font font, Color color, RectangleF bounds)
    {
        var previousHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        try
        {
            using var brush = new SolidBrush(color);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
            };
            g.DrawString(glyph, font, brush, bounds, format);
        }
        finally
        {
            g.TextRenderingHint = previousHint;
        }
    }

    private static Font? TryCreateFont(string family, float emSizePoints)
    {
        try
        {
            var font = new Font(family, emSizePoints, FontStyle.Regular, GraphicsUnit.Point);
            if (string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase))
            {
                return font;
            }

            font.Dispose();
        }
        catch (ArgumentException)
        {
            // Family rejected by GDI+; fall through to the next candidate.
        }

        return null;
    }
}
