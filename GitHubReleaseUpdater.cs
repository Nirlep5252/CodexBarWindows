using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

public sealed class GitHubReleaseUpdater
{
    private const string Owner = "Nirlep5252";
    private const string Repository = "CodexBarWindows";
    private const string RepositoryApiUrl = "https://api.github.com/repos/" + Owner + "/" + Repository;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public bool IsInstalledBuild
    {
        get
        {
            var installRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "CodexBarWindows");

            var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var expectedDirectory = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(baseDirectory, expectedDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task<UpdateCheckResult> CheckAndInstallLatestAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalledBuild)
        {
            return UpdateCheckResult.Skipped("Update checks only run from the installed app.");
        }

        var currentVersion = CurrentVersion();
        var token = await ResolveGitHubTokenAsync(cancellationToken).ConfigureAwait(false);

        using var httpClient = CreateClient(token);
        var release = await FetchLatestReleaseAsync(httpClient, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return UpdateCheckResult.Skipped("No GitHub release was found.");
        }

        if (!TryParseVersion(release.TagName, out var releaseVersion))
        {
            return UpdateCheckResult.Skipped($"Latest release tag is not a version: {release.TagName}");
        }

        if (releaseVersion <= currentVersion)
        {
            return UpdateCheckResult.UpToDate(currentVersion);
        }

        var msiAsset = release.Assets
            .Where(asset => asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset => asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.Name.Contains("CodexBarWindows", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (msiAsset is null)
        {
            return UpdateCheckResult.Skipped($"Release {release.TagName} does not include an MSI asset.");
        }

        var msiPath = await DownloadAssetAsync(httpClient, msiAsset, releaseVersion, cancellationToken).ConfigureAwait(false);
        LaunchMsiAfterCurrentProcessExits(msiPath);
        return UpdateCheckResult.Installing(releaseVersion);
    }

    private static HttpClient CreateClient(string? token)
    {
        var client = new HttpClient
        {
            Timeout = RequestTimeout
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexBarWindows/" + CurrentVersion());
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static async Task<GitHubRelease?> FetchLatestReleaseAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(RepositoryApiUrl + "/releases/latest", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> DownloadAssetAsync(
        HttpClient httpClient,
        GitHubAsset asset,
        Version releaseVersion,
        CancellationToken cancellationToken)
    {
        var updateDirectory = Path.Combine(Path.GetTempPath(), "CodexBarWindows", "updates", releaseVersion.ToString());
        Directory.CreateDirectory(updateDirectory);

        var downloadPath = Path.Combine(updateDirectory, asset.Name);
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(downloadPath);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

        return downloadPath;
    }

    private static void LaunchMsiAfterCurrentProcessExits(string msiPath)
    {
        var currentProcessId = Environment.ProcessId;
        var executablePath = Application.ExecutablePath;
        var workingDirectory = AppContext.BaseDirectory;

        var script = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"Wait-Process -Id {currentProcessId} -Timeout 30",
            "$ErrorActionPreference = 'Stop'",
            $"Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', '{EscapePowerShellSingleQuoted(msiPath)}', '/qn', '/norestart') -Wait",
            $"if (Test-Path -LiteralPath '{EscapePowerShellSingleQuoted(executablePath)}') {{",
            $"    Start-Process -FilePath '{EscapePowerShellSingleQuoted(executablePath)}' -WorkingDirectory '{EscapePowerShellSingleQuoted(workingDirectory)}'",
            "}");

        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static async Task<string?> ResolveGitHubTokenAsync(CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable("CODEXBAR_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token.Trim();
        }

        token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token.Trim();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null)
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            token = (await outputTask.ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    private static Version CurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? new Version(0, 0, 0)
            : new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var normalized = tag.Trim().TrimStart('v', 'V');
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            normalized += ".0.0";
        }
        else if (parts.Length == 2)
        {
            normalized += ".0";
        }

        return Version.TryParse(normalized, out version!);
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] string Url);
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version? Version,
    string Message)
{
    public static UpdateCheckResult UpToDate(Version version)
    {
        return new UpdateCheckResult(UpdateCheckStatus.UpToDate, version, "CodexBarWindows is up to date.");
    }

    public static UpdateCheckResult Installing(Version version)
    {
        return new UpdateCheckResult(UpdateCheckStatus.Installing, version, $"Installing CodexBarWindows {version}.");
    }

    public static UpdateCheckResult Skipped(string message)
    {
        return new UpdateCheckResult(UpdateCheckStatus.Skipped, null, message);
    }
}

public enum UpdateCheckStatus
{
    Skipped,
    UpToDate,
    Installing
}
