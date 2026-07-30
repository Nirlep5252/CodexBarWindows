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
                CursorEnabled = ReadEnabled(key, CursorEnabledValueName)
            };
        }
        catch
        {
            return new UiSettings();
        }
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
