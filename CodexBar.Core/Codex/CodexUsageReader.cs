using System.Text.Json;

namespace CodexBarWindows;

public sealed class CodexUsageReader
{
    private const int InitialRpcSampleCount = 3;
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);
    private readonly string? codexPath;
    private readonly CodexRateLimitStabilizer stabilizer;

    public CodexUsageReader()
        : this(null)
    {
    }

    public CodexUsageReader(string? codexPath)
        : this(codexPath, new CodexRateLimitStabilizer())
    {
    }

    internal CodexUsageReader(string? codexPath, CodexRateLimitStabilizer stabilizer)
    {
        this.codexPath = string.IsNullOrWhiteSpace(codexPath) ? null : codexPath;
        this.stabilizer = stabilizer;
    }

    public UsageLookupResult ReadLatest()
    {
        return ReadLatest(CancellationToken.None);
    }

    internal UsageLookupResult ReadLatest(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sampleCount = stabilizer.NeedsInitialConsensus ? InitialRpcSampleCount : 1;
        var samples = new List<UsageLookupResult>(sampleCount);
        for (var index = 0; index < sampleCount; index++)
        {
            samples.Add(ReadLatestFromRpc());
            cancellationToken.ThrowIfCancellationRequested();
        }

        return stabilizer.Stabilize(samples, DateTimeOffset.Now);
    }

    private UsageLookupResult ReadLatestFromRpc()
    {
        var resolvedCodexPath = CodexAppServerSession.ResolveExecutable(codexPath);
        if (resolvedCodexPath is null)
        {
            return new UsageLookupResult(
                null,
                codexPath is null
                    ? "Codex CLI was not found on PATH."
                    : $"Codex CLI was not found: {codexPath}");
        }

        try
        {
            using var session = CodexAppServerSession.Start(resolvedCodexPath, RpcTimeout);
            var response = session.Request("account/rateLimits/read");

            var snapshot = ParseRpcSnapshot(response, $"Codex CLI RPC ({resolvedCodexPath})");
            return snapshot is null
                ? new UsageLookupResult(null, "Codex CLI RPC returned no Codex rate-limit window.")
                : new UsageLookupResult(snapshot, null);
        }
        catch (Exception exception)
        {
            return new UsageLookupResult(null, $"Codex CLI RPC failed: {exception.Message}");
        }
    }

    internal static CodexRateLimitSnapshot? ParseRpcSnapshot(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !TryGetCodexRateLimits(result, out var rateLimits))
        {
            return null;
        }

        var windows = ParseRpcWindows(rateLimits);
        if (windows.Count == 0)
        {
            return null;
        }

        var planType = rateLimits.TryGetProperty("planType", out var planElement)
            ? planElement.GetString()
            : null;

        return new CodexRateLimitSnapshot(
            DateTimeOffset.Now,
            planType,
            windows[0],
            windows.Count > 1 ? windows[1] : null,
            source,
            windows.Count > 2 ? windows.Skip(2).ToArray() : null,
            ParseResetCredits(result));
    }

    /// <summary>
    /// Reads the banked reset-credit inventory, which the app-server reports alongside
    /// (not inside) the rate-limit buckets. Absent on older CLI builds.
    /// </summary>
    internal static CodexResetCredits? ParseResetCredits(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var summary) ||
            summary.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryReadInt32(summary, ["availableCount", "available_count"], out var availableCount))
        {
            availableCount = 0;
        }

        var credits = new List<CodexResetCredit>();
        if (summary.TryGetProperty("credits", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object && ParseResetCredit(row) is { } credit)
                {
                    credits.Add(credit);
                }
            }
        }

        return new CodexResetCredits(Math.Max(0, availableCount), credits);
    }

    private static CodexResetCredit? ParseResetCredit(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var idElement) ||
            idElement.GetString() is not { Length: > 0 } id)
        {
            // Without an opaque id the credit cannot be redeemed explicitly, and an
            // implicit redeem could spend from the wrong account.
            return null;
        }

        return new CodexResetCredit(
            id,
            element.TryGetProperty("status", out var status) ? status.GetString() : null,
            ReadUnixSeconds(element, ["grantedAt", "granted_at"]),
            ReadUnixSeconds(element, ["expiresAt", "expires_at"]),
            element.TryGetProperty("title", out var title) ? title.GetString() : null,
            element.TryGetProperty("description", out var description) ? description.GetString() : null);
    }

    private static DateTimeOffset? ReadUnixSeconds(JsonElement element, IReadOnlyList<string> names)
    {
        if (!TryReadInt64(element, names, out var seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryGetCodexRateLimits(JsonElement result, out JsonElement rateLimits)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var limitsById) &&
            limitsById.ValueKind == JsonValueKind.Object &&
            limitsById.TryGetProperty("codex", out rateLimits) &&
            rateLimits.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (result.TryGetProperty("rateLimits", out rateLimits) &&
            rateLimits.ValueKind == JsonValueKind.Object &&
            (!rateLimits.TryGetProperty("limitId", out var limitId) ||
             string.Equals(limitId.GetString(), "codex", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        rateLimits = default;
        return false;
    }

    private static IReadOnlyList<UsageWindow> ParseRpcWindows(JsonElement rateLimits)
    {
        var windows = new List<UsageWindow>();
        foreach (var property in rateLimits.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object && ParseRpcWindow(property.Value) is { } window)
            {
                windows.Add(window);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && ParseRpcWindow(item) is { } arrayWindow)
                    {
                        windows.Add(arrayWindow);
                    }
                }
            }
        }

        return windows
            .GroupBy(window => new { window.WindowMinutes, window.ResetsAt })
            .Select(group => group.First())
            .OrderBy(window => window.WindowMinutes)
            .ThenBy(window => window.ResetsAt)
            .ToArray();
    }

    private static UsageWindow? ParseRpcWindow(JsonElement element)
    {
        if (!TryReadDouble(element, ["usedPercent", "used_percent", "utilization"], out var usedPercent) ||
            !TryReadInt32(element, ["windowDurationMins", "window_duration_mins", "windowMinutes", "window_minutes"], out var windowMinutes) ||
            windowMinutes <= 0)
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        if (TryReadInt64(element, ["resetsAt", "resets_at"], out var resetSeconds))
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                // Keep the otherwise valid window and show an unknown reset time.
            }
        }

        return new UsageWindow(Math.Clamp(usedPercent, 0, 100), windowMinutes, resetsAt);
    }

    private static bool TryReadDouble(JsonElement element, IReadOnlyList<string> names, out double value)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var candidate))
            {
                continue;
            }

            if (candidate.ValueKind == JsonValueKind.Number && candidate.TryGetDouble(out value))
            {
                return true;
            }

            if (candidate.ValueKind == JsonValueKind.String &&
                double.TryParse(candidate.GetString(), System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadInt32(JsonElement element, IReadOnlyList<string> names, out int value)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var candidate))
            {
                continue;
            }

            if (candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt32(out value))
            {
                return true;
            }

            if (candidate.ValueKind == JsonValueKind.String &&
                int.TryParse(candidate.GetString(), System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadInt64(JsonElement element, IReadOnlyList<string> names, out long value)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var candidate))
            {
                continue;
            }

            if (candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt64(out value))
            {
                return true;
            }

            if (candidate.ValueKind == JsonValueKind.String &&
                long.TryParse(candidate.GetString(), System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

}
