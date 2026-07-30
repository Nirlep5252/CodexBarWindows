using System.Text.Json;
using Microsoft.Win32;

namespace CodexBarWindows;

public sealed record CodexCliEntry(string Id, string Name, string? BinaryPath)
{
    public bool IsDefault => string.IsNullOrWhiteSpace(BinaryPath);
}

public static class CodexCliSettings
{
    private const string SettingsKeyPath = @"Software\CodexBarWindows";
    private const string EntriesValueName = "CodexCliEntries";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<CodexCliEntry> Load()
    {
        var entries = new List<CodexCliEntry>
        {
            new("default", "Codex", null)
        };

        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
        if (key?.GetValue(EntriesValueName) is not string json || string.IsNullOrWhiteSpace(json))
        {
            return entries;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<List<SavedCodexCliEntry>>(json, JsonOptions) ?? [];
            foreach (var entry in saved)
            {
                if (string.IsNullOrWhiteSpace(entry.BinaryPath))
                {
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
                var name = string.IsNullOrWhiteSpace(entry.Name)
                    ? $"Codex {entries.Count + 1}"
                    : entry.Name.Trim();

                entries.Add(new CodexCliEntry(id, name, entry.BinaryPath.Trim()));
            }
        }
        catch
        {
            // Ignore malformed settings and keep the built-in PATH-resolved entry.
        }

        return entries;
    }

    public static void SaveAdditional(IEnumerable<CodexCliEntry> entries)
    {
        var saved = entries
            .Where(entry => !entry.IsDefault && !string.IsNullOrWhiteSpace(entry.BinaryPath))
            .Select(entry => new SavedCodexCliEntry(
                string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                string.IsNullOrWhiteSpace(entry.Name) ? "Codex" : entry.Name.Trim(),
                entry.BinaryPath!.Trim()))
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

    private sealed record SavedCodexCliEntry(string Id, string Name, string BinaryPath);
}
