using System;
using System.Runtime.InteropServices;

namespace CodexBar.WinUI;

/// <summary>
/// The handful of Win32 calls WinUI 3 does not surface: DWM corner rounding, per-monitor DPI,
/// and foreground-window ownership (used by the flyout's dismiss-on-focus-loss logic).
/// </summary>
internal static class NativeWindow
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRoundSmall = 3;   // 2 = DWMWCP_ROUND, the larger radius.

    public static void ApplyRoundedCorners(IntPtr hwnd)
    {
        var preference = DwmwcpRoundSmall;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    public static double ScaleFor(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// True when the window that currently has the foreground belongs to THIS process.
    /// <para>
    /// This is the process-level check ported from the WinForms
    /// <c>UsagePopupForm.HideIfFocusLeftProcess</c>. Checking the window instead of the
    /// process is the bug it fixes: the tray context menu, the settings window and the
    /// graphs window are all separate HWNDs, so a window-level test dismisses the flyout
    /// the moment any of them is used.
    /// </para>
    /// </summary>
    public static bool ForegroundBelongsToThisProcess()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Takes the foreground reliably. A bare <c>SetForegroundWindow</c> is refused by the
    /// foreground lock when another process owns the foreground (which is exactly the case
    /// after a click on the shell's notification area), and a flyout that never becomes
    /// foreground can never observe losing it either.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd)
        {
            return;
        }

        var thisThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero
            ? thisThread
            : GetWindowThreadProcessId(foreground, out _);

        var attached = false;
        if (foregroundThread != thisThread)
        {
            attached = AttachThreadInput(thisThread, foregroundThread, true);
        }

        try
        {
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
            _ = SetActiveWindow(hwnd);
            _ = SetFocus(hwnd);
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);
}
