using System;
using System.Collections.Generic;
using CodexBarWindows;
using Microsoft.UI.Xaml;
using SkiaSharp;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CodexBar.WinUI;

/// <summary>
/// The colours the LiveCharts surfaces need, expressed as SkiaSharp colours.
/// </summary>
/// <remarks>
/// <para>
/// A direct port of the WinForms <c>UsageGraphsForm.SpendCategoryColor</c> rules, including the
/// deliberate ones: "fast" never uses the warning amber (a gold system accent would make every
/// fast segment merge into the accent), and the secondary series is a HUE-ROTATED accent so
/// adjacent stacked segments differ in hue rather than only in lightness.
/// </para>
/// <para>
/// Built from a specific element's <see cref="FrameworkElement.ActualTheme"/>, exactly like
/// <see cref="FlyoutPalette"/> - reading brushes out of <c>Application.Current.Resources</c>
/// would freeze them to the app theme. Consumers rebuild on <c>ActualThemeChanged</c>.
/// </para>
/// </remarks>
internal sealed class ChartPalette
{
    private readonly IReadOnlyDictionary<string, string> overrides;

    private ChartPalette(bool isDark, IReadOnlyDictionary<string, string> overrides)
    {
        IsDark = isDark;
        this.overrides = overrides;

        Accent = ToSkia(SystemAccent(isDark));
        Success = isDark ? new SKColor(0x6C, 0xCB, 0x5F) : new SKColor(0x0F, 0x7B, 0x0F);
        Warning = isDark ? new SKColor(0xFF, 0xC8, 0x3D) : new SKColor(0x9D, 0x5D, 0x00);
        Danger = isDark ? new SKColor(0xFF, 0x7A, 0x7A) : new SKColor(0xC4, 0x2B, 0x1C);

        var shifted = ShiftHue(Accent, 60f);
        SeriesAlt = isDark ? Lighten(shifted, 0.20f) : Darken(shifted, 0.10f);

        Text = isDark ? new SKColor(0xFF, 0xFF, 0xFF) : new SKColor(0x1A, 0x1A, 0x1A);
        SecondaryText = isDark ? new SKColor(0xC5, 0xC5, 0xC5) : new SKColor(0x5D, 0x5D, 0x5D);
        Separator = isDark ? new SKColor(0xFF, 0xFF, 0xFF, 0x18) : new SKColor(0x00, 0x00, 0x00, 0x16);
        // Deliberately NOT the card colour: the tooltip is drawn over the card, so a matching
        // tone made it invisible (verified on screen - the text floated with no surface).
        TooltipBackground = isDark ? new SKColor(0x3D, 0x3D, 0x3D) : new SKColor(0xFF, 0xFF, 0xFF);
        Track = isDark ? new SKColor(0xFF, 0xFF, 0xFF, 0x14) : new SKColor(0x00, 0x00, 0x00, 0x10);
    }

    public bool IsDark { get; }

    public SKColor Accent { get; }

    public SKColor Success { get; }

    public SKColor Warning { get; }

    public SKColor Danger { get; }

    /// <summary>Hue-rotated accent used for the second series in a stack.</summary>
    public SKColor SeriesAlt { get; }

    public SKColor Text { get; }

    public SKColor SecondaryText { get; }

    public SKColor Separator { get; }

    public SKColor TooltipBackground { get; }

    /// <summary>The unfilled part of a model row's bar.</summary>
    public SKColor Track { get; }

    /// <summary>
    /// Builds the palette for one element's resolved theme, with the user's per-model colour
    /// overrides folded in.
    /// </summary>
    /// <remarks>
    /// The overrides are PASSED IN rather than read from <c>AppTheme.Settings</c> here, keeping
    /// this type's only dependency the element it is themed from - the same UI → settings
    /// direction <see cref="UiSettings"/> documents.
    /// </remarks>
    public static ChartPalette For(FrameworkElement element, IReadOnlyDictionary<string, string>? overrides = null) =>
        new(
            element.ActualTheme == ElementTheme.Dark ||
                (element.ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark),
            overrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this label's colour was chosen by the user. A picked colour is used EXACTLY as
    /// picked, so callers skip <see cref="Nudge"/> for it - a silently lightened swatch would not
    /// match the hex the settings page shows.
    /// </summary>
    public bool IsOverridden(string label) => TryGetOverride(label, out _);

    /// <summary>
    /// The colour for one spend category ("gpt-5.5", "opus fast", "regular", …). Ported rule for
    /// rule from the WinForms chart so the two apps colour the same data the same way.
    /// </summary>
    public SKColor ForCategory(string label)
    {
        // FIRST, before the "fast" short-circuit below: that rule returns for any label merely
        // CONTAINING "fast", so an override on "gpt-5.3 fast" would otherwise be unreachable.
        if (TryGetOverride(label, out var chosen))
        {
            return chosen;
        }

        var normalized = label.Replace(" fast", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (label.Contains("fast", StringComparison.OrdinalIgnoreCase))
        {
            return SeriesAlt;
        }

        if (normalized.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("claude-opus", StringComparison.OrdinalIgnoreCase) ||
            normalized == "regular")
        {
            return Accent;
        }

        if (normalized.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("claude-sonnet", StringComparison.OrdinalIgnoreCase))
        {
            return SeriesAlt;
        }

        if (normalized.Contains("gpt-5.3", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("claude-haiku", StringComparison.OrdinalIgnoreCase))
        {
            return Success;
        }

        if (normalized.Contains("gpt-5.2", StringComparison.OrdinalIgnoreCase))
        {
            return Danger;
        }

        SKColor[] palette = [Accent, Success, SeriesAlt, Warning];
        return palette[StableColorIndex(normalized, palette.Length)];
    }

    /// <summary>
    /// Parses a stored <c>"#RRGGBB"</c> override. Anything else is treated as absent so a corrupt
    /// entry degrades to the automatic colour instead of throwing while a chart is being drawn.
    /// </summary>
    public static bool TryParseHex(string? hex, out SKColor color)
    {
        color = default;
        if (UiSettings.NormalizeHexColor(hex) is not { } normalized)
        {
            return false;
        }

        color = new SKColor(
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
        return true;
    }

    public static string ToHex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private bool TryGetOverride(string label, out SKColor color)
    {
        color = default;
        return !string.IsNullOrWhiteSpace(label) &&
            overrides.TryGetValue(label.Trim(), out var hex) &&
            TryParseHex(hex, out color);
    }

    /// <summary>
    /// Separates two categories that landed on the same colour, so a stack never shows two
    /// touching segments the eye reads as one bar. Only ever LIGHTENS/DARKENS - the category's
    /// identity colour stays recognisable.
    /// </summary>
    public static SKColor Nudge(SKColor color, int step, bool isDark)
    {
        if (step <= 0)
        {
            return color;
        }

        var amount = Math.Min(0.45f, 0.16f * step);
        return isDark ? Darken(color, amount) : Lighten(color, amount);
    }

    public static SKColor Lighten(SKColor color, float amount)
    {
        color.ToHsl(out var h, out var s, out var l);
        return SKColor.FromHsl(h, s, Math.Clamp(l + (amount * 100f), 0f, 100f), color.Alpha);
    }

    public static SKColor Darken(SKColor color, float amount)
    {
        color.ToHsl(out var h, out var s, out var l);
        return SKColor.FromHsl(h, s, Math.Clamp(l - (amount * 100f), 0f, 100f), color.Alpha);
    }

    public static SKColor ShiftHue(SKColor color, float degrees)
    {
        color.ToHsl(out var h, out var s, out var l);
        var hue = (h + degrees) % 360f;
        if (hue < 0)
        {
            hue += 360f;
        }

        return SKColor.FromHsl(hue, s, l, color.Alpha);
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

    private static SKColor ToSkia(Color color) => new(color.R, color.G, color.B, color.A);

    /// <summary>The user's Windows accent, nudged the same way the flyout meters nudge it.</summary>
    private static Color SystemAccent(bool isDark)
    {
        try
        {
            var settings = new UISettings();
            return settings.GetColorValue(isDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        }
        catch (Exception)
        {
            return isDark
                ? Color.FromArgb(0xFF, 0x60, 0xB0, 0xFF)
                : Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD);
        }
    }
}
