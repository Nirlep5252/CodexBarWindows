using Microsoft.Win32;

namespace CodexBarWindows;

/// <summary>
/// Start-with-Windows, backed by the per-user HKCU Run key.
/// </summary>
/// <remarks>
/// The value NAME is a parameter rather than a constant because two shells (the WinForms app and
/// the WinUI 3 rewrite) are installed side by side during the migration. They must not share one
/// Run value: whichever was toggled last would silently replace the other's autostart entry, and
/// disabling autostart in one would delete the other's. At cutover the WinUI shell drops its
/// suffix and inherits <see cref="AppInfo.AppName"/> - see <c>CodexBar.WinUI/ShellIdentity.cs</c>.
/// </remarks>
public static class StartupSettings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Start-with-Windows for the default (WinForms) shell.</summary>
    public static bool IsEnabled => IsEnabledFor(AppInfo.AppName);

    /// <summary>Start-with-Windows for the default (WinForms) shell.</summary>
    public static void SetEnabled(bool enabled) => SetEnabledFor(AppInfo.AppName, enabled);

    public static bool IsEnabledFor(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) is string value &&
            !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabledFor(string valueName, bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            // Environment.ProcessPath rather than WinForms' Application.ExecutablePath — see
            // GitHubReleaseUpdater for why that dependency was easy to miss. It is also what
            // makes this portable: the WinUI shell registers its own exe with no changes here.
            var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            key.SetValue(valueName, $"\"{executablePath}\"", RegistryValueKind.String);
            return;
        }

        key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
