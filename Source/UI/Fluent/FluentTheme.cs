using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace CodexBarWindows;

/// <summary>
/// Immutable set of Fluent (WinUI 3 approximate) design colors for one theme/surface combination.
/// When <see cref="OnBackdrop"/> is true the fill/stroke/text tokens may be semi-transparent ARGB
/// values meant to composite over a DWM backdrop material; when false they are pre-blended opaque
/// colors safe for plain GDI/GDI+ rendering without alpha.
/// </summary>
public sealed record FluentTokens
{
    public required bool IsDark { get; init; }
    public required bool OnBackdrop { get; init; }
    public required Color Background { get; init; }
    public required Color CardFill { get; init; }
    public required Color CardStroke { get; init; }
    public required Color ControlFill { get; init; }
    public required Color ControlFillHover { get; init; }
    public required Color ControlFillPressed { get; init; }
    public required Color ControlFillDisabled { get; init; }
    public required Color ControlStroke { get; init; }
    public required Color ControlStrokeBottom { get; init; }
    public required Color ControlStrongStroke { get; init; }
    public required Color SubtleHover { get; init; }
    public required Color SubtlePressed { get; init; }
    public required Color TextPrimary { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color TextTertiary { get; init; }
    public required Color TextDisabled { get; init; }
    public required Color TextOnAccent { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentPressed { get; init; }
    public required Color AccentText { get; init; }
    public required Color MeterTrack { get; init; }
    public required Color Warning { get; init; }
    public required Color Danger { get; init; }
    public required Color Success { get; init; }
}

/// <summary>
/// Single source of truth for the Windows 11 Fluent design tokens used across the app:
/// colors (dark/light, on-backdrop/opaque), the system accent, the type ramp, and shared geometry.
/// </summary>
public static class FluentTheme
{
    /// <summary>Corner radius in pixels (at 96 dpi) for buttons, text boxes and other controls.</summary>
    public const int ControlCornerRadius = 4;

    /// <summary>Corner radius in pixels (at 96 dpi) for cards and grouped surfaces.</summary>
    public const int CardCornerRadius = 8;

    /// <summary>Corner radius in pixels (at 96 dpi) for overlays (popups, flyouts).</summary>
    public const int OverlayCornerRadius = 8;

    private const string TextFontFamily = "Segoe UI Variable Text";
    private const string TextSemiboldFontFamily = "Segoe UI Variable Text Semibold";
    private const string DisplaySemiboldFontFamily = "Segoe UI Variable Display Semib";
    private const string FallbackSemiboldFontFamily = "Segoe UI Semibold";
    private const string FallbackFontFamily = "Segoe UI";

    private static readonly Color DefaultAccent = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
    private static Color? cachedAccent;
    private static (Color Light2, Color Dark1)? cachedAccentVariants;

    /// <summary>
    /// True while the opt-in "vibes" appearance is enabled; when false the token sets below are
    /// exactly the stock Fluent ones.
    /// </summary>
    /// <remarks>
    /// This layer OBSERVES <see cref="UiSettings"/> rather than being written by it. The previous
    /// direction made the settings type — which the UI-free logic layer wants to share — depend on
    /// the UI theme, which blocks reusing it outside WinForms. Seeding happens in the static
    /// constructor, so the value is correct from the first read regardless of startup order.
    /// </remarks>
    public static bool VibesActive { get; private set; }

    static FluentTheme()
    {
        VibesActive = UiSettings.Load().VibesEnabled;
        UiSettings.Changed += (_, _) => VibesActive = UiSettings.Load().VibesEnabled;
    }

    /// <summary>
    /// Builds the token set for the requested theme. When <paramref name="onBackdrop"/> is false
    /// every translucent token is alpha-composited over the theme Background so the result is
    /// fully opaque (fallback path for Windows 10 / pre-22H2 and for opaque window bodies).
    /// </summary>
    public static FluentTokens Get(bool isDark, bool onBackdrop)
    {
        if (VibesActive)
        {
            return GetVibeTokens(onBackdrop);
        }

        // WinUI uses the theme-adjusted accent for fills and text (SystemAccentColorLight2
        // in dark, SystemAccentColorDark1 in light), never the raw accent — raw dark-toned
        // accents (gold, olive, ...) are illegible on dark surfaces.
        var accent = GetAccentFill(isDark);
        var background = isDark
            ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        Color Resolve(Color color) => onBackdrop ? color : BlendOver(color, background);

        var accentText = accent;
        Color textOnAccent;
        if (Luminance(accent) > 0.6f)
        {
            // Bright user accents (gold, light teal, ...) need dark text in either theme,
            // matching how Windows itself flips caption text on light accent colors.
            var nearBlack = Argb(0xF2000000);
            textOnAccent = onBackdrop ? nearBlack : BlendOver(nearBlack, accent);
        }
        else
        {
            textOnAccent = Color.White;
        }

        if (isDark)
        {
            return new FluentTokens
            {
                IsDark = true,
                OnBackdrop = onBackdrop,
                Background = background,
                CardFill = Resolve(Argb(0x0FFFFFFF)),
                CardStroke = Resolve(Argb(0x14FFFFFF)),
                ControlFill = Resolve(Argb(0x0FFFFFFF)),
                ControlFillHover = Resolve(Argb(0x15FFFFFF)),
                ControlFillPressed = Resolve(Argb(0x08FFFFFF)),
                ControlFillDisabled = Resolve(Argb(0x0BFFFFFF)),
                ControlStroke = Resolve(Argb(0x18FFFFFF)),
                ControlStrokeBottom = Resolve(Argb(0x20FFFFFF)),
                ControlStrongStroke = Resolve(Argb(0x8BFFFFFF)),
                SubtleHover = Resolve(Argb(0x0FFFFFFF)),
                SubtlePressed = Resolve(Argb(0x0AFFFFFF)),
                TextPrimary = Resolve(Argb(0xFFFFFFFF)),
                TextSecondary = Resolve(Argb(0xC5FFFFFF)),
                TextTertiary = Resolve(Argb(0x87FFFFFF)),
                TextDisabled = Resolve(Argb(0x5DFFFFFF)),
                TextOnAccent = textOnAccent,
                Accent = accent,
                AccentHover = Resolve(Color.FromArgb(0xE6, accent)),
                AccentPressed = Resolve(Color.FromArgb(0xCC, accent)),
                AccentText = accentText,
                MeterTrack = Resolve(Argb(0x15FFFFFF)),
                Warning = Argb(0xFFF8A800),
                Danger = Argb(0xFFFF99A4),
                Success = Argb(0xFF6CCB5F)
            };
        }

        return new FluentTokens
        {
            IsDark = false,
            OnBackdrop = onBackdrop,
            Background = background,
            CardFill = Resolve(Argb(0xB3FFFFFF)),
            CardStroke = Resolve(Argb(0x0F000000)),
            ControlFill = Resolve(Argb(0xB3FFFFFF)),
            ControlFillHover = Resolve(Argb(0x80F9F9F9)),
            ControlFillPressed = Resolve(Argb(0x4DF9F9F9)),
            ControlFillDisabled = Resolve(Argb(0x4DF9F9F9)),
            ControlStroke = Resolve(Argb(0x0F000000)),
            ControlStrokeBottom = Resolve(Argb(0x29000000)),
            ControlStrongStroke = Resolve(Argb(0x72000000)),
            SubtleHover = Resolve(Argb(0x0A000000)),
            SubtlePressed = Resolve(Argb(0x06000000)),
            TextPrimary = Resolve(Argb(0xE4000000)),
            TextSecondary = Resolve(Argb(0x9E000000)),
            TextTertiary = Resolve(Argb(0x72000000)),
            TextDisabled = Resolve(Argb(0x5C000000)),
            TextOnAccent = textOnAccent,
            Accent = accent,
            AccentHover = Resolve(Color.FromArgb(0xE6, accent)),
            AccentPressed = Resolve(Color.FromArgb(0xCC, accent)),
            AccentText = accentText,
            MeterTrack = Resolve(Argb(0x14000000)),
            Warning = Argb(0xFF9D5D00),
            Danger = Argb(0xFFC42B1C),
            Success = Argb(0xFF0F7B0F)
        };
    }

    /// <summary>
    /// Raw surface recipe for one vibe background style: canvas, surface/stroke/text tints
    /// (ARGB uints, composited over the canvas on opaque paths) and the accent pair.
    /// </summary>
    private readonly record struct VibePalette(
        Color Background,
        uint CardFill,
        uint CardStroke,
        uint ControlFill,
        uint ControlFillHover,
        uint ControlFillPressed,
        uint ControlStroke,
        uint ControlStrokeBottom,
        uint ControlStrongStroke,
        uint TextPrimary,
        uint TextSecondary,
        uint TextTertiary,
        uint TextDisabled,
        uint MeterTrack,
        Color AccentText);

    /// <summary>
    /// The "vibes" token set. Always dark; the Windows accent is deliberately ignored so the
    /// palette stays coherent. The surface family is baked in as Graphite: hue-free charcoal
    /// with a steel-blue accent.
    /// </summary>
    private static FluentTokens GetVibeTokens(bool onBackdrop)
    {
        var palette = new VibePalette(
            Color.FromArgb(0xFF, 0x15, 0x15, 0x17),
            0x10FFFFFF, 0x1CFFFFFF,
            0x0FFFFFFF, 0x18FFFFFF, 0x0AFFFFFF,
            0x1AFFFFFF, 0x24FFFFFF, 0x8BFFFFFF,
            0xFFFAFAFA, 0xC5E8E8EA, 0x87C4C6CA, 0x5DB4B6BA,
            0x16FFFFFF,
            Color.FromArgb(0xFF, 0x9C, 0xC4, 0xFF));

        var background = palette.Background;
        var accent = VibeTheme.StyleAccent;
        Color Resolve(Color color) => onBackdrop ? color : BlendOver(color, background);
        Color R(uint argb) => Resolve(Argb(argb));

        return new FluentTokens
        {
            IsDark = true,
            OnBackdrop = onBackdrop,
            Background = background,
            CardFill = R(palette.CardFill),
            CardStroke = R(palette.CardStroke),
            ControlFill = R(palette.ControlFill),
            ControlFillHover = R(palette.ControlFillHover),
            ControlFillPressed = R(palette.ControlFillPressed),
            ControlFillDisabled = R(palette.ControlFillPressed),
            ControlStroke = R(palette.ControlStroke),
            ControlStrokeBottom = R(palette.ControlStrokeBottom),
            ControlStrongStroke = R(palette.ControlStrongStroke),
            SubtleHover = R(palette.ControlFill),
            SubtlePressed = R(palette.ControlFillPressed),
            TextPrimary = R(palette.TextPrimary),
            TextSecondary = R(palette.TextSecondary),
            TextTertiary = R(palette.TextTertiary),
            TextDisabled = R(palette.TextDisabled),
            TextOnAccent = Color.White,
            Accent = accent,
            AccentHover = Resolve(Color.FromArgb(0xE6, accent)),
            AccentPressed = Resolve(Color.FromArgb(0xCC, accent)),
            AccentText = palette.AccentText,
            MeterTrack = R(palette.MeterTrack),
            Warning = VibeTheme.WarnStart,
            Danger = VibeTheme.DangerStart,
            Success = Argb(0xFF46E0A3)
        };
    }

    /// <summary>
    /// Reads the user's accent color from HKCU\Software\Microsoft\Windows\DWM (AccentColor,
    /// DWORD in ABGR byte order). Cached after the first read; falls back to #FF0078D4.
    /// </summary>
    public static Color GetSystemAccent()
    {
        if (cachedAccent is { } cached)
        {
            return cached;
        }

        var accent = ReadAccentColor();
        cachedAccent = accent;
        return accent;
    }

    /// <summary>Re-reads the system accent from the registry (e.g. after a settings change broadcast).</summary>
    public static void RefreshAccent()
    {
        cachedAccent = ReadAccentColor();
        cachedAccentVariants = ReadAccentVariants();
    }

    /// <summary>
    /// Theme-adjusted accent for fills and accent text: Windows' precomputed
    /// SystemAccentColorLight2 in dark mode, SystemAccentColorDark1 in light mode,
    /// falling back to a computed blend when the palette is unavailable.
    /// </summary>
    public static Color GetAccentFill(bool isDark)
    {
        if (VibesActive)
        {
            return VibeTheme.StyleAccent;
        }

        cachedAccentVariants ??= ReadAccentVariants();
        var variants = cachedAccentVariants.Value;
        return isDark ? variants.Light2 : variants.Dark1;
    }

    /// <summary>Blends <paramref name="color"/> toward white by <paramref name="amount"/> (0..1), preserving alpha.</summary>
    public static Color Lighten(Color color, float amount) => BlendToward(color, Color.White, amount);

    /// <summary>Blends <paramref name="color"/> toward black by <paramref name="amount"/> (0..1), preserving alpha.</summary>
    public static Color Darken(Color color, float amount) => BlendToward(color, Color.Black, amount);

    /// <summary>
    /// Rotates the hue of <paramref name="color"/> by <paramref name="degrees"/>, preserving
    /// saturation, lightness and alpha. Used to derive hue-distinct chart series from the accent.
    /// </summary>
    public static Color ShiftHue(Color color, float degrees)
    {
        var hue = (color.GetHue() + degrees) % 360f;
        if (hue < 0f)
        {
            hue += 360f;
        }

        var saturation = color.GetSaturation();
        var lightness = color.GetBrightness();

        var chroma = (1f - Math.Abs((2f * lightness) - 1f)) * saturation;
        var second = chroma * (1f - Math.Abs(((hue / 60f) % 2f) - 1f));
        var match = lightness - (chroma / 2f);

        var (r, g, b) = (int)(hue / 60f) switch
        {
            0 => (chroma, second, 0f),
            1 => (second, chroma, 0f),
            2 => (0f, chroma, second),
            3 => (0f, second, chroma),
            4 => (second, 0f, chroma),
            _ => (chroma, 0f, second),
        };

        return Color.FromArgb(
            color.A,
            (int)Math.Round((r + match) * 255f),
            (int)Math.Round((g + match) * 255f),
            (int)Math.Round((b + match) * 255f));
    }

    /// <summary>Caption: 12px (9pt) Regular, Segoe UI Variable Text. Caller owns disposal.</summary>
    public static Font CaptionFont(float scale) => CreateTextFont(9f * scale, semiBold: false);

    /// <summary>Body: 14px (10.5pt) Regular, Segoe UI Variable Text. Caller owns disposal.</summary>
    public static Font BodyFont(float scale) => CreateTextFont(10.5f * scale, semiBold: false);

    /// <summary>BodyStrong: 14px (10.5pt) SemiBold, Segoe UI Variable Text. Caller owns disposal.</summary>
    public static Font BodyStrongFont(float scale) => CreateTextFont(10.5f * scale, semiBold: true);

    /// <summary>Subtitle: 20px (15pt) SemiBold, Segoe UI Variable Display. Caller owns disposal.</summary>
    public static Font SubtitleFont(float scale) => CreateDisplayFont(15f * scale);

    /// <summary>Title: 28px (21pt) SemiBold, Segoe UI Variable Display. Caller owns disposal.</summary>
    public static Font TitleFont(float scale) => CreateDisplayFont(21f * scale);

    /// <summary>
    /// Builds a rounded-rectangle <see cref="GraphicsPath"/>. Radius is clamped to half the
    /// smaller dimension; radius &lt;= 0 yields a plain rectangle. Caller owns disposal.
    /// </summary>
    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0f || bounds.Width <= 0f || bounds.Height <= 0f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180f, 90f);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270f, 90f);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0f, 90f);
        arc.X = bounds.X;
        path.AddArc(arc, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    private static (Color Light2, Color Dark1) ReadAccentVariants()
    {
        // Explorer stores the full Windows-computed accent ramp as 8 RGBA entries:
        // Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, reserved.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 32)
            {
                var light2 = Color.FromArgb(0xFF, palette[4], palette[5], palette[6]);
                var dark1 = Color.FromArgb(0xFF, palette[16], palette[17], palette[18]);
                if (light2.ToArgb() != Color.Black.ToArgb() || dark1.ToArgb() != Color.Black.ToArgb())
                {
                    return (light2, dark1);
                }
            }
        }
        catch
        {
            // Palette unavailable; fall back to computed variants below.
        }

        var accent = GetSystemAccent();
        return (Lighten(accent, 0.30f), Darken(accent, 0.15f));
    }

    private static Color ReadAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int raw)
            {
                var abgr = unchecked((uint)raw);
                return Color.FromArgb(
                    0xFF,
                    (int)(abgr & 0xFF),
                    (int)((abgr >> 8) & 0xFF),
                    (int)((abgr >> 16) & 0xFF));
            }
        }
        catch
        {
            // Registry unavailable; use the default Windows accent.
        }

        return DefaultAccent;
    }

    private static Font CreateTextFont(float points, bool semiBold)
    {
        if (!semiBold)
        {
            return TryCreateFont(TextFontFamily, points, FontStyle.Regular)
                ?? new Font(FallbackFontFamily, points, FontStyle.Regular, GraphicsUnit.Point);
        }

        return TryCreateFont(TextSemiboldFontFamily, points, FontStyle.Regular)
            ?? TryCreateFont(FallbackSemiboldFontFamily, points, FontStyle.Regular)
            ?? new Font(FallbackFontFamily, points, FontStyle.Bold, GraphicsUnit.Point);
    }

    private static Font CreateDisplayFont(float points)
    {
        return TryCreateFont(DisplaySemiboldFontFamily, points, FontStyle.Regular)
            ?? TryCreateFont(FallbackSemiboldFontFamily, points, FontStyle.Regular)
            ?? new Font(FallbackFontFamily, points, FontStyle.Bold, GraphicsUnit.Point);
    }

    private static Font? TryCreateFont(string family, float points, FontStyle style)
    {
        try
        {
            var font = new Font(family, points, style, GraphicsUnit.Point);
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

    private static Color Argb(uint argb) => Color.FromArgb(unchecked((int)argb));

    private static Color BlendOver(Color over, Color under)
    {
        if (over.A == 0xFF)
        {
            return over;
        }

        var alpha = over.A / 255f;
        var inverse = 1f - alpha;
        return Color.FromArgb(
            0xFF,
            (int)Math.Round((over.R * alpha) + (under.R * inverse)),
            (int)Math.Round((over.G * alpha) + (under.G * inverse)),
            (int)Math.Round((over.B * alpha) + (under.B * inverse)));
    }

    private static Color BlendToward(Color color, Color target, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            color.A,
            (int)Math.Round(color.R + ((target.R - color.R) * amount)),
            (int)Math.Round(color.G + ((target.G - color.G) * amount)),
            (int)Math.Round(color.B + ((target.B - color.B) * amount)));
    }

    private static float Luminance(Color color) =>
        ((0.2126f * color.R) + (0.7152f * color.G) + (0.0722f * color.B)) / 255f;
}
