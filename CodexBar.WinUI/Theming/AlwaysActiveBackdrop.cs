using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CodexBar.WinUI;

internal enum BackdropKind
{
    Mica,
    MicaAlt,
    Acrylic
}

/// <summary>
/// A system backdrop that keeps rendering its ACTIVE material even while the window is not
/// focused.
/// <para>
/// The stock <c>MicaBackdrop</c>/<c>DesktopAcrylicBackdrop</c> fall back to their inactive
/// (flat, washed out) appearance the moment focus leaves - which happens constantly for a tray
/// flyout, e.g. while its own context menu is up. The switch is driven by
/// <see cref="SystemBackdropConfiguration.IsInputActive"/>; the configuration handed out by
/// <c>GetDefaultSystemBackdropConfiguration</c> is owned by XAML and re-tracks real activation,
/// so pinning the flag on it does not stick. This type owns its configuration instead, pins
/// <c>IsInputActive</c>, and mirrors only the theme-related fields from the default one.
/// </para>
/// </summary>
internal sealed partial class AlwaysActiveBackdrop(BackdropKind kind) : SystemBackdrop
{
    private ISystemBackdropControllerWithTargets? controller;
    private SystemBackdropConfiguration? configuration;

    public static bool IsSupported(BackdropKind kind) => kind == BackdropKind.Acrylic
        ? DesktopAcrylicController.IsSupported()
        : MicaController.IsSupported();

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        configuration = new SystemBackdropConfiguration { IsInputActive = true };
        SyncThemeFromDefault(connectedTarget, xamlRoot);

        controller = kind switch
        {
            BackdropKind.Acrylic => new DesktopAcrylicController(),
            BackdropKind.MicaAlt => new MicaController { Kind = MicaKind.BaseAlt },
            _ => new MicaController { Kind = MicaKind.Base }
        };

        controller.SetSystemBackdropConfiguration(configuration);
        controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
        => SyncThemeFromDefault(target, xamlRoot);

    private void SyncThemeFromDefault(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        if (configuration is null)
        {
            return;
        }

        var defaults = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        configuration.Theme = defaults.Theme;
        configuration.IsHighContrast = defaults.IsHighContrast;
        configuration.HighContrastBackgroundColor = defaults.HighContrastBackgroundColor;
        // IsInputActive is deliberately NOT copied - that is the whole point of this type.
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        controller?.Dispose();
        controller = null;
        configuration = null;
    }
}
