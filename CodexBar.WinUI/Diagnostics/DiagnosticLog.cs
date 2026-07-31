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

    /// <summary>
    /// Where a CRASH is recorded when ordinary diagnostics are off.
    /// </summary>
    /// <remarks>
    /// This shell is UNPACKAGED, so an unhandled exception on the UI thread fail-fasts the
    /// process: no dialog, no Windows Error Reporting bucket the user can read, and - until this
    /// existed - no trace at all unless they happened to have CODEXBAR_WINUI_DIAG set BEFORE the
    /// crash, which nobody ever does. A crash is the one event always worth a line on disk, so
    /// it gets a file of its own rather than relaxing the opt-in rule for everything else.
    /// </remarks>
    private static readonly string CrashPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexbar-winui-crash.log");

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

        Append(Path, format, args);
    }

    /// <summary>
    /// Writes unconditionally: to the configured diagnostic log when there is one, otherwise to
    /// <see cref="CrashPath"/>. Reserved for crashes - see the field remarks. Everything else
    /// must keep going through <see cref="Write"/> so diagnostics stay off by default.
    /// </summary>
    public static void WriteCrash(string format, params object?[] args) =>
        Append(Path ?? CrashPath, format, args);

    private static void Append(string path, string format, params object?[] args)
    {
        string stamped;
        try
        {
            var line = string.Format(CultureInfo.InvariantCulture, format, args);
            stamped = string.Format(
                CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} {1}{2}",
                DateTime.Now,
                line,
                Environment.NewLine);
        }
        catch (FormatException)
        {
            // A crash message is built from exception text that can contain stray braces, and a
            // logger that throws while reporting a crash loses the very report it was called for.
            stamped = string.Format(
                CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} {1}{2}",
                DateTime.Now,
                format,
                Environment.NewLine);
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(path, stamped);
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
