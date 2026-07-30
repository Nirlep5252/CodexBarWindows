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

    public static event EventHandler? Changed;

    public static UiSettings Load()
    {
        var settings = LoadCore();
        FluentTheme.VibesActive = settings.VibesEnabled;
        return settings;
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

            return new UiSettings
            {
                Theme = theme,
                Material = material,
                TintOpacityPercent = tint,
                VibesEnabled = vibes
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
        }

        FluentTheme.VibesActive = VibesEnabled;
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
