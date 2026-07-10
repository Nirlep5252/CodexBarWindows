using System.Diagnostics;
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
        var resolvedCodexPath = ResolveCodexExecutable(codexPath);
        if (resolvedCodexPath is null)
        {
            return new UsageLookupResult(
                null,
                codexPath is null
                    ? "Codex CLI was not found on PATH."
                    : $"Codex CLI was not found: {codexPath}");
        }

        Process? process = null;
        try
        {
            process = StartCodexRpc(resolvedCodexPath);
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();

            SendRpcRequest(
                process,
                1,
                "initialize",
                "\"params\":{\"clientInfo\":{\"name\":\"codexbarwindows\",\"version\":\"0.1.0\"}}");
            _ = ReadRpcResponse(process, 1, RpcTimeout);
            SendRpcNotification(process, "initialized");
            SendRpcRequest(process, 2, "account/rateLimits/read");

            var response = ReadRpcResponse(process, 2, RpcTimeout);
            TryKill(process);

            var snapshot = ParseRpcSnapshot(response, $"Codex CLI RPC ({resolvedCodexPath})");
            return snapshot is null
                ? new UsageLookupResult(null, "Codex CLI RPC returned no Codex rate-limit window.")
                : new UsageLookupResult(snapshot, null);
        }
        catch (Exception exception)
        {
            return new UsageLookupResult(null, $"Codex CLI RPC failed: {exception.Message}");
        }
        finally
        {
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }
        }
    }

    private static Process StartCodexRpc(string codexPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = codexPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.ArgumentList.Add("read-only");
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add("untrusted");
        process.StartInfo.ArgumentList.Add("app-server");

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start codex app-server.");
        }

        return process;
    }

    private static void SendRpcRequest(Process process, int id, string method, string? extraFields = null)
    {
        var payload = extraFields is null
            ? $"{{\"id\":{id},\"method\":\"{method}\",\"params\":{{}}}}"
            : $"{{\"id\":{id},\"method\":\"{method}\",{extraFields}}}";
        process.StandardInput.WriteLine(payload);
        process.StandardInput.Flush();
    }

    private static void SendRpcNotification(Process process, string method)
    {
        process.StandardInput.WriteLine($"{{\"method\":\"{method}\",\"params\":{{}}}}");
        process.StandardInput.Flush();
    }

    private static string ReadRpcResponse(Process process, int expectedId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var lineTask = process.StandardOutput.ReadLineAsync();
            if (!lineTask.Wait(remaining))
            {
                break;
            }

            var line = lineTask.Result;
            if (line is null)
            {
                throw new InvalidOperationException("codex app-server closed stdout.");
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                idElement.GetInt32() != expectedId)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var messageElement))
            {
                throw new InvalidOperationException(messageElement.GetString() ?? "Codex RPC request failed.");
            }

            return line;
        }

        throw new TimeoutException("Timed out waiting for codex app-server.");
    }

    internal static CodexRateLimitSnapshot? ParseRpcSnapshot(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !TryGetCodexRateLimits(result, out var rateLimits))
        {
            return null;
        }

        var primary = ParseRpcWindow(rateLimits, "primary");
        var secondary = ParseRpcWindow(rateLimits, "secondary");
        if (primary is null)
        {
            return null;
        }

        var planType = rateLimits.TryGetProperty("planType", out var planElement)
            ? planElement.GetString()
            : null;

        return new CodexRateLimitSnapshot(
            DateTimeOffset.Now,
            planType,
            primary,
            secondary,
            source);
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

    private static UsageWindow? ParseRpcWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var element) ||
            element.ValueKind == JsonValueKind.Null ||
            !element.TryGetProperty("usedPercent", out var usedPercentElement) ||
            !element.TryGetProperty("windowDurationMins", out var windowMinutesElement) ||
            !element.TryGetProperty("resetsAt", out var resetsAtElement))
        {
            return null;
        }

        return new UsageWindow(
            usedPercentElement.GetDouble(),
            windowMinutesElement.GetInt32(),
            DateTimeOffset.FromUnixTimeSeconds(resetsAtElement.GetInt64()).ToLocalTime());
    }

    private static string? ResolveCodexExecutable(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var environmentPath = Environment.GetEnvironmentVariable("CODEX_BINARY");
        if (explicitPath is null && !string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return environmentPath;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidateNames = OperatingSystem.IsWindows()
            ? new[] { "codex.cmd", "codex.exe", "codex.bat", "codex" }
            : new[] { "codex" };

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var name in candidateNames)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup for a short-lived local RPC process.
        }
    }
}
