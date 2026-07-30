using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

/// <summary>
/// A short-lived <c>codex app-server</c> process wrapped in the minimal JSON-RPC handshake
/// shared by the usage reader and the reset-credit redeemer. One session serves one
/// Codex CLI binary, and therefore exactly one authenticated account.
/// </summary>
internal sealed class CodexAppServerSession : IDisposable
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Process process;
    private readonly TimeSpan timeout;
    private int nextRequestId = 2;
    private bool disposed;

    private CodexAppServerSession(Process process, TimeSpan timeout)
    {
        this.process = process;
        this.timeout = timeout;
    }

    /// <summary>Starts the app-server and completes the initialize/initialized handshake.</summary>
    public static CodexAppServerSession Start(string executablePath, TimeSpan timeout)
    {
        var session = new CodexAppServerSession(StartProcess(executablePath), timeout);
        try
        {
            session.SendRequest(1, "initialize", new
            {
                clientInfo = new { name = "codexbarwindows", version = "0.1.0" }
            });
            _ = session.ReadResponse(1);
            session.SendNotification("initialized");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Sends one request and returns the raw JSON response line matching its id.</summary>
    /// <exception cref="InvalidOperationException">The peer replied with a JSON-RPC error.</exception>
    /// <exception cref="TimeoutException">No matching response arrived within the timeout.</exception>
    public string Request(string method, object? parameters = null)
    {
        var id = nextRequestId++;
        SendRequest(id, method, parameters);
        return ReadResponse(id);
    }

    /// <summary>
    /// Resolves the Codex CLI to launch: an explicit path when configured, otherwise the
    /// <c>CODEX_BINARY</c> override, otherwise the first match on PATH.
    /// </summary>
    public static string? ResolveExecutable(string? explicitPath)
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        TryKill(process);
        process.Dispose();
    }

    private static Process StartProcess(string codexPath)
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

        // Drain stderr so a chatty CLI cannot fill the pipe buffer and wedge the process.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private void SendRequest(int id, string method, object? parameters)
    {
        var payload = JsonSerializer.Serialize(
            new RpcRequest(id, method, parameters ?? new { }),
            RequestJsonOptions);
        process.StandardInput.WriteLine(payload);
        process.StandardInput.Flush();
    }

    private void SendNotification(string method)
    {
        var payload = JsonSerializer.Serialize(new RpcNotification(method, new { }), RequestJsonOptions);
        process.StandardInput.WriteLine(payload);
        process.StandardInput.Flush();
    }

    private string ReadResponse(int expectedId)
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

    private sealed record RpcRequest(int Id, string Method, object Params);

    private sealed record RpcNotification(string Method, object Params);
}
