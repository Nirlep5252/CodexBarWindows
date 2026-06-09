using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace CodexBarWindows;

public static class CursorSettings
{
    private const string SettingsKeyPath = @"Software\CodexBarWindows";
    private const string CookieHeaderValueName = "CursorCookieHeader";
    private const string ProtectedCookieHeaderValueName = "CursorCookieHeaderProtected";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexBarWindows.CursorCookieHeader.v1");

    public static string LoadCookieHeader()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
        if (key is null)
        {
            return string.Empty;
        }

        if (key.GetValue(ProtectedCookieHeaderValueName) is string protectedValue &&
            TryUnprotect(protectedValue, out var unprotected))
        {
            return unprotected;
        }

        return key.GetValue(CookieHeaderValueName) as string ?? string.Empty;
    }

    public static void SaveCookieHeader(string cookieHeader)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);

        var normalized = CursorUsageReader.NormalizeCookieHeader(cookieHeader);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            key.DeleteValue(ProtectedCookieHeaderValueName, throwOnMissingValue: false);
            key.DeleteValue(CookieHeaderValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ProtectedCookieHeaderValueName, Protect(normalized), RegistryValueKind.String);
        key.DeleteValue(CookieHeaderValueName, throwOnMissingValue: false);
    }

    private static string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static bool TryUnprotect(string protectedValue, out string value)
    {
        value = string.Empty;
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            value = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
