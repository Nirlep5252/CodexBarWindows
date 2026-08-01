using System;
using CodexBarWindows;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexBar.WinUI;

/// <summary>
/// Theming foundation for the WinUI shell.
/// <para>
/// Deliberately NOT a port of the WinForms <c>FluentTheme</c>: WinUI already owns the token
/// system (system brushes, type ramp, elevation), so this type only translates the three
/// persisted user choices - <see cref="UiSettings.Theme"/>, <see cref="UiSettings.Material"/>
/// and <see cref="UiSettings.TintOpacityPercent"/> - into native WinUI concepts, and re-applies
/// them live when the settings change.
/// </para>
/// </summary>
internal static class AppTheme
{
    private static DispatcherQueue? uiQueue;

    /// <summary>
    /// The persisted settings as this shell sees them. ALWAYS read through
    /// <see cref="UiSettings.WithoutVibes"/>: vibes is not implemented here (see the hidden row
    /// in SettingsWindow.xaml), and the registry key is shared with the WinForms app, so a user
    /// who turned vibes on there arrives with <c>UiVibes=1</c> and would otherwise get
    /// <c>EffectiveMaterial</c> pinned to Acrylic and a force-dark theme from a feature this
    /// shell never renders. This property is the shell's ONLY settings instance - every window,
    /// including SettingsWindow, reads it - so neutralising here covers every consumer at once.
    /// Drop the call when VibeTheme is ported, together with un-hiding the toggle.
    /// </summary>
    public static UiSettings Settings { get; private set; } = UiSettings.Load().WithoutVibes();

    /// <summary>Raised on the UI thread after <see cref="Settings"/> is reloaded.</summary>
    public static event EventHandler? Changed;

    public static void Initialize(DispatcherQueue queue)
    {
        uiQueue = queue;
        UiSettings.Changed += OnUiSettingsChanged;
    }

    public static void Shutdown()
    {
        UiSettings.Changed -= OnUiSettingsChanged;
        uiQueue = null;
    }

    private static void OnUiSettingsChanged(object? sender, EventArgs e)
    {
        // UiSettings.Changed can be raised from any thread; every consumer touches XAML.
        uiQueue?.TryEnqueue(() =>
        {
            Settings = UiSettings.Load().WithoutVibes();
            DiagnosticLog.Write("theme reload theme={0} material={1} tint={2}", Settings.Theme, Settings.EffectiveMaterial, Settings.TintOpacityPercent);
            Changed?.Invoke(null, EventArgs.Empty);
        });
    }

    /// <summary>System / Light / Dark, expressed the way WinUI wants it.</summary>
    public static ElementTheme RequestedTheme => Settings.Theme switch
    {
        AppThemeMode.Light => ElementTheme.Light,
        AppThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>
    /// The backdrop for the configured material, or <c>null</c> for
    /// <see cref="BackdropMaterial.Solid"/> and for machines that cannot render the material.
    /// </summary>
    public static SystemBackdrop? CreateBackdrop()
    {
        var kind = Settings.EffectiveMaterial switch
        {
            BackdropMaterial.Mica => BackdropKind.Mica,
            BackdropMaterial.MicaAlt => BackdropKind.MicaAlt,
            BackdropMaterial.Acrylic => BackdropKind.Acrylic,
            _ => (BackdropKind?)null
        };

        if (kind is null)
        {
            return null;
        }

        if (AlwaysActiveBackdrop.IsSupported(kind.Value))
        {
            return new AlwaysActiveBackdrop(kind.Value);
        }

        // Mica needs Windows 11; acrylic is the universal fallback.
        return DesktopAcrylicController.IsSupported()
            ? new AlwaysActiveBackdrop(BackdropKind.Acrylic)
            : null;
    }

    /// <summary>
    /// The tint painted over the backdrop: 0% leaves the raw material, 100% is fully opaque.
    /// A window with no backdrop always gets the opaque colour, otherwise it would be
    /// see-through with nothing behind it.
    /// </summary>
    public static Brush CreateTintBrush(ElementTheme actualTheme)
    {
        var isDark = actualTheme == ElementTheme.Dark;
        var opaque = Settings.EffectiveMaterial == BackdropMaterial.Solid || CreateBackdrop() is null;
        var percent = opaque ? 100 : Math.Clamp(Settings.TintOpacityPercent, 0, 100);
        var alpha = (byte)Math.Round(percent * 255.0 / 100.0);

        // The WinUI "SolidBackgroundFillColorBase" values, so a fully tinted window is
        // indistinguishable from a stock opaque WinUI surface.
        var color = isDark
            ? Color.FromArgb(alpha, 0x20, 0x20, 0x20)
            : Color.FromArgb(alpha, 0xF3, 0xF3, 0xF3);

        return new SolidColorBrush(color);
    }

    /// <summary>
    /// Applies theme, backdrop and tint to a window in one call. <paramref name="tintLayer"/>
    /// is the element that paints the tint (it must sit behind the window's content).
    /// </summary>
    public static void Apply(Window window, FrameworkElement root, FrameworkElement tintLayer)
    {
        root.RequestedTheme = RequestedTheme;
        window.SystemBackdrop = CreateBackdrop();
        ApplyTint(root, tintLayer);

        // The system caption bar does not follow the XAML theme on its own.
        NativeWindow.SetTitleBarTheme(
            WinRT.Interop.WindowNative.GetWindowHandle(window),
            root.ActualTheme == ElementTheme.Dark);

        DiagnosticLog.Write(
            "theme applied window={0} requested={1} actual={2} material={3} backdrop={4} tint={5}%",
            window.GetType().Name,
            RequestedTheme,
            root.ActualTheme,
            Settings.EffectiveMaterial,
            window.SystemBackdrop?.GetType().Name ?? "none",
            Settings.TintOpacityPercent);
    }

    public static void ApplyTint(FrameworkElement root, FrameworkElement tintLayer)
    {
        var brush = CreateTintBrush(root.ActualTheme);
        switch (tintLayer)
        {
            case Microsoft.UI.Xaml.Controls.Panel panel:
                panel.Background = brush;
                break;
            case Microsoft.UI.Xaml.Controls.Border border:
                border.Background = brush;
                break;
            case Microsoft.UI.Xaml.Controls.Control control:
                control.Background = brush;
                break;
        }
    }
}
