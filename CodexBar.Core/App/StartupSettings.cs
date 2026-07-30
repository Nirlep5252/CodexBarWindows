using Microsoft.Win32;

namespace CodexBarWindows;

public static class StartupSettings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppInfo.AppName) is string value &&
                !string.IsNullOrWhiteSpace(value);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            // Environment.ProcessPath rather than WinForms' Application.ExecutablePath — see
            // GitHubReleaseUpdater for why that dependency was easy to miss.
            var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            key.SetValue(AppInfo.AppName, $"\"{executablePath}\"", RegistryValueKind.String);
            return;
        }

        key.DeleteValue(AppInfo.AppName, throwOnMissingValue: false);
    }
}
