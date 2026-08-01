using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

/// <param name="installFolderName">
/// Folder under <c>%LOCALAPPDATA%\Programs</c> that this shell's MSI installs into. Only a build
/// running from there updates itself, so a dev build never overwrites the installed app. Defaults
/// to <see cref="AppInfo.AppName"/>; the WinUI shell passes its own while the two are installed
/// side by side (see <c>CodexBar.WinUI/ShellIdentity.cs</c>).
/// </param>
/// <param name="assetNameHint">
/// Substring every candidate release asset must contain. This is a HARD filter, not a preference:
/// once a release carries an MSI for each shell, a mere ordering hint would happily install the
/// other app over this one when the preferred asset were missing.
/// </param>
public sealed class GitHubReleaseUpdater(string? installFolderName = null, string? assetNameHint = null)
{
    private const string Owner = "Nirlep5252";
    private const string Repository = "CodexBarWindows";
    private const string RepositoryApiUrl = "https://api.github.com/repos/" + Owner + "/" + Repository;
    /// <summary>Deadline for the release-metadata call, which is a few KB.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Cap for the MSI download. Sized for a slow connection rather than a fast one: the asset is
    /// ~77 MB, so even 1 Mbit/s finishes inside this, and anything slower is better reported as a
    /// failure than left running for an hour.
    /// </summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly string installFolderName =
        string.IsNullOrWhiteSpace(installFolderName) ? AppInfo.AppName : installFolderName;

    private readonly string assetNameHint =
        string.IsNullOrWhiteSpace(assetNameHint) ? AppInfo.AppName : assetNameHint;

    public bool IsInstalledBuild
    {
        get
        {
            var installRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                installFolderName);

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
            .Where(asset => asset.Name.Contains(assetNameHint, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset => asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (msiAsset is null)
        {
            return UpdateCheckResult.Skipped(
                $"Release {release.TagName} does not include an MSI asset named for {assetNameHint}.");
        }

        var msiPath = await DownloadAssetAsync(httpClient, msiAsset, releaseVersion, cancellationToken).ConfigureAwait(false);
        LaunchMsiAfterCurrentProcessExits(msiPath);
        return UpdateCheckResult.Installing(releaseVersion);
    }

    private static HttpClient CreateClient(string? token)
    {
        var client = new HttpClient
        {
            // INFINITE ON THE CLIENT, bounded per request instead. HttpClient.Timeout is a
            // whole-operation deadline: with HttpCompletionOption.ResponseHeadersRead it keeps
            // running while the body streams, so a 20 second client timeout also capped the MSI
            // DOWNLOAD at 20 seconds. That asset is ~77 MB, which needs a sustained ~31 Mbit/s to
            // land in time, so the updater failed with "the request was canceled due to the
            // configured HttpClient.Timeout" on any ordinary connection - and because a check that
            // finds nothing new never reaches the download, the bug stayed invisible until the
            // first release that actually had an update to fetch.
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.AppName + "/" + AppInfo.CurrentVersion);
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
        // The metadata call is a few KB, so it keeps the short deadline: an unreachable GitHub
        // should fail the check quickly rather than leave the user staring at a spinner.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);

        using var response = await httpClient.GetAsync(RepositoryApiUrl + "/releases/latest", deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions(), deadline.Token).ConfigureAwait(false);
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

        // A generous CAP, not a deadline anyone should hit: the download is tens of megabytes and
        // its duration is the user's bandwidth, not ours to predict. The point is only that a
        // half-open connection cannot wedge the updater forever.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadTimeout);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Downloaded to a .partial and renamed only once complete. A cancelled or failed download
        // that left a truncated file under the real name would be handed straight to msiexec by the
        // next run, which fails with an opaque installer error rather than "download interrupted".
        var partialPath = downloadPath + ".partial";
        await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
        await using (var output = File.Create(partialPath))
        {
            await input.CopyToAsync(output, deadline.Token).ConfigureAwait(false);
        }

        File.Move(partialPath, downloadPath, overwrite: true);
        return downloadPath;
    }

    private static void LaunchMsiAfterCurrentProcessExits(string msiPath)
    {
        var currentProcessId = Environment.ProcessId;
        // Environment.ProcessPath rather than WinForms' Application.ExecutablePath: this file is
        // otherwise UI-agnostic, and the WinForms dependency was invisible because
        // UseWindowsForms=true injects a repo-wide global using for it.
        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
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
        return AppInfo.CurrentVersion;
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
