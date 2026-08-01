using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace CodexBarWindows;

/// <summary>
/// Stores the OpenCode Go browser session for the current Windows user. The Cookie header is a
/// live credential, so it is protected with DPAPI; the optional workspace id is not secret.
/// </summary>
public static class OpenCodeGoSettings
{
    private const string SettingsKeyPath = @"Software\CodexBarWindows";
    private const string CookieHeaderValueName = "OpenCodeGoCookieHeader";
    private const string ProtectedCookieHeaderValueName = "OpenCodeGoCookieHeaderProtected";
    private const string WorkspaceIdValueName = "OpenCodeGoWorkspaceId";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexBarWindows.OpenCodeGoCookieHeader.v1");

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

        // One-way migration path for any development build that wrote the early plaintext value.
        return key.GetValue(CookieHeaderValueName) as string ?? string.Empty;
    }

    public static string LoadWorkspaceId()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
        return key?.GetValue(WorkspaceIdValueName) as string ?? string.Empty;
    }

    public static void Save(string cookieHeader, string? workspaceId)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);

        var normalizedCookie = OpenCodeGoUsageReader.NormalizeCookieHeader(cookieHeader);
        if (string.IsNullOrWhiteSpace(normalizedCookie))
        {
            key.DeleteValue(ProtectedCookieHeaderValueName, throwOnMissingValue: false);
            key.DeleteValue(CookieHeaderValueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(ProtectedCookieHeaderValueName, Protect(normalizedCookie), RegistryValueKind.String);
            key.DeleteValue(CookieHeaderValueName, throwOnMissingValue: false);
        }

        var normalizedWorkspace = OpenCodeGoUsageReader.NormalizeWorkspaceId(workspaceId);
        if (normalizedWorkspace is null)
        {
            key.DeleteValue(WorkspaceIdValueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(WorkspaceIdValueName, normalizedWorkspace, RegistryValueKind.String);
        }
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
