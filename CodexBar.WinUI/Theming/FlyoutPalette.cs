using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CodexBar.WinUI;

/// <summary>
/// The handful of colours the flyout cannot express as a XAML <c>{ThemeResource}</c>: meter
/// fills, the percent figure's "limit heat", the provider glyph tint and the reset badge.
/// </summary>
/// <remarks>
/// <para>
/// These are built in code, which is normally the phase-2 trap: a brush read from
/// <c>Application.Current.Resources</c> resolves against the APP theme and renders wrong when
/// the user forces the opposite theme. The rule that avoids it is followed here - the palette
/// is derived from a specific element's <see cref="FrameworkElement.ActualTheme"/> and every
/// consumer rebuilds its models on <c>ActualThemeChanged</c>, so nothing is ever frozen to a
/// stale theme. Anything that CAN stay a <c>{ThemeResource}</c> in XAML (all body text, card
/// fills, strokes) does.
/// </para>
/// <para>
/// Heat colours are deliberately literal rather than accent-derived: "you are nearly out" must
/// not turn green because the user picked a green accent.
/// </para>
/// </remarks>
internal sealed class FlyoutPalette
{
    /// <summary>Used-percent at which the figure turns amber.</summary>
    private const double WarnPercent = 70;

    /// <summary>Used-percent at which the figure turns red.</summary>
    private const double DangerPercent = 90;

    private FlyoutPalette(bool isDark)
    {
        IsDark = isDark;

        Accent = new SolidColorBrush(SystemAccent(isDark));
        Warning = Brush(isDark ? Color.FromArgb(0xFF, 0xFF, 0xC8, 0x3D) : Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00));
        Danger = Brush(isDark ? Color.FromArgb(0xFF, 0xFF, 0x7A, 0x7A) : Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
        Glyph = Brush(isDark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));

        // Anthropic's own mark colour: it identifies the provider, so it does not vary by theme.
        ClaudeGlyph = Brush(Color.FromArgb(0xFF, 0xD9, 0x77, 0x57));

        // xAI / Grok mark: near-white on dark chrome, near-black on light — slightly cooler than
        // the generic monochrome glyph so the X reads as its own identity next to Claude's coral.
        GrokGlyph = Brush(isDark
            ? Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2)
            : Color.FromArgb(0xFF, 0x14, 0x14, 0x14));

        OnAccentText = Brush(IsLight(SystemAccent(isDark))
            ? Color.FromArgb(0xF2, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        OnWarningText = Brush(isDark
            ? Color.FromArgb(0xF2, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    public bool IsDark { get; }

    public Brush Accent { get; }

    public Brush Warning { get; }

    public Brush Danger { get; }

    /// <summary>Provider glyph tint for marks that should read as monochrome UI (Codex, Cursor).</summary>
    public Brush Glyph { get; }

    public Brush ClaudeGlyph { get; }

    public Brush GrokGlyph { get; }

    public Brush OnAccentText { get; }

    public Brush OnWarningText { get; }

    public static FlyoutPalette For(FrameworkElement element) =>
        new(element.ActualTheme == ElementTheme.Dark ||
            (element.ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark));

    /// <summary>The meter fill and percent figure for a used-percent: accent, amber, then red.</summary>
    public Brush Heat(double usedPercent) => usedPercent switch
    {
        >= DangerPercent => Danger,
        >= WarnPercent => Warning,
        _ => Accent
    };

    private static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>
    /// The user's Windows accent, nudged for legibility: the raw accent is tuned for large
    /// fills and small percent digits need more contrast against the card.
    /// </summary>
    private static Color SystemAccent(bool isDark)
    {
        try
        {
            var settings = new UISettings();
            return settings.GetColorValue(isDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        }
        catch (Exception)
        {
            // UISettings is not guaranteed to resolve in every unpackaged host; a fixed blue
            // keeps the meters readable rather than throwing on the render path.
            return isDark
                ? Color.FromArgb(0xFF, 0x60, 0xB0, 0xFF)
                : Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD);
        }
    }

    private static bool IsLight(Color color) =>
        (((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0) > 0.6;
}
