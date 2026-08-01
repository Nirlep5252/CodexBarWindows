using System.Text.Json;
using Microsoft.Win32;

namespace CodexBarWindows;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public enum BackdropMaterial
{
    Acrylic,
    Mica,
    MicaAlt,
    Solid
}



/// <summary>
/// User-configurable appearance settings, persisted in HKCU\Software\CodexBarWindows.
/// <see cref="Changed"/> is raised after every <see cref="Save"/> so open windows can
/// re-apply theme, backdrop material and tint live.
/// </summary>
public sealed record UiSettings
{
    private const string SettingsKeyPath = @"Software\CodexBarWindows";
    private const string ThemeValueName = "UiTheme";
    private const string MaterialValueName = "UiMaterial";
    private const string TintOpacityValueName = "UiTintOpacityPercent";
    private const string VibesValueName = "UiVibes";
    private const string CodexEnabledValueName = "ProviderCodexEnabled";
    private const string ClaudeEnabledValueName = "ProviderClaudeEnabled";
    private const string CursorEnabledValueName = "ProviderCursorEnabled";
    private const string ChartColorsValueName = "ChartColorOverrides";

    /// <summary>
    /// Web-cased JSON, same shape as <c>CodexCliEntries</c>: one REG_SZ holding the whole map.
    /// The registry has no dictionary type, and a value-per-model would leak an unbounded number
    /// of stray values that nothing ever cleans up.
    /// </summary>
    private static readonly JsonSerializerOptions ChartColorJsonOptions = new(JsonSerializerDefaults.Web);

    public AppThemeMode Theme { get; init; } = AppThemeMode.System;

    public BackdropMaterial Material { get; init; } = BackdropMaterial.Acrylic;

    /// <summary>
    /// Opt-in "vibes" appearance: the V3 Code violet/magenta theme with celebratory motion.
    /// When false the app renders exactly as it does without the feature.
    /// </summary>
    public bool VibesEnabled { get; init; }

    /// <summary>
    /// The backdrop material actually applied: vibes always rides the stock Acrylic backdrop
    /// (the Material section is disabled while vibes are on).
    /// </summary>
    public BackdropMaterial EffectiveMaterial => VibesEnabled ? BackdropMaterial.Acrylic : Material;

    /// <summary>
    /// The same settings with <see cref="VibesEnabled"/> forced off, for a shell that does not
    /// IMPLEMENT vibes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="VibesValueName"/> lives in a registry key shared with the frozen WinForms app,
    /// so a user who ever enabled vibes there keeps <c>UiVibes=1</c> after upgrading in place.
    /// A shell without <c>VibeTheme</c> renders none of the appearance that flag promises, yet
    /// every DERIVED member here still honours it: <see cref="EffectiveMaterial"/> pins the
    /// backdrop to Acrylic (so the user's Mica/Solid choice silently stops applying) and
    /// <see cref="ResolveIsDark"/> forces dark over the theme choice. Calling this at the point
    /// the shell loads its settings makes the flag inert for that shell in ONE place, instead of
    /// leaving every consumer to remember the gate.
    /// </para>
    /// <para>
    /// Deliberately NOT applied inside <see cref="Load"/>: the WinForms shell does implement
    /// vibes and reads through the same method, and a flag it can still honour must not be
    /// erased on its behalf.
    /// </para>
    /// </remarks>
    public UiSettings WithoutVibes() => VibesEnabled ? this with { VibesEnabled = false } : this;

    /// <summary>
    /// Strength of the theme-colored tint painted over the backdrop material.
    /// 0 = pure material (maximum translucency), 100 = fully solid background.
    /// Ignored when <see cref="Material"/> is <see cref="BackdropMaterial.Solid"/>.
    /// </summary>
    public int TintOpacityPercent { get; init; } = 45;

    /// <summary>
    /// Per-tool opt-out. A disabled tool gets no tab in the flyout and is never polled, so
    /// disabling one you do not use also removes its refresh cost.
    /// </summary>
    public bool CodexEnabled { get; init; } = true;

    public bool ClaudeEnabled { get; init; } = true;

    public bool CursorEnabled { get; init; } = true;

    /// <summary>
    /// Per-model chart colour overrides: RAW category label (the <c>ProviderSpendCategory.Label</c>
    /// / <c>ProviderModelUsage.Model</c> string, lower-cased) → <c>"#RRGGBB"</c>. Anything absent
    /// keeps the automatic palette, so an empty map is the shipped behaviour.
    /// </summary>
    /// <remarks>
    /// Persisted as ONE REG_SZ value holding JSON - see <see cref="ChartColorsValueName"/>. Keys are
    /// the raw labels on purpose: the friendly labels the charts DISPLAY are produced by a lossy
    /// one-way transform, so a map keyed on those could never be matched back at render time.
    /// <para>
    /// This is a dictionary on a record, so the compiler-generated value equality degrades to
    /// reference equality for this member. Nothing compares whole <see cref="UiSettings"/>
    /// instances (every consumer compares individual scalars), so that is inert today.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string> ChartColorOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsProviderEnabled(UsageProvider provider) => provider switch
    {
        UsageProvider.Claude => ClaudeEnabled,
        UsageProvider.Cursor => CursorEnabled,
        _ => CodexEnabled
    };

    public static event EventHandler? Changed;

    /// <remarks>
    /// Deliberately does not touch the UI theme: <c>FluentTheme</c> observes <see cref="Changed"/>
    /// instead. Keeping the dependency pointing UI → settings lets this type be shared with the
    /// UI-free logic layer.
    /// </remarks>
    public static UiSettings Load()
    {
        return LoadCore();
    }

    private static UiSettings LoadCore()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
            if (key is null)
            {
                return new UiSettings();
            }

            var theme = key.GetValue(ThemeValueName) is string themeText &&
                Enum.TryParse<AppThemeMode>(themeText, ignoreCase: true, out var parsedTheme)
                    ? parsedTheme
                    : AppThemeMode.System;

            var material = key.GetValue(MaterialValueName) is string materialText &&
                Enum.TryParse<BackdropMaterial>(materialText, ignoreCase: true, out var parsedMaterial)
                    ? parsedMaterial
                    : BackdropMaterial.Acrylic;

            var tint = key.GetValue(TintOpacityValueName) is int tintValue
                ? Math.Clamp(tintValue, 0, 100)
                : 45;

            var vibes = key.GetValue(VibesValueName) is int vibesValue && vibesValue != 0;

            // Absent value means enabled: an existing install must not lose its tools.
            static bool ReadEnabled(RegistryKey key, string name)
                => key.GetValue(name) is not int value || value != 0;

            return new UiSettings
            {
                Theme = theme,
                Material = material,
                TintOpacityPercent = tint,
                VibesEnabled = vibes,
                CodexEnabled = ReadEnabled(key, CodexEnabledValueName),
                ClaudeEnabled = ReadEnabled(key, ClaudeEnabledValueName),
                CursorEnabled = ReadEnabled(key, CursorEnabledValueName),
                ChartColorOverrides = ReadChartColors(key)
            };
        }
        catch
        {
            return new UiSettings();
        }
    }

    /// <summary>
    /// Reads the colour map, dropping anything that does not parse. A corrupt or half-written
    /// value must never take the rest of the settings down with it - the charts simply fall back
    /// to the automatic palette, which is what an unset override means anyway.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadChartColors(RegistryKey key)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (key.GetValue(ChartColorsValueName) is not string json || string.IsNullOrWhiteSpace(json))
        {
            return map;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ChartColorJsonOptions);
            if (saved is null)
            {
                return map;
            }

            foreach (var pair in saved)
            {
                if (NormalizeHexColor(pair.Value) is { } hex && !string.IsNullOrWhiteSpace(pair.Key))
                {
                    map[pair.Key.Trim()] = hex;
                }
            }
        }
        catch
        {
            // Malformed JSON: ignore the whole map rather than guess at a partial one.
        }

        return map;
    }

    /// <summary>
    /// Canonicalises a stored colour to <c>"#RRGGBB"</c>, or null if it is not one. Everything
    /// downstream can then parse with a fixed-width substring instead of defending itself.
    /// </summary>
    public static string? NormalizeHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length != 6)
        {
            return null;
        }

        foreach (var character in text)
        {
            if (!Uri.IsHexDigit(character))
            {
                return null;
            }
        }

        return "#" + text.ToUpperInvariant();
    }

    public void Save()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true))
        {
            key.SetValue(ThemeValueName, Theme.ToString(), RegistryValueKind.String);
            key.SetValue(MaterialValueName, Material.ToString(), RegistryValueKind.String);
            key.SetValue(TintOpacityValueName, Math.Clamp(TintOpacityPercent, 0, 100), RegistryValueKind.DWord);
            key.SetValue(VibesValueName, VibesEnabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(CodexEnabledValueName, CodexEnabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(ClaudeEnabledValueName, ClaudeEnabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(CursorEnabledValueName, CursorEnabled ? 1 : 0, RegistryValueKind.DWord);

            // WRITTEN UNCONDITIONALLY, and read in LoadCore above: the frozen WinForms app shares
            // this key and compiles against this same type, so a value that is read but not
            // written (or the reverse) would be silently erased by whichever app saves last.
            if (ChartColorOverrides.Count == 0)
            {
                key.DeleteValue(ChartColorsValueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(
                    ChartColorsValueName,
                    JsonSerializer.Serialize(
                        ChartColorOverrides.ToDictionary(pair => pair.Key, pair => pair.Value),
                        ChartColorJsonOptions),
                    RegistryValueKind.String);
            }
        }

        // Listeners (including FluentTheme) re-read from here; this type does not push into the UI.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resolves the effective dark/light mode, honoring the System theme setting.
    /// The vibes appearance is inherently dark and overrides the theme choice while enabled.
    /// </summary>
    public bool ResolveIsDark() => VibesEnabled || Theme switch
    {
        AppThemeMode.Light => false,
        AppThemeMode.Dark => true,
        _ => IsSystemDark()
    };

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
