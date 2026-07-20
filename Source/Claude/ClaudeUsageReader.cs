using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

public sealed class ClaudeUsageReader
{
    private const string TokenRefreshEndpoint = "https://platform.claude.com/v1/oauth/token";
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string BetaHeader = "oauth-2025-04-20";
    private const string FallbackClaudeCodeVersion = "2.1.0";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly string credentialsPath;
    private ClaudeOAuthCredentials? memoryCredentials;

    public ClaudeUsageReader()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            ".credentials.json"))
    {
    }

    public ClaudeUsageReader(string credentialsPath)
    {
        this.credentialsPath = credentialsPath;
    }

    public async Task<ProviderUsageLookupResult> ReadLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await LoadCredentialsAsync(cancellationToken).ConfigureAwait(false);
            using var httpClient = CreateHttpClient();
            var usage = await FetchUsageAsync(httpClient, credentials.AccessToken, cancellationToken)
                .ConfigureAwait(false);

            var snapshot = MapUsage(usage, PlanLabel(credentials));
            return new ProviderUsageLookupResult(snapshot, null);
        }
        catch (Exception exception)
        {
            return new ProviderUsageLookupResult(null, $"Could not read Claude usage: {exception.Message}");
        }
    }

    private async Task<ClaudeOAuthCredentials> LoadCredentialsAsync(CancellationToken cancellationToken)
    {
        if (memoryCredentials is { } cached && !cached.IsExpired)
        {
            return cached;
        }

        var credentials = LoadCredentialsFromFile();
        if (credentials.IsExpired)
        {
            credentials = await RefreshCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
        }

        memoryCredentials = credentials;
        return credentials;
    }

    private ClaudeOAuthCredentials LoadCredentialsFromFile()
    {
        if (!File.Exists(credentialsPath))
        {
            throw new FileNotFoundException($"Claude Code credentials were not found: {credentialsPath}");
        }

        using var stream = File.OpenRead(credentialsPath);
        var file = JsonSerializer.Deserialize<ClaudeCredentialsFile>(stream, JsonOptions())
            ?? throw new InvalidOperationException("Claude Code credentials file is empty.");

        var oauth = file.ClaudeAiOauth
            ?? throw new InvalidOperationException("Claude Code OAuth credentials were not found.");

        if (string.IsNullOrWhiteSpace(oauth.AccessToken))
        {
            throw new InvalidOperationException("Claude Code OAuth access token is missing.");
        }

        return new ClaudeOAuthCredentials(
            oauth.AccessToken,
            oauth.RefreshToken,
            ParseExpiresAt(oauth.ExpiresAt),
            oauth.Scopes ?? [],
            oauth.SubscriptionType,
            oauth.RateLimitTier);
    }

    private static async Task<ClaudeOAuthCredentials> RefreshCredentialsAsync(
        ClaudeOAuthCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            throw new InvalidOperationException("Claude OAuth token expired and no refresh token was found. Run `claude login`.");
        }

        using var httpClient = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenRefreshEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken,
            ["client_id"] = OAuthClientId
        });
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Claude OAuth refresh failed: HTTP {(int)response.StatusCode}. Run `claude login` if this persists.");
        }

        var token = JsonSerializer.Deserialize<TokenRefreshResponse>(body, JsonOptions())
            ?? throw new InvalidOperationException("Claude OAuth refresh response was invalid.");

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Claude OAuth refresh response did not include an access token.");
        }

        return credentials with
        {
            AccessToken = token.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? credentials.RefreshToken : token.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn))
        };
    }

    private static async Task<OAuthUsageResponse> FetchUsageAsync(
        HttpClient httpClient,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", BetaHeader);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Claude usage endpoint returned HTTP {(int)response.StatusCode}.");
        }

        return JsonSerializer.Deserialize<OAuthUsageResponse>(body, JsonOptions())
            ?? throw new InvalidOperationException("Claude usage response was empty.");
    }

    internal static ProviderUsageSnapshot MapUsage(OAuthUsageResponse usage, string? planLabel)
    {
        var primary = MakeWindow("5 hour limit", usage.FiveHour, 5 * 60)
            ?? throw new InvalidOperationException("Claude usage response did not include 5 hour data.");

        var weekly = MakeWindow("Weekly limit", usage.SevenDay, 7 * 24 * 60)
            ?? MakeWindow("Weekly limit", usage.SevenDaySonnet, 7 * 24 * 60)
            ?? MakeWindow("Weekly limit", usage.SevenDayOpus, 7 * 24 * 60);

        var fable = MakeWindow("Fable 5 limit", usage.SevenDayOverageIncluded, 7 * 24 * 60)
            ?? MakeFableWindow(usage.Limits);

        return new ProviderUsageSnapshot(
            UsageProvider.Claude,
            DateTimeOffset.Now,
            planLabel,
            primary,
            weekly,
            "Claude Code OAuth",
            fable);
    }

    private static ProviderUsageWindow? MakeWindow(string title, OAuthUsageWindow? window, int windowMinutes)
    {
        if (window?.Utilization is not { } utilization)
        {
            return null;
        }

        return new ProviderUsageWindow(
            title,
            Math.Clamp(utilization, 0, 100),
            windowMinutes,
            ParseIsoDate(window.ResetsAt));
    }

    private static ProviderUsageWindow? MakeFableWindow(IReadOnlyList<OAuthUsageLimit>? limits)
    {
        var limit = limits?.FirstOrDefault(candidate =>
            string.Equals(candidate.Kind, "weekly_scoped", StringComparison.OrdinalIgnoreCase) &&
            candidate.Scope?.Model?.DisplayName?.StartsWith("Fable", StringComparison.OrdinalIgnoreCase) == true);

        if (limit?.Percent is not { } percent)
        {
            return null;
        }

        return new ProviderUsageWindow(
            "Fable 5 limit",
            Math.Clamp(percent, 0, 100),
            7 * 24 * 60,
            ParseIsoDate(limit.ResetsAt));
    }

    private static string? PlanLabel(ClaudeOAuthCredentials credentials)
    {
        return ProviderPlanFormatter.ClaudePlanType(
            credentials.SubscriptionType,
            credentials.RateLimitTier);
    }

    private static DateTimeOffset ParseExpiresAt(long expiresAt)
    {
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return expiresAt > nowSeconds * 100
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)
            : DateTimeOffset.FromUnixTimeSeconds(expiresAt);
    }

    private static DateTimeOffset? ParseIsoDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime()
            : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"claude-code/{ResolveClaudeCodeVersion()}");
        return client;
    }

    private static string ResolveClaudeCodeVersion()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "claude",
                ArgumentList = { "--version" },
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null || !process.WaitForExit(1500) || process.ExitCode != 0)
            {
                return FallbackClaudeCodeVersion;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var token = output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(token) ? FallbackClaudeCodeVersion : token;
        }
        catch
        {
            return FallbackClaudeCodeVersion;
        }
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    private sealed record ClaudeOAuthCredentials(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset ExpiresAt,
        string[] Scopes,
        string? SubscriptionType,
        string? RateLimitTier)
    {
        public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2);
    }

    private sealed record ClaudeCredentialsFile(
        [property: JsonPropertyName("claudeAiOauth")] ClaudeOauthPayload? ClaudeAiOauth);

    private sealed record ClaudeOauthPayload(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken,
        [property: JsonPropertyName("expiresAt")] long ExpiresAt,
        [property: JsonPropertyName("scopes")] string[]? Scopes,
        [property: JsonPropertyName("subscriptionType")] string? SubscriptionType,
        [property: JsonPropertyName("rateLimitTier")] string? RateLimitTier);

    private sealed record TokenRefreshResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    internal sealed record OAuthUsageResponse(
        [property: JsonPropertyName("five_hour")] OAuthUsageWindow? FiveHour,
        [property: JsonPropertyName("seven_day")] OAuthUsageWindow? SevenDay,
        [property: JsonPropertyName("seven_day_sonnet")] OAuthUsageWindow? SevenDaySonnet,
        [property: JsonPropertyName("seven_day_opus")] OAuthUsageWindow? SevenDayOpus,
        [property: JsonPropertyName("seven_day_overage_included")] OAuthUsageWindow? SevenDayOverageIncluded,
        [property: JsonPropertyName("limits")] OAuthUsageLimit[]? Limits);

    internal sealed record OAuthUsageWindow(
        [property: JsonPropertyName("utilization")] double? Utilization,
        [property: JsonPropertyName("resets_at")] string? ResetsAt);

    internal sealed record OAuthUsageLimit(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("percent")] double? Percent,
        [property: JsonPropertyName("resets_at")] string? ResetsAt,
        [property: JsonPropertyName("scope")] OAuthUsageLimitScope? Scope);

    internal sealed record OAuthUsageLimitScope(
        [property: JsonPropertyName("model")] OAuthUsageLimitModel? Model);

    internal sealed record OAuthUsageLimitModel(
        [property: JsonPropertyName("display_name")] string? DisplayName);
}
