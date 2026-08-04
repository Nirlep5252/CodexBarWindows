using System.Text.Json;
using Microsoft.Win32;

namespace CodexBarWindows;

/// <summary>
/// One configured Grok account. <paramref name="HomePath"/> is a GROK_HOME directory - the folder
/// holding <c>auth.json</c> and <c>sessions/</c> - which is how the Grok CLI itself separates
/// accounts (<c>GROK_HOME=... grok login</c>). The default entry has none and resolves the same
/// location the CLI would.
/// </summary>
/// <remarks>
/// A home directory, not an <c>auth.json</c> path: the file alone would give live limits but no
/// history, because the 30-day insights scan reads <c>sessions/</c> next to it.
/// </remarks>
public sealed record GrokAccountEntry(string Id, string Name, string? HomePath)
{
    public bool IsDefault => string.IsNullOrWhiteSpace(HomePath);

    /// <summary>The account's Grok home, falling back to GROK_HOME then <c>~/.grok</c>.</summary>
    public string ResolveHome()
    {
        if (!string.IsNullOrWhiteSpace(HomePath))
        {
            return HomePath;
        }

        var home = Environment.GetEnvironmentVariable("GROK_HOME");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok")
            : home;
    }

    public string ResolveAuthPath() => Path.Combine(ResolveHome(), "auth.json");

    public string ResolveSessionsPath() => Path.Combine(ResolveHome(), "sessions");
}

/// <summary>
/// The configured Grok accounts, persisted alongside the Codex ones in
/// HKCU\Software\CodexBarWindows. Shaped exactly like <see cref="CodexCliSettings"/>: one REG_SZ
/// of JSON holding only the EXTRA accounts, with the default one always synthesised at the head so
/// an install that never opens this setting behaves as it always did.
/// </summary>
public static class GrokAccountSettings
{
    private const string SettingsKeyPath = @"Software\CodexBarWindows";
    private const string EntriesValueName = "GrokAccounts";

    /// <summary>Id of the synthesised entry that reads GROK_HOME / <c>~/.grok</c>.</summary>
    public const string DefaultId = "default";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<GrokAccountEntry> Load()
    {
        var entries = new List<GrokAccountEntry>
        {
            new(DefaultId, "Grok", null)
        };

        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
        if (key?.GetValue(EntriesValueName) is not string json || string.IsNullOrWhiteSpace(json))
        {
            return entries;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<List<SavedGrokAccount>>(json, JsonOptions) ?? [];
            foreach (var entry in saved)
            {
                if (string.IsNullOrWhiteSpace(entry.HomePath))
                {
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
                var name = string.IsNullOrWhiteSpace(entry.Name)
                    ? $"Grok {entries.Count + 1}"
                    : entry.Name.Trim();

                entries.Add(new GrokAccountEntry(id, name, entry.HomePath.Trim()));
            }
        }
        catch
        {
            // Ignore malformed settings and keep the built-in account.
        }

        return entries;
    }

    public static void SaveAdditional(IEnumerable<GrokAccountEntry> entries)
    {
        var saved = entries
            .Where(entry => !entry.IsDefault && !string.IsNullOrWhiteSpace(entry.HomePath))
            .Select(entry => new SavedGrokAccount(
                string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                string.IsNullOrWhiteSpace(entry.Name) ? "Grok" : entry.Name.Trim(),
                entry.HomePath!.Trim()))
            .ToList();

        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);

        if (saved.Count == 0)
        {
            key.DeleteValue(EntriesValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(EntriesValueName, JsonSerializer.Serialize(saved, JsonOptions), RegistryValueKind.String);
    }

    private sealed record SavedGrokAccount(string Id, string Name, string HomePath);
}
