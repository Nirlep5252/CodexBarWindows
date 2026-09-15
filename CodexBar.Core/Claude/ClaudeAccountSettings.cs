using System.Text.Json;
using Microsoft.Win32;

namespace CodexBarWindows;

/// <summary>
/// One configured Claude account. <paramref name="ConfigDir"/> is a CLAUDE_CONFIG_DIR directory -
/// the folder holding <c>.credentials.json</c>, <c>.claude.json</c> and <c>projects/</c> - which is
/// how Claude Code itself separates accounts (<c>CLAUDE_CONFIG_DIR=... claude</c>). The default
/// entry has none and resolves the same location the CLI would.
/// </summary>
/// <remarks>
/// A directory, not a <c>.credentials.json</c> path: the account's identity (the signed-in email,
/// used to name the entry) lives in <c>.claude.json</c> next to it, not in the credentials file.
/// Scope is limits only - the 30-day history scan stays single-account.
/// </remarks>
public sealed record ClaudeAccountEntry(string Id, string Name, string? ConfigDir)
{
    public bool IsDefault => string.IsNullOrWhiteSpace(ConfigDir);

    /// <summary>The account's config directory, falling back to CLAUDE_CONFIG_DIR then <c>~/.claude</c>.</summary>
    public string ResolveConfigDir()
    {
        if (!string.IsNullOrWhiteSpace(ConfigDir))
        {
            return ConfigDir;
        }

        return EnvironmentConfigDir() ?? DefaultConfigDir();
    }

    public string ResolveCredentialsPath() => Path.Combine(ResolveConfigDir(), ".credentials.json");

    /// <summary>
    /// The account's <c>.claude.json</c>. It sits INSIDE the config directory whenever one is
    /// named - explicitly here or through CLAUDE_CONFIG_DIR - but the plain default keeps it in
    /// the user profile root, NOT inside <c>~/.claude</c>. That asymmetry is Claude Code's own
    /// layout, not a quirk of ours, and getting it wrong means never finding the account email.
    /// </summary>
    public string ResolveConfigJsonPath()
    {
        if (!string.IsNullOrWhiteSpace(ConfigDir))
        {
            return ConfigJsonPathFor(ConfigDir);
        }

        var configured = EnvironmentConfigDir();
        return configured is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
            : ConfigJsonPathFor(configured);
    }

    /// <summary>
    /// Where an EXPLICIT config directory keeps its <c>.claude.json</c>. The one place this is
    /// encoded, so the settings screen and the entry cannot drift apart on it.
    /// </summary>
    public static string ConfigJsonPathFor(string configDir) => Path.Combine(configDir, ".claude.json");

    /// <summary>
    /// The first entry of CLAUDE_CONFIG_DIR, or null when it is unset. The variable is a
    /// comma-separated LIST - Claude Code writes to the first and only reads the rest - so the
    /// first is the one an account's files actually live in.
    /// </summary>
    private static string? EnvironmentConfigDir()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var first = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    private static string DefaultConfigDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
}

/// <summary>
/// The configured Claude accounts, persisted alongside the Codex and Grok ones in
/// HKCU\Software\CodexBarWindows. Shaped like <see cref="GrokAccountSettings"/>: one REG_SZ of
/// JSON, with the default account always synthesised at the head so an install that never opens
/// this setting behaves as it always did. Unlike Grok, the default entry can be renamed, so the
/// JSON may also carry a row with the default id and no folder that holds nothing but its name.
/// </summary>
public static class ClaudeAccountSettings
{
    private const string SettingsKeyPath = @"Software\CodexBarWindows";
    private const string EntriesValueName = "ClaudeAccounts";

    /// <summary>Id of the synthesised entry that reads CLAUDE_CONFIG_DIR / <c>~/.claude</c>.</summary>
    public const string DefaultId = "default";

    /// <summary>What the built-in account is called until the user renames it.</summary>
    public const string DefaultName = "Claude";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ClaudeAccountEntry> Load()
    {
        var entries = new List<ClaudeAccountEntry>
        {
            new(DefaultId, DefaultName, null)
        };

        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
        if (key?.GetValue(EntriesValueName) is not string json || string.IsNullOrWhiteSpace(json))
        {
            return entries;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<List<SavedClaudeAccount>>(json, JsonOptions) ?? [];
            // Ids are dictionary keys everywhere downstream (usage, readers, the reload diff), so
            // a hand-edited value with a repeated id must not reach them: first occurrence wins.
            var seenIds = new HashSet<string>(StringComparer.Ordinal) { DefaultId };
            foreach (var entry in saved)
            {
                if (string.Equals(entry.Id, DefaultId, StringComparison.Ordinal))
                {
                    // The default row only ever carries a name; its folder is resolved live.
                    if (!string.IsNullOrWhiteSpace(entry.Name))
                    {
                        entries[0] = entries[0] with { Name = entry.Name.Trim() };
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.ConfigDir))
                {
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
                if (!seenIds.Add(id))
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(entry.Name)
                    ? $"Claude {entries.Count + 1}"
                    : entry.Name.Trim();

                entries.Add(new ClaudeAccountEntry(id, name, entry.ConfigDir.Trim()));
            }
        }
        catch
        {
            // Ignore malformed settings and keep the built-in account.
        }

        return entries;
    }

    /// <summary>
    /// Persists the extra accounts, plus the default's name when it has been changed. A default
    /// still called "Claude" writes nothing, so an untouched install keeps an empty value.
    /// </summary>
    public static void Save(IEnumerable<ClaudeAccountEntry> entries)
    {
        var list = entries.ToList();
        var saved = list
            .Where(entry => !entry.IsDefault && !string.IsNullOrWhiteSpace(entry.ConfigDir))
            .Select(entry => new SavedClaudeAccount(
                string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                string.IsNullOrWhiteSpace(entry.Name) ? DefaultName : entry.Name.Trim(),
                entry.ConfigDir!.Trim()))
            .ToList();

        var defaultName = list.FirstOrDefault(entry => entry.IsDefault)?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(defaultName) &&
            !string.Equals(defaultName, DefaultName, StringComparison.Ordinal))
        {
            saved.Insert(0, new SavedClaudeAccount(DefaultId, defaultName, null));
        }

        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);

        if (saved.Count == 0)
        {
            key.DeleteValue(EntriesValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(EntriesValueName, JsonSerializer.Serialize(saved, JsonOptions), RegistryValueKind.String);
    }

    /// <summary>
    /// The signed-in email recorded in a <c>.claude.json</c>, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Purely cosmetic: the UI offers it as the display name for a newly added directory, so a
    /// user picking two folders does not end up with two accounts called "Claude 2". Every
    /// failure - no file, a half-written one, an older layout without the property - is the same
    /// non-answer, so this never throws and never blocks adding the account.
    /// </remarks>
    public static string? ReadAccountEmail(string configJsonPath)
    {
        try
        {
            using var stream = File.OpenRead(configJsonPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("oauthAccount", out var account) ||
                !account.TryGetProperty("emailAddress", out var email) ||
                email.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = email.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private sealed record SavedClaudeAccount(string Id, string Name, string? ConfigDir);
}
