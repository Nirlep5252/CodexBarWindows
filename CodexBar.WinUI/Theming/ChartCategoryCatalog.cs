using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexBar.WinUI;

/// <summary>
/// The RAW category/model labels the graphs window has actually plotted, remembered so the
/// settings page can offer a colour row per model.
/// </summary>
/// <remarks>
/// <para>
/// The names exist only inside <c>ProviderUsageInsights</c>, which the refresh service only ever
/// builds while <c>IncludeHistory</c> is on - a gate the graphs window owns and that exists
/// precisely so the app costs nothing when idle. Turning that gate on from the settings window
/// would pay for a full session-log scan just to populate a list, so the labels are cached here
/// as the graphs window draws them instead.
/// </para>
/// <para>
/// Persisted next to the other settings (HKCU\Software\CodexBarWindows, one REG_SZ of JSON, the
/// <c>CodexCliEntries</c> pattern) so the list survives a restart: without that, the settings
/// page would be empty every session until the user opened the graphs window again.
/// </para>
/// <para>
/// Deliberately NOT part of <see cref="CodexBarWindows.UiSettings"/>: this is written on every
/// render, and <c>UiSettings.Save</c> raises <c>Changed</c>, which re-themes every open window.
/// </para>
/// </remarks>
internal static class ChartCategoryCatalog
{
    private const string SettingsKeyPath = @"Software\CodexBarWindows";
    private const string CatalogValueName = "ChartCategoryCatalog";

    /// <summary>Bounded so a pathological account cannot grow the value without limit.</summary>
    private const int MaxLabels = 64;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The label as ForCategory keys it, and the <c>#RRGGBB</c> the chart actually DREW it in.
    /// </summary>
    /// <remarks>
    /// The drawn colour is stored rather than recomputed because the settings page cannot
    /// reproduce it: several labels share one base accent, and the charts separate a collision by
    /// nudging whichever series claimed the slot second - an order that depends on the stack
    /// ordering of the data being plotted. Recomputing from <c>ForCategory</c> alone shows both
    /// collided models the same un-nudged hex, which matches neither bar.
    /// </remarks>
    public sealed record CatalogEntry(string Label, string? DrawnHex);

    public static IReadOnlyList<CatalogEntry> Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
            if (key?.GetValue(CatalogValueName) is not string json || string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            return Read(json)
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Select(pair => new CatalogEntry(pair.Key.Trim(), pair.Value))
                .DistinctBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            // A corrupt catalog only costs the settings page its rows; never fail a render for it.
            return [];
        }
    }

    /// <summary>
    /// Reads either shape of the stored value.
    /// </summary>
    /// <remarks>
    /// 0.8.0 shipped a bare JSON array of labels. Upgrading in place must not throw or silently
    /// drop the list, so an array still parses - the entries simply have no drawn colour until the
    /// graphs window renders once and rewrites the value as an object.
    /// </remarks>
    private static Dictionary<string, string?> Read(string json)
    {
        var trimmed = json.TrimStart();
        if (trimmed.StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions)?
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .DistinctBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(label => label, _ => (string?)null, StringComparer.OrdinalIgnoreCase)
                ?? [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, string?>>(json, JsonOptions)
            ?? [];
    }

    /// <summary>
    /// Folds newly seen labels, and the colours they were drawn in, into the stored set. Union,
    /// not replace: switching provider or scrolling out of a model's last active day must not
    /// delete a colour row the user is using. Returns without writing when nothing changed,
    /// because this runs on every chart render.
    /// </summary>
    public static void Merge(IEnumerable<(string Label, string? DrawnHex)> entries)
    {
        try
        {
            var known = Load().ToDictionary(
                entry => entry.Label,
                entry => entry.DrawnHex,
                StringComparer.OrdinalIgnoreCase);
            var dirty = false;

            foreach (var (label, hex) in entries)
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                var trimmed = label.Trim();
                if (known.TryGetValue(trimmed, out var stored))
                {
                    if (hex is not null && !string.Equals(stored, hex, StringComparison.OrdinalIgnoreCase))
                    {
                        known[trimmed] = hex;
                        dirty = true;
                    }

                    continue;
                }

                if (known.Count < MaxLabels)
                {
                    known[trimmed] = hex;
                    dirty = true;
                }
            }

            if (!dirty)
            {
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
            key.SetValue(
                CatalogValueName,
                JsonSerializer.Serialize(
                    known.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(pair => pair.Key, pair => pair.Value),
                    JsonOptions),
                RegistryValueKind.String);
        }
        catch
        {
            // Best effort: the catalog is a convenience, not part of drawing the chart.
        }
    }
}
