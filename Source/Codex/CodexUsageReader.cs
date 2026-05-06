using System.Globalization;
using System.Diagnostics;
using System.Text.Json;

namespace CodexBarWindows;

public sealed class CodexUsageReader
{
    private const int MaxSessionFilesToScan = 80;
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);
    private readonly string sessionsRoot;
    private readonly string? codexPath;

    public CodexUsageReader()
        : this(null)
    {
    }

    public CodexUsageReader(string? codexPath)
        : this(
            codexPath,
            Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions"))
    {
    }

    public CodexUsageReader(string? codexPath, string sessionsRoot)
    {
        this.codexPath = string.IsNullOrWhiteSpace(codexPath) ? null : codexPath;
        this.sessionsRoot = sessionsRoot;
    }

    public string SessionsRoot => sessionsRoot;

    public UsageLookupResult ReadLatest()
    {
        var rpcResult = ReadLatestFromRpc();
        if (rpcResult.HasSnapshot)
        {
            return rpcResult;
        }

        if (codexPath is not null)
        {
            return rpcResult;
        }

        var sessionsResult = ReadLatestFromSessions();
        if (sessionsResult.HasSnapshot)
        {
            return sessionsResult;
        }

        var error = rpcResult.Error ?? sessionsResult.Error ?? "No Codex rate-limit data was found.";
        if (!string.IsNullOrWhiteSpace(sessionsResult.Error))
        {
            error = $"{error} Session fallback: {sessionsResult.Error}";
        }

        return new UsageLookupResult(null, error);
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
            var stderr = new List<string>();
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    stderr.Add(args.Data);
                }
            };
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
                ? new UsageLookupResult(null, "Codex CLI RPC returned no rate-limit windows.")
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

    private UsageLookupResult ReadLatestFromSessions()
    {
        if (!Directory.Exists(sessionsRoot))
        {
            return new UsageLookupResult(null, $"Codex sessions folder was not found: {sessionsRoot}");
        }

        try
        {
            var files = EnumerateSessionFiles(sessionsRoot)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxSessionFilesToScan);

            CodexRateLimitSnapshot? latest = null;

            foreach (var file in files)
            {
                var snapshot = TryReadLatestFromFile(file.FullName);
                if (snapshot is null)
                {
                    continue;
                }

                if (latest is null || snapshot.ObservedAt > latest.ObservedAt)
                {
                    latest = snapshot;
                }
            }

            return latest is null
                ? new UsageLookupResult(null, "No Codex rate-limit events were found in recent sessions.")
                : new UsageLookupResult(latest, null);
        }
        catch (Exception exception)
        {
            return new UsageLookupResult(null, $"Could not read Codex usage: {exception.Message}");
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

    private static CodexRateLimitSnapshot? ParseRpcSnapshot(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("rateLimits", out var rateLimits))
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

    private static CodexRateLimitSnapshot? TryReadLatestFromFile(string path)
    {
        var lines = ReadSharedLines(path);
        if (lines is null)
        {
            return null;
        }

        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var line = lines[index];
            if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal))
            {
                continue;
            }

            var snapshot = TryParseSnapshot(line, path);
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSessionFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.jsonl");
            }
            catch (Exception exception) when (IsSkippableIoException(exception))
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (IsSkippableIoException(exception))
            {
                continue;
            }

            foreach (var childDirectory in directories)
            {
                pending.Push(childDirectory);
            }
        }
    }

    private static List<string>? ReadSharedLines(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (Exception exception) when (IsSkippableIoException(exception))
        {
            return null;
        }
    }

    private static CodexRateLimitSnapshot? TryParseSnapshot(string line, string sourcePath)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("timestamp", out var timestampElement) ||
                !DateTimeOffset.TryParse(
                    timestampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var observedAt))
            {
                observedAt = DateTimeOffset.UtcNow;
            }

            if (!root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("rate_limits", out var rateLimits) ||
                !rateLimits.TryGetProperty("primary", out var primary))
            {
                return null;
            }

            var primaryWindow = ParseUsageWindow(primary);
            UsageWindow? secondaryWindow = null;
            if (rateLimits.TryGetProperty("secondary", out var secondary) &&
                secondary.ValueKind != JsonValueKind.Null)
            {
                secondaryWindow = ParseUsageWindow(secondary);
            }

            if (primaryWindow is null)
            {
                return null;
            }

            var planType = rateLimits.TryGetProperty("plan_type", out var planElement)
                ? planElement.GetString()
                : null;

            return new CodexRateLimitSnapshot(
                observedAt,
                planType,
                primaryWindow,
                secondaryWindow,
                sourcePath);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UsageWindow? ParseUsageWindow(JsonElement element)
    {
        if (!element.TryGetProperty("used_percent", out var usedPercentElement) ||
            !element.TryGetProperty("window_minutes", out var windowMinutesElement) ||
            !element.TryGetProperty("resets_at", out var resetsAtElement))
        {
            return null;
        }

        var usedPercent = usedPercentElement.GetDouble();
        var windowMinutes = windowMinutesElement.GetInt32();
        var resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetsAtElement.GetInt64()).ToLocalTime();

        return new UsageWindow(usedPercent, windowMinutes, resetsAt);
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

    private static bool IsSkippableIoException(Exception exception)
    {
        return exception is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or System.ComponentModel.Win32Exception;
    }
}
