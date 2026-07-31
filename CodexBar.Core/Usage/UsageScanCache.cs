using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

/// <summary>
/// One cached file's parsed rows, as persisted to disk.
/// </summary>
/// <remarks>
/// The validity triple mirrors the in-memory cache and adds <paramref name="CreationUtcTicks"/>:
/// a restore-from-backup, a sync client, or a checkout can reproduce a file with an identical
/// length and write time but different content, and creation time is the cheapest signal that
/// separates them (it is already in the same stat call).
/// </remarks>
internal sealed record UsageScanCacheEntry<TRow>(
    long Length,
    long LastWriteUtcTicks,
    long CreationUtcTicks,
    DateOnly FirstScanDay,
    IReadOnlyList<TRow> Rows);

internal sealed record UsageScanCacheArtifact<TRow>(
    int SchemaVersion,
    string AppVersion,
    string TimeZoneId,
    Dictionary<string, UsageScanCacheEntry<TRow>> Entries);

/// <summary>
/// Persists the per-file parse cache so a relaunch does not re-parse gigabytes of session logs.
/// </summary>
/// <remarks>
/// <para>
/// This only ever stores RAW rows. Anything derived — cost, fast-turn membership, normalized
/// model labels, daily buckets — is recomputed at replay, so a pricing refresh or an accounting
/// fix still applies to cached rows. Persisting a derived value would freeze it permanently,
/// which the in-memory cache never did because it died with the process.
/// </para>
/// <para>
/// Deliberately not wired to any timer or startup path: load happens on the first history read
/// (which only runs while a window is open) and save at the end of that same read. Idle cost
/// stays zero.
/// </para>
/// </remarks>
internal static class UsageScanCache
{
    /// <summary>
    /// Bump when the row shape OR the scanner's accounting semantics change — a snapshot written
    /// by different accounting logic is not trustworthy even if it still deserializes.
    /// </summary>
    /// <remarks>
    /// v2: rows carry a DateTimeOffset truncated to the hour instead of a DateOnly. A v1 payload
    /// would still deserialize — the missing member simply defaults — and every row would silently
    /// land on 0001-01-01, so the version is what makes the change safe rather than merely tidy.
    /// The version is in the FILE NAME, so a downgrade reads its own file rather than this one.
    /// </remarks>
    private const int SchemaVersion = 2;

    /// <summary>
    /// Single shared instance: System.Text.Json caches reflection metadata per options object,
    /// so allocating one per call rebuilds the whole row-graph metadata each time.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true
    };

    private static string CacheFilePath(string provider)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CodexBarWindows", "usage-history", $"{provider}-v{SchemaVersion}.json");
    }

    /// <summary>
    /// Returns the persisted entries, or null when absent, unreadable, or written by a build,
    /// schema, or time zone that cannot be trusted. Every failure degrades to a cold scan.
    /// </summary>
    public static Dictionary<string, UsageScanCacheEntry<TRow>>? TryLoad<TRow>(string provider)
    {
        try
        {
            var path = CacheFilePath(provider);
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            var artifact = JsonSerializer.Deserialize<UsageScanCacheArtifact<TRow>>(stream, SerializerOptions);
            if (artifact is null ||
                artifact.SchemaVersion != SchemaVersion ||
                !string.Equals(artifact.AppVersion, AppVersion(), StringComparison.Ordinal) ||
                !string.Equals(artifact.TimeZoneId, TimeZoneInfo.Local.Id, StringComparison.Ordinal))
            {
                return null;
            }

            return artifact.Entries;
        }
        catch
        {
            // A corrupt or partial cache must never break the app — fall back to a cold scan.
            return null;
        }
    }

    /// <summary>Writes the snapshot atomically. Failures are swallowed; the cache is an accelerator.</summary>
    public static void TrySave<TRow>(string provider, Dictionary<string, UsageScanCacheEntry<TRow>> entries)
    {
        string? temp = null;
        try
        {
            var path = CacheFilePath(provider);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temp = Path.Combine(directory, $".tmp-{Guid.NewGuid():N}.json");

            var artifact = new UsageScanCacheArtifact<TRow>(
                SchemaVersion,
                AppVersion(),
                TimeZoneInfo.Local.Id,
                entries);

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, artifact, SerializerOptions);
                // Flush to disk before the rename so a power loss cannot leave a renamed-but-empty file.
                stream.Flush(flushToDisk: true);
            }

            // Single atomic replace: never delete-then-move, which leaves a window with no file
            // and races a second instance (Local\ single-instance is per-logon-session, so two
            // instances of this app can legitimately be running).
            File.Move(temp, path, overwrite: true);
            temp = null;
            DeleteSupersededVersions(directory, provider, path);
        }
        catch
        {
            // Non-critical.
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // Nothing useful to do if cleanup itself fails.
                }
            }
        }
    }

    /// <summary>
    /// Drops snapshots left behind by an older schema version. The version lives in the file name,
    /// so nothing ever reads those again — without this, every bump leaks a multi-megabyte file
    /// forever. Done after the replace, so a failure here can only leave garbage, never lose the
    /// snapshot that was just written.
    /// </summary>
    private static void DeleteSupersededVersions(string directory, string provider, string currentPath)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, $"{provider}-v*.json"))
            {
                if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Another instance may hold it open; it will be collected on a later save.
                }
            }
        }
        catch
        {
            // Non-critical: this is housekeeping, not part of the write.
        }
    }

    private static string AppVersion()
    {
        try
        {
            return AppInfo.CurrentVersion.ToString();
        }
        catch
        {
            return "unknown";
        }
    }
}
