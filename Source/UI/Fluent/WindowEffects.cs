using System.Runtime.InteropServices;

namespace CodexBarWindows;

/// <summary>
/// DWM system backdrop materials (DWMWA_SYSTEMBACKDROP_TYPE values, Windows 11 22H2+).
/// </summary>
public enum SystemBackdrop
{
    /// <summary>No backdrop material (DWMSBT_NONE).</summary>
    None = 1,

    /// <summary>Mica — for long-lived main windows (DWMSBT_MAINWINDOW).</summary>
    Mica = 2,

    /// <summary>Acrylic — for transient surfaces like popups/flyouts (DWMSBT_TRANSIENTWINDOW).</summary>
    Acrylic = 3,

    /// <summary>Mica Alt / tabbed (DWMSBT_TABBEDWINDOW).</summary>
    Tabbed = 4
}

/// <summary>
/// DWM window composition helpers: system backdrops, frame extension, rounded corners and
/// immersive dark mode.
/// <para>
/// WARNING: never combine a DWM backdrop with <c>Form.Opacity != 1.0</c> or
/// <c>Form.TransparencyKey</c> — both force WS_EX_LAYERED on the window, which disables the
/// backdrop entirely. Entrance animations on backdrop windows must animate position, not opacity.
/// </para>
/// </summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpDefault = 0;
    private const int DwmwcpRound = 2;

    /// <summary>True on Windows 11 (build 22000) or later.</summary>
    public static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>True when DWMWA_SYSTEMBACKDROP_TYPE is available (Windows 11 22H2, build 22621+).</summary>
    public static bool IsBackdropSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>
    /// Applies a system backdrop material to the window. Returns false (no-op) when the OS does
    /// not support backdrops or DWM rejects the call. For the backdrop to show through the client
    /// area you must also call <see cref="ExtendFrameIntoClientArea"/> and paint the client area
    /// with alpha-0 pixels (GDI+ <c>Graphics.Clear(Color.Transparent)</c>).
    /// </summary>
    public static bool TryApplyBackdrop(IntPtr hwnd, SystemBackdrop type)
    {
        if (!IsBackdropSupported)
        {
            return false;
        }

        var value = (int)type;
        return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref value, sizeof(int)) == 0;
    }

    /// <summary>
    /// Extends the window frame into the entire client area (MARGINS of -1) so the backdrop
    /// material is visible wherever client pixels have alpha 0. Remember: GDI drawing
    /// (TextRenderer, native controls) writes alpha 0 and becomes see-through — use GDI+ only.
    /// </summary>
    public static void ExtendFrameIntoClientArea(IntPtr hwnd)
    {
        var margins = new Margins
        {
            Left = -1,
            Right = -1,
            Top = -1,
            Bottom = -1
        };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>
    /// Sets DWMWA_WINDOW_CORNER_PREFERENCE (DWMWCP_ROUND or DWMWCP_DEFAULT). On Windows 11 a
    /// rounded frameless popup also receives the standard DWM shadow, so do not add a custom
    /// Form.Region or CS_DROPSHADOW on that path. No-op before Windows 11.
    /// </summary>
    public static void SetRoundedCorners(IntPtr hwnd, bool round)
    {
        if (!IsWindows11)
        {
            return;
        }

        var preference = round ? DwmwcpRound : DwmwcpDefault;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>Sets DWMWA_USE_IMMERSIVE_DARK_MODE so the non-client area matches the app theme.</summary>
    public static void SetImmersiveDarkMode(IntPtr hwnd, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return;
        }

        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins pMarInset);
}
