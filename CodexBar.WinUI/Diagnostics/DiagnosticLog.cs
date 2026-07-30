using System;
using System.Globalization;
using System.IO;

namespace CodexBar.WinUI;

/// <summary>
/// Opt-in file log for behaviour that cannot be observed from a screenshot - mainly the
/// tray flyout's focus/dismiss lifecycle, which has no visible trace once the window is gone.
/// Disabled unless <c>CODEXBAR_WINUI_DIAG</c> is set; the value may be a file path, or
/// <c>1</c> to use <c>%TEMP%\codexbar-winui.log</c>.
/// </summary>
internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly string? Path = ResolvePath();

    public static bool IsEnabled => Path is not null;

    private static string? ResolvePath()
    {
        var setting = Environment.GetEnvironmentVariable("CODEXBAR_WINUI_DIAG");
        if (string.IsNullOrWhiteSpace(setting))
        {
            return null;
        }

        return setting is "1" or "true"
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexbar-winui.log")
            : setting;
    }

    public static void Write(string format, params object?[] args)
    {
        if (Path is null)
        {
            return;
        }

        var line = string.Format(CultureInfo.InvariantCulture, format, args);
        var stamped = string.Format(
            CultureInfo.InvariantCulture,
            "{0:HH:mm:ss.fff} {1}{2}",
            DateTime.Now,
            line,
            Environment.NewLine);

        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, stamped);
            }
        }
        catch (IOException)
        {
            // Diagnostics must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
