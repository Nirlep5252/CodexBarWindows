using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

public sealed class CursorUsageReader
{
    private const string WorkosSessionCookieName = "WorkosCursorSessionToken";
    private static readonly IReadOnlyDictionary<string, string> KnownCookieNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["WorkosCursorSessionToken"] = "WorkosCursorSessionToken",
        ["__Secure-next-auth.session-token"] = "__Secure-next-auth.session-token",
        ["next-auth.session-token"] = "next-auth.session-token",
        ["wos-session"] = "wos-session",
        ["__Secure-wos-session"] = "__Secure-wos-session",
        ["authjs.session-token"] = "authjs.session-token",
        ["__Secure-authjs.session-token"] = "__Secure-authjs.session-token"
    };
    private static readonly Uri BaseUri = new("https://cursor.com");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly Func<HttpClient> httpClientFactory;
    private readonly string? cookieHeaderOverride;

    public CursorUsageReader()
        : this(null, null)
    {
    }

    public CursorUsageReader(string? cookieHeaderOverride)
        : this(null, cookieHeaderOverride)
    {
    }

    public CursorUsageReader(Func<HttpClient>? httpClientFactory, string? cookieHeaderOverride = null)
    {
        this.httpClientFactory = httpClientFactory ?? CreateHttpClient;
        this.cookieHeaderOverride = cookieHeaderOverride;
    }

    public async Task<ProviderUsageLookupResult> ReadLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cookieHeader = NormalizeCookieHeader(cookieHeaderOverride ?? CursorSettings.LoadCookieHeader());
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                return new ProviderUsageLookupResult(
                    null,
                    "Cursor Cookie header is not configured. Open Settings and paste a Cookie header from a cursor.com request.");
            }

            using var httpClient = httpClientFactory();
            var (summary, rawSummary) = await FetchUsageSummaryAsync(httpClient, cookieHeader, cancellationToken)
                .ConfigureAwait(false);

            CursorUserInfoResponse? userInfo = null;
            try
            {
                userInfo = await FetchUserInfoAsync(httpClient, cookieHeader, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Account identity is optional; usage-summary is the source of truth for the displayed stats.
            }

            CursorUsageResponse? requestUsage = null;
            if (!string.IsNullOrWhiteSpace(userInfo?.Sub))
            {
                try
                {
                    requestUsage = await FetchRequestUsageAsync(httpClient, userInfo.Sub!, cookieHeader, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Legacy request usage is not available for every Cursor account.
                }
            }

            var snapshot = MapUsage(summary, userInfo, requestUsage);
            return new ProviderUsageLookupResult(snapshot, null);
        }
        catch (Exception exception)
        {
            return new ProviderUsageLookupResult(null, $"Could not read Cursor usage: {exception.Message}");
        }
    }

    public static string NormalizeCookieHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Cookie:".Length..].Trim();
        }

        if (!normalized.Contains('=', StringComparison.Ordinal) &&
            !normalized.Contains(';', StringComparison.Ordinal))
        {
            return $"{WorkosSessionCookieName}={normalized}";
        }

        var parts = normalized
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeCookiePart)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return parts.Length == 0 ? normalized : string.Join("; ", parts);
    }

    private static string NormalizeCookiePart(string part)
    {
        var separator = part.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return part.Trim();
        }

        var name = part[..separator].Trim();
        var cookieValue = part[(separator + 1)..].Trim();
        var canonicalName = KnownCookieNames.TryGetValue(name, out var knownName) ? knownName : name;
        return $"{canonicalName}={cookieValue}";
    }

    public static ProviderUsageSnapshot MapUsage(
        CursorUsageSummaryResponse summary,
        CursorUserInfoResponse? userInfo = null,
        CursorUsageResponse? requestUsage = null)
    {
        var billingCycleStart = ParseDate(summary.BillingCycleStart);
        var billingCycleEnd = ParseDate(summary.BillingCycleEnd);
        var windowMinutes = BillingCycleWindowMinutes(billingCycleStart, billingCycleEnd);

        var planUsedRaw = summary.IndividualUsage?.Plan?.Used ?? 0;
        var planLimitRaw = summary.IndividualUsage?.Plan?.Limit ?? 0;
        var overallUsedRaw = summary.IndividualUsage?.Overall?.Used;
        var overallLimitRaw = summary.IndividualUsage?.Overall?.Limit;
        var pooledUsedRaw = summary.TeamUsage?.Pooled?.Used;
        var pooledLimitRaw = summary.TeamUsage?.Pooled?.Limit;

        var autoPercent = NormalizePercent(summary.IndividualUsage?.Plan?.AutoPercentUsed);
        var apiPercent = NormalizePercent(summary.IndividualUsage?.Plan?.ApiPercentUsed);

        var planPercentUsed = summary.IndividualUsage?.Plan?.TotalPercentUsed is { } totalPercent
            ? NormalizeTotalPercent(totalPercent)
            : autoPercent is { } auto && apiPercent is { } api
                ? NormalizeTotalPercent((auto + api) / 2)
                : apiPercent is { } onlyApi
                    ? NormalizeTotalPercent(onlyApi)
                    : autoPercent is { } onlyAuto
                        ? NormalizeTotalPercent(onlyAuto)
                        : planLimitRaw > 0
                            ? NormalizeTotalPercent((planUsedRaw / planLimitRaw) * 100)
                            : overallUsedRaw is { } overallUsed && overallLimitRaw is { } overallLimit && overallLimit > 0
                                ? NormalizeTotalPercent((overallUsed / overallLimit) * 100)
                                : pooledUsedRaw is { } pooledUsed && pooledLimitRaw is { } pooledLimit && pooledLimit > 0
                                    ? NormalizeTotalPercent((pooledUsed / pooledLimit) * 100)
                                    : 0;

        decimal planUsedUsd;
        decimal planLimitUsd;
        if (planLimitRaw > 0 || planUsedRaw > 0)
        {
            planUsedUsd = CentsToUsd(planUsedRaw);
            planLimitUsd = CentsToUsd(planLimitRaw);
        }
        else if (overallUsedRaw is { } overallUsedForDollars && overallLimitRaw is { } overallLimitForDollars)
        {
            planUsedUsd = CentsToUsd(overallUsedForDollars);
            planLimitUsd = CentsToUsd(overallLimitForDollars);
        }
        else if (pooledUsedRaw is { } pooledUsedForDollars && pooledLimitRaw is { } pooledLimitForDollars)
        {
            planUsedUsd = CentsToUsd(pooledUsedForDollars);
            planLimitUsd = CentsToUsd(pooledLimitForDollars);
        }
        else
        {
            planUsedUsd = 0;
            planLimitUsd = 0;
        }

        var requestsUsed = requestUsage?.Gpt4?.NumRequestsTotal ?? requestUsage?.Gpt4?.NumRequests;
        var requestsLimit = requestUsage?.Gpt4?.MaxRequestUsage;
        if (requestsUsed is { } used && requestsLimit is { } limit && limit > 0)
        {
            planPercentUsed = NormalizeTotalPercent((double)used / limit * 100);
        }

        var onDemandUsedUsd = CentsToUsd(summary.IndividualUsage?.OnDemand?.Used ?? 0);
        var onDemandLimitUsd = summary.IndividualUsage?.OnDemand?.Limit is { } onDemandLimit
            ? CentsToUsd(onDemandLimit)
            : (decimal?)null;
        var cost = onDemandUsedUsd > 0 || (onDemandLimitUsd ?? 0) > 0
            ? new ProviderUsageCost(onDemandUsedUsd, onDemandLimitUsd, "USD", "Monthly", billingCycleEnd)
            : null;

        var primaryTitle = requestsLimit is not null ? "Requests" : "Total";
        var primary = new ProviderUsageWindow(primaryTitle, planPercentUsed, windowMinutes, billingCycleEnd);
        var secondary = autoPercent is { } autoUsage
            ? new ProviderUsageWindow("Auto", autoUsage, windowMinutes, billingCycleEnd)
            : null;
        var tertiary = apiPercent is { } apiUsage
            ? new ProviderUsageWindow("API", apiUsage, windowMinutes, billingCycleEnd)
            : null;

        var planLabel = FormatMembershipType(summary.MembershipType);
        return new ProviderUsageSnapshot(
            UsageProvider.Cursor,
            DateTimeOffset.Now,
            planLabel,
            primary,
            secondary,
            "cursor.com",
            tertiary,
            cost,
            userInfo?.Email);
    }

    private static async Task<(CursorUsageSummaryResponse Summary, string RawJson)> FetchUsageSummaryAsync(
        HttpClient httpClient,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/usage-summary", cookieHeader);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Cursor rejected the Cookie header. Sign in to cursor.com and update it in Settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Cursor usage endpoint returned HTTP {(int)response.StatusCode}.");
        }

        var summary = JsonSerializer.Deserialize<CursorUsageSummaryResponse>(body, JsonOptions())
            ?? throw new InvalidOperationException("Cursor usage response was empty.");
        return (summary, body);
    }

    private static async Task<CursorUserInfoResponse> FetchUserInfoAsync(
        HttpClient httpClient,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/auth/me", cookieHeader);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<CursorUserInfoResponse>(body, JsonOptions())
            ?? throw new InvalidOperationException("Cursor user response was empty.");
    }

    private static async Task<CursorUsageResponse> FetchRequestUsageAsync(
        HttpClient httpClient,
        string userId,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            "/api/usage?user=" + Uri.EscapeDataString(userId),
            cookieHeader);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<CursorUsageResponse>(body, JsonOptions())
            ?? throw new InvalidOperationException("Cursor legacy usage response was empty.");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string pathAndQuery, string cookieHeader)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUri, pathAndQuery));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        return request;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.AppName}/{AppInfo.VersionText}");
        return client;
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToLocalTime() : null;
    }

    private static int BillingCycleWindowMinutes(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is { } cycleStart && end is { } cycleEnd)
        {
            var minutes = (int)Math.Round((cycleEnd - cycleStart).TotalMinutes, MidpointRounding.AwayFromZero);
            if (minutes > 0)
            {
                return minutes;
            }
        }

        return 30 * 24 * 60;
    }

    private static double? NormalizePercent(double? value)
    {
        return value is null ? null : NormalizeTotalPercent(value.Value);
    }

    private static double NormalizeTotalPercent(double value)
    {
        return Math.Clamp(value, 0, 100);
    }

    private static decimal CentsToUsd(double cents)
    {
        return Math.Round((decimal)cents / 100m, 2, MidpointRounding.AwayFromZero);
    }

    private static string? FormatMembershipType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "enterprise" => "Cursor Enterprise",
            "pro" => "Cursor Pro",
            "hobby" => "Cursor Hobby",
            "team" => "Cursor Team",
            var value => $"Cursor {char.ToUpperInvariant(value[0])}{value[1..]}"
        };
    }
}

public sealed record CursorUsageSummaryResponse(
    [property: JsonPropertyName("billingCycleStart")] string? BillingCycleStart,
    [property: JsonPropertyName("billingCycleEnd")] string? BillingCycleEnd,
    [property: JsonPropertyName("membershipType")] string? MembershipType,
    [property: JsonPropertyName("limitType")] string? LimitType,
    [property: JsonPropertyName("isUnlimited")] bool? IsUnlimited,
    [property: JsonPropertyName("autoModelSelectedDisplayMessage")] string? AutoModelSelectedDisplayMessage,
    [property: JsonPropertyName("namedModelSelectedDisplayMessage")] string? NamedModelSelectedDisplayMessage,
    [property: JsonPropertyName("individualUsage")] CursorIndividualUsageResponse? IndividualUsage,
    [property: JsonPropertyName("teamUsage")] CursorTeamUsageResponse? TeamUsage);

public sealed record CursorIndividualUsageResponse(
    [property: JsonPropertyName("plan")] CursorPlanUsageResponse? Plan,
    [property: JsonPropertyName("onDemand")] CursorOnDemandUsageResponse? OnDemand,
    [property: JsonPropertyName("overall")] CursorOverallUsageResponse? Overall);

public sealed record CursorPlanUsageResponse(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("used")] double? Used,
    [property: JsonPropertyName("limit")] double? Limit,
    [property: JsonPropertyName("remaining")] double? Remaining,
    [property: JsonPropertyName("breakdown")] CursorPlanBreakdownResponse? Breakdown,
    [property: JsonPropertyName("autoPercentUsed")] double? AutoPercentUsed,
    [property: JsonPropertyName("apiPercentUsed")] double? ApiPercentUsed,
    [property: JsonPropertyName("totalPercentUsed")] double? TotalPercentUsed);

public sealed record CursorPlanBreakdownResponse(
    [property: JsonPropertyName("included")] int? Included,
    [property: JsonPropertyName("bonus")] int? Bonus,
    [property: JsonPropertyName("total")] int? Total);

public sealed record CursorOverallUsageResponse(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("used")] double? Used,
    [property: JsonPropertyName("limit")] double? Limit,
    [property: JsonPropertyName("remaining")] double? Remaining);

public sealed record CursorOnDemandUsageResponse(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("used")] double? Used,
    [property: JsonPropertyName("limit")] double? Limit,
    [property: JsonPropertyName("remaining")] double? Remaining);

public sealed record CursorTeamUsageResponse(
    [property: JsonPropertyName("onDemand")] CursorOnDemandUsageResponse? OnDemand,
    [property: JsonPropertyName("pooled")] CursorPooledUsageResponse? Pooled);

public sealed record CursorPooledUsageResponse(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("used")] double? Used,
    [property: JsonPropertyName("limit")] double? Limit,
    [property: JsonPropertyName("remaining")] double? Remaining);

public sealed record CursorUserInfoResponse(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("email_verified")] bool? EmailVerified,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("sub")] string? Sub,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("picture")] string? Picture);

public sealed record CursorUsageResponse(
    [property: JsonPropertyName("gpt-4")] CursorModelUsageResponse? Gpt4,
    [property: JsonPropertyName("startOfMonth")] string? StartOfMonth);

public sealed record CursorModelUsageResponse(
    [property: JsonPropertyName("numRequests")] int? NumRequests,
    [property: JsonPropertyName("numRequestsTotal")] int? NumRequestsTotal,
    [property: JsonPropertyName("numTokens")] int? NumTokens,
    [property: JsonPropertyName("maxRequestUsage")] int? MaxRequestUsage,
    [property: JsonPropertyName("maxTokenUsage")] int? MaxTokenUsage);
