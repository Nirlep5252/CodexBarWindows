namespace CodexBarWindows;

/// <summary>
/// The tray-icon hover text. Extracted from the WinForms tray context so both shells build
/// exactly the same string (and so the 63-character shell limit is enforced in one place).
/// </summary>
public static class UsageTooltip
{
    /// <summary>Shell limit for a notification-area tooltip.</summary>
    private const int MaxLength = 63;

    /// <summary>"5h", "7d", "45m" - the compact form of a rate-limit window duration.</summary>
    public static string ShortWindow(int windowMinutes)
    {
        if (windowMinutes >= 1440 && windowMinutes % 1440 == 0)
        {
            return $"{windowMinutes / 1440}d";
        }

        return windowMinutes >= 60 && windowMinutes % 60 == 0
            ? $"{windowMinutes / 60}h"
            : $"{windowMinutes}m";
    }

    public static string Build(
        IReadOnlyList<CodexCliEntry> codexEntries,
        IReadOnlyDictionary<string, ProviderUsageLookupResult> codexUsage,
        ProviderUsageLookupResult claudeUsage,
        ProviderUsageLookupResult grokUsage,
        ProviderUsageLookupResult cursorUsage,
        ProviderUsageLookupResult openCodeGoUsage,
        UiSettings settings)
    {
        if (codexUsage.Values.All(result => result.Snapshot is null) &&
            claudeUsage.Snapshot is null &&
            grokUsage.Snapshot is null &&
            cursorUsage.Snapshot is null &&
            openCodeGoUsage.Snapshot is null)
        {
            return Trim("CodexBarWindows: no usage data found");
        }

        // ONLY ENABLED PROVIDERS. The string is capped at 63 characters by the shell and Trim cuts
        // mid-word, so every segment for a tool the user switched off costs a segment they wanted:
        // five providers plus two Codex accounts already overflows, and providers late in the list
        // are the ones that disappear.
        var segments = new List<string>();

        if (settings.CodexEnabled)
        {
            segments.AddRange(codexEntries.Take(2).Select(entry =>
            {
                var result = codexUsage.TryGetValue(ProviderKeys.Codex(entry.Id), out var value)
                    ? value
                    : null;
                return result?.Snapshot is { } snapshot
                    ? $"{entry.Name} {snapshot.Primary.UsedPercent:0.#}% {ShortWindow(snapshot.Primary.WindowMinutes)}"
                    : $"{entry.Name} --";
            }));
        }

        if (settings.ClaudeEnabled)
        {
            segments.Add(claudeUsage.Snapshot is { } claude
                ? $"Claude {claude.Primary.UsedPercent:0.#}% {ShortWindow(claude.Primary.WindowMinutes)}"
                : "Claude --");
        }

        if (settings.GrokEnabled)
        {
            segments.Add(grokUsage.Snapshot is { } grok
                ? $"Grok {grok.Primary.UsedPercent:0.#}% {ShortWindow(grok.Primary.WindowMinutes)}"
                : "Grok --");
        }

        if (settings.CursorEnabled)
        {
            segments.Add(cursorUsage.Snapshot is { } cursor
                ? $"Cursor {cursor.Primary.UsedPercent:0.#}%"
                : "Cursor --");
        }

        if (settings.OpenCodeGoEnabled)
        {
            segments.Add(openCodeGoUsage.Snapshot is { } openCodeGo
                ? $"Go {openCodeGo.Primary.UsedPercent:0.#}% {ShortWindow(openCodeGo.Primary.WindowMinutes)}"
                : "Go --");
        }

        return segments.Count == 0
            ? Trim("CodexBarWindows: no tools enabled")
            : Trim(string.Join(", ", segments));
    }

    private static string Trim(string value) => value.Length <= MaxLength ? value : value[..MaxLength];
}
