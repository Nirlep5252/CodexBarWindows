using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

/// <summary>
/// Reads live Grok credit usage from the same cli-chat-proxy billing endpoint the Grok CLI
/// uses for <c>/usage</c>, authenticating with the local <c>~/.grok/auth.json</c> session.
/// </summary>
public sealed class GrokUsageReader
{
    private const string DefaultBillingBaseEndpoint = "https://cli-chat-proxy.grok.com/v1/billing";
    private const string ClientAuthHeaderValue = "xai-grok-cli";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly string authPath;
    private readonly string? billingEndpointOverride;
    private GrokSessionCredentials? memoryCredentials;
    private DateTime memoryCredentialsWriteTimeUtc;

    public GrokUsageReader()
        : this(ResolveDefaultAuthPath(), null)
    {
    }

    public GrokUsageReader(string authPath)
        : this(authPath, null)
    {
    }

    internal GrokUsageReader(string authPath, string? billingEndpointOverride)
    {
        this.authPath = authPath;
        this.billingEndpointOverride = string.IsNullOrWhiteSpace(billingEndpointOverride)
            ? null
            : billingEndpointOverride;
    }

    public async Task<ProviderUsageLookupResult> ReadLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await LoadCredentialsAsync(cancellationToken).ConfigureAwait(false);
            using var httpClient = CreateHttpClient();
            BillingCreditsResponse billing;
            try
            {
                billing = await FetchBillingAsync(httpClient, credentials, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GrokUnauthorizedException)
            {
                // A re-login revokes the old token without always updating expiry on disk.
                memoryCredentials = null;
                credentials = await LoadCredentialsAsync(cancellationToken).ConfigureAwait(false);
                billing = await FetchBillingAsync(httpClient, credentials, cancellationToken)
                    .ConfigureAwait(false);
            }

            var snapshot = MapUsage(billing, credentials);
            return new ProviderUsageLookupResult(snapshot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ProviderUsageLookupResult(null, $"Could not read Grok usage: {exception.Message}");
        }
    }

    private async Task<GrokSessionCredentials> LoadCredentialsAsync(CancellationToken cancellationToken)
    {
        var fileWriteTimeUtc = GetAuthWriteTimeUtc();
        if (memoryCredentials is { } cached && !cached.IsExpired && fileWriteTimeUtc == memoryCredentialsWriteTimeUtc)
        {
            return cached;
        }

        var credentials = LoadCredentialsFromFile();
        if (credentials.IsExpired)
        {
            credentials = await RefreshCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
        }

        memoryCredentials = credentials;
        memoryCredentialsWriteTimeUtc = fileWriteTimeUtc;
        return credentials;
    }

    private DateTime GetAuthWriteTimeUtc()
    {
        try
        {
            return File.GetLastWriteTimeUtc(authPath);
        }
        catch
        {
            return default;
        }
    }

    private GrokSessionCredentials LoadCredentialsFromFile()
    {
        if (!File.Exists(authPath))
        {
            throw new FileNotFoundException(
                $"Grok credentials were not found: {authPath}. Run `grok login`.",
                authPath);
        }

        using var stream = File.OpenRead(authPath);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Grok auth.json was empty or invalid.");
        }

        GrokSessionCredentials? best = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryReadEntry(property.Value, out var candidate) &&
                (best is null || candidate.RanksAbove(best)))
            {
                best = candidate;
            }
        }

        return best ?? throw new InvalidOperationException(
            "Grok OAuth session credentials were not found in auth.json. Run `grok login`.");
    }

    private static bool TryReadEntry(JsonElement entry, out GrokSessionCredentials credentials)
    {
        credentials = default!;

        // OAUTH FIELDS FIRST, and "key" only as a last resort. auth.json is keyed by provider and an
        // entry can be a plain stored API key rather than a session; reading "key" first meant such
        // an entry could be picked and sent to the billing proxy as a Bearer token, which it is not.
        var accessToken = ReadString(entry, "access_token")
            ?? ReadString(entry, "accessToken")
            ?? ReadString(entry, "key");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        var expiresAt = ReadExpiresAt(entry);
        credentials = new GrokSessionCredentials(
            accessToken,
            expiresAt is not null,
            ReadString(entry, "refresh_token") ?? ReadString(entry, "refreshToken"),
            expiresAt ?? DateTimeOffset.MinValue,
            ReadString(entry, "email"),
            ReadString(entry, "user_id") ?? ReadString(entry, "userId") ?? ReadString(entry, "principal_id"),
            ReadString(entry, "oidc_issuer") ?? ReadString(entry, "oidcIssuer") ?? "https://auth.x.ai",
            ReadString(entry, "oidc_client_id") ?? ReadString(entry, "oidcClientId"),
            ReadString(entry, "subscription_tier")
                ?? ReadString(entry, "subscriptionTier")
                ?? TierSlugFromAccessToken(accessToken));
        return true;
    }

    /// <summary>
    /// Recovers the subscription slug from the numeric <c>tier</c> claim on the access token.
    /// The billing endpoint omits <c>subscriptionTier</c> entirely on unified-billing accounts, and
    /// only some Grok CLI builds write <c>subscription_tier</c> into auth.json during login
    /// enrichment - on the rest the JWT is the only place the plan is stated at all.
    /// </summary>
    internal static string? TierSlugFromAccessToken(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            using var document = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("tier", out var tier) ||
                tier.ValueKind != JsonValueKind.Number ||
                !tier.TryGetInt32(out var value))
            {
                return null;
            }

            return TierClaimSlugs.GetValueOrDefault(value);
        }
        catch
        {
            // Opaque or non-JWT tokens simply carry no tier.
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    /// <summary>
    /// The <c>tier</c> claim is a plan ordinal, ascending from the cheapest. Derived from the slug
    /// list the Grok CLI itself carries (<c>supergrok_heavy, supergrok_plus, supergrok,
    /// supergrok_lite, x_premium_plus, x_premium, x_basic, free, api</c>, highest first) read in
    /// reverse, and anchored on an observed account: a SuperGrok Plus session carries <c>tier: 7</c>.
    /// Values outside the table are left unmapped on purpose - showing no plan beats naming the
    /// wrong one.
    /// </summary>
    private static readonly Dictionary<int, string> TierClaimSlugs = new()
    {
        [1] = "free",
        [2] = "x_basic",
        [3] = "x_premium",
        [4] = "x_premium_plus",
        [5] = "supergrok_lite",
        [6] = "supergrok",
        [7] = "supergrok_plus",
        [8] = "supergrok_heavy"
    };

    private static async Task<GrokSessionCredentials> RefreshCredentialsAsync(
        GrokSessionCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken) ||
            string.IsNullOrWhiteSpace(credentials.OidcClientId))
        {
            throw new InvalidOperationException(
                "Grok session expired and could not be refreshed. Run `grok login`.");
        }

        var tokenUrl = $"{credentials.OidcIssuer.TrimEnd('/')}/oauth2/token";
        using var httpClient = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken!,
            ["client_id"] = credentials.OidcClientId!
        });
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Grok OAuth refresh failed: HTTP {(int)response.StatusCode}. Run `grok login` if this persists.");
        }

        var token = JsonSerializer.Deserialize<TokenRefreshResponse>(body, JsonOptions())
            ?? throw new InvalidOperationException("Grok OAuth refresh response was invalid.");
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Grok OAuth refresh response did not include an access token.");
        }

        return credentials with
        {
            AccessToken = token.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? credentials.RefreshToken : token.RefreshToken,
            // A refreshed token always has a known lifetime, whatever the entry on disk looked like.
            HasExpiry = true,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn)),
            // The new token restates the tier, so an upgrade lands without waiting for a re-login.
            SubscriptionTier = TierSlugFromAccessToken(token.AccessToken) ?? credentials.SubscriptionTier
        };
    }

    private async Task<BillingCreditsResponse> FetchBillingAsync(
        HttpClient httpClient,
        GrokSessionCredentials credentials,
        CancellationToken cancellationToken)
    {
        var (creditsEndpoint, plainEndpoint) = ResolveBillingEndpoints();

        var billing = await FetchBillingFromAsync(httpClient, credentials, creditsEndpoint, cancellationToken)
            .ConfigureAwait(false);
        if (HasUsableUsage(billing))
        {
            return billing;
        }

        // The `format=credits` view carries `creditUsagePercent` for credit-based plans, but on
        // monthly-limit / unified-billing accounts it returns only period and on-demand data with
        // no usage figure at all. The plain billing endpoint carries `used`/`monthlyLimit` there,
        // which MapUsage turns into a percentage - so fall back to it before giving up.
        if (!string.Equals(plainEndpoint, creditsEndpoint, StringComparison.Ordinal))
        {
            var plain = await FetchBillingFromAsync(httpClient, credentials, plainEndpoint, cancellationToken)
                .ConfigureAwait(false);
            if (HasUsableUsage(plain))
            {
                return plain;
            }
        }

        // Neither view had usage; return the credits response so MapUsage throws the usual message.
        return billing;
    }

    /// <summary>
    /// The credits view provides a usage percentage only for credit-based plans; monthly-limit
    /// accounts express usage as <c>used</c>/<c>monthlyLimit</c> on the plain endpoint instead.
    /// Mirrors the two paths MapUsage can derive a percentage from.
    /// </summary>
    private static bool HasUsableUsage(BillingCreditsResponse billing)
        => billing.CreditUsagePercent is not null ||
           (billing.Used is not null && billing.MonthlyLimit is { } limit && limit > 0);

    private (string Credits, string Plain) ResolveBillingEndpoints()
    {
        string baseUrl;
        if (!string.IsNullOrWhiteSpace(billingEndpointOverride))
        {
            baseUrl = billingEndpointOverride;
        }
        else
        {
            var proxyBase = Environment.GetEnvironmentVariable("GROK_CLI_CHAT_PROXY_BASE_URL");
            baseUrl = !string.IsNullOrWhiteSpace(proxyBase)
                ? $"{proxyBase.TrimEnd('/')}/billing"
                : DefaultBillingBaseEndpoint;
        }

        var plain = StripQuery(baseUrl);
        return ($"{plain}?format=credits", plain);
    }

    private static string StripQuery(string url)
    {
        var index = url.IndexOf('?');
        return index >= 0 ? url[..index] : url;
    }

    private async Task<BillingCreditsResponse> FetchBillingFromAsync(
        HttpClient httpClient,
        GrokSessionCredentials credentials,
        string endpoint,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", ClientAuthHeaderValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(credentials.UserId))
        {
            request.Headers.TryAddWithoutValidation("x-user-id", credentials.UserId);
        }

        request.Headers.TryAddWithoutValidation("x-grok-client-mode", "cli");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new GrokUnauthorizedException(
                $"Grok billing endpoint rejected the token: HTTP {(int)response.StatusCode}. Run `grok login` if this persists.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Grok billing endpoint returned HTTP {(int)response.StatusCode}.");
        }

        return ParseBillingResponse(body);
    }

    internal static BillingCreditsResponse ParseBillingResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // CLI may wrap the config, return it at the root, or nest under "config".
        var config = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("config", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                config = nested;
            }
            else if (root.TryGetProperty("billingConfig", out var billingConfig) &&
                     billingConfig.ValueKind == JsonValueKind.Object)
            {
                config = billingConfig;
            }
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                config = data.TryGetProperty("config", out var dataConfig) && dataConfig.ValueKind == JsonValueKind.Object
                    ? dataConfig
                    : data;
            }
        }

        return new BillingCreditsResponse(
            ReadDouble(config, "creditUsagePercent") ?? ReadDouble(config, "credit_usage_percent"),
            ReadPeriod(config, "currentPeriod") ?? ReadPeriod(config, "current_period"),
            ReadMoney(config, "onDemandCap") ?? ReadMoney(config, "on_demand_cap"),
            ReadMoney(config, "onDemandUsed") ?? ReadMoney(config, "on_demand_used"),
            ReadMoney(config, "prepaidBalance") ?? ReadMoney(config, "prepaid_balance"),
            ReadMoney(config, "monthlyLimit") ?? ReadMoney(config, "monthly_limit"),
            ReadMoney(config, "used"),
            ReadString(config, "subscriptionTier")
                ?? ReadString(config, "subscription_tier")
                ?? ReadString(root, "subscriptionTier")
                ?? ReadString(root, "subscription_tier"),
            ReadDate(config, "billingPeriodStart") ?? ReadDate(config, "billing_period_start")
                ?? ReadPeriod(config, "currentPeriod")?.Start,
            ReadDate(config, "billingPeriodEnd") ?? ReadDate(config, "billing_period_end")
                ?? ReadPeriod(config, "currentPeriod")?.End,
            ReadBool(config, "isUnifiedBillingUser") ?? ReadBool(config, "is_unified_billing_user"));
    }

    internal static ProviderUsageSnapshot MapUsage(
        BillingCreditsResponse billing,
        GrokSessionCredentials? credentials = null)
    {
        var periodEnd = billing.BillingPeriodEnd ?? billing.CurrentPeriod?.End;
        var periodStart = billing.BillingPeriodStart ?? billing.CurrentPeriod?.Start;
        var windowMinutes = ResolveWindowMinutes(billing.CurrentPeriod?.Type, periodStart, periodEnd);

        var creditPercent = billing.CreditUsagePercent;
        if (creditPercent is null &&
            billing.Used is { } used &&
            billing.MonthlyLimit is { } limit &&
            limit > 0)
        {
            creditPercent = Math.Clamp((double)(used / limit * 100m), 0, 100);
        }

        if (creditPercent is null)
        {
            throw new InvalidOperationException("Grok billing response did not include credit usage.");
        }

        // Same title strings as Claude/Codex so ShortWindowLabel compresses them to "Week" /
        // "Month" in the flyout instead of truncating "Weekly credits" to "Weekl…".
        var primaryTitle = billing.CurrentPeriod?.Type?.Contains("WEEKLY", StringComparison.OrdinalIgnoreCase) == true
            ? "Weekly limit"
            : billing.CurrentPeriod?.Type?.Contains("MONTHLY", StringComparison.OrdinalIgnoreCase) == true
                ? "Monthly limit"
                : "Credits";

        var primary = new ProviderUsageWindow(
            primaryTitle,
            Math.Clamp(creditPercent.Value, 0, 100),
            windowMinutes,
            periodEnd?.ToLocalTime());

        ProviderUsageWindow? secondary = null;
        ProviderUsageCost? cost = null;
        if (billing.OnDemandCap is { } cap && cap > 0)
        {
            var onDemandUsed = billing.OnDemandUsed ?? 0;
            var percent = Math.Clamp((double)(onDemandUsed / cap * 100m), 0, 100);
            secondary = new ProviderUsageWindow("On-demand", percent, windowMinutes, periodEnd?.ToLocalTime());
            cost = new ProviderUsageCost(onDemandUsed, cap, "USD", "Period", periodEnd?.ToLocalTime());
        }
        else if (billing.OnDemandUsed is { } usedOnly && usedOnly > 0)
        {
            cost = new ProviderUsageCost(usedOnly, null, "USD", "Period", periodEnd?.ToLocalTime());
        }

        // Prefer the subscription tier when the billing API provides one. Do not fall back to
        // "Grok" — that only repeats the card title. Never surface the account email: it is
        // personal data and clutters the flyout (Cursor keeps email; Grok intentionally does not).
        var planLabel = !string.IsNullOrWhiteSpace(billing.SubscriptionTier)
            ? billing.SubscriptionTier
            : !string.IsNullOrWhiteSpace(credentials?.SubscriptionTier)
                ? credentials!.SubscriptionTier
                : null;

        return new ProviderUsageSnapshot(
            UsageProvider.Grok,
            DateTimeOffset.Now,
            planLabel,
            primary,
            secondary,
            "Grok CLI billing",
            Cost: cost);
    }

    private static int ResolveWindowMinutes(string? periodType, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is { } from && end is { } to && to > from)
        {
            var minutes = (int)Math.Round((to - from).TotalMinutes);
            if (minutes > 0)
            {
                return minutes;
            }
        }

        if (periodType is not null &&
            periodType.Contains("MONTHLY", StringComparison.OrdinalIgnoreCase))
        {
            return 30 * 24 * 60;
        }

        return 7 * 24 * 60;
    }

    private static string ResolveDefaultAuthPath()
    {
        var home = Environment.GetEnvironmentVariable("GROK_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, "auth.json");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".grok",
            "auth.json");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexBarWindows/GrokUsageReader");
        return client;
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static decimal? ReadMoney(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return ReadMoneyValue(value);
    }

    private static decimal? ReadMoneyValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDecimal(out var number):
                return number;
            case JsonValueKind.String when decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed):
                return parsed;
            case JsonValueKind.Object:
                if (value.TryGetProperty("val", out var val))
                {
                    return ReadMoneyValue(val);
                }

                if (value.TryGetProperty("value", out var nested))
                {
                    return ReadMoneyValue(nested);
                }

                return null;
            default:
                return null;
        }
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ReadExpiresAt(JsonElement entry)
    {
        if (entry.TryGetProperty("expires_at", out var expiresAt) ||
            entry.TryGetProperty("expiresAt", out expiresAt))
        {
            if (expiresAt.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(expiresAt.GetString(), out var parsed))
            {
                return parsed;
            }

            if (expiresAt.ValueKind == JsonValueKind.Number && expiresAt.TryGetInt64(out var unix))
            {
                return unix > 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
            }
        }

        return null;
    }

    private static BillingPeriod? ReadPeriod(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var period) || period.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new BillingPeriod(
            ReadString(period, "type"),
            ReadDate(period, "start"),
            ReadDate(period, "end"));
    }

    private sealed class GrokUnauthorizedException(string message) : Exception(message);

    internal sealed record GrokSessionCredentials(
        string AccessToken,
        bool HasExpiry,
        string? RefreshToken,
        DateTimeOffset ExpiresAt,
        string? Email,
        string? UserId,
        string OidcIssuer,
        string? OidcClientId,
        string? SubscriptionTier)
    {
        /// <summary>
        /// An entry with no expiry field is not treated as expired - we cannot know that it is, and
        /// forcing a refresh it may have no refresh token for would fail a session that works.
        /// It is ranked below any real session instead; see <see cref="RanksAbove"/>.
        /// </summary>
        public bool IsExpired => HasExpiry && ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2);

        /// <summary>
        /// A dated session always beats an undated one, and among dated ones the longest-lived
        /// wins. Defaulting a missing expiry to "tomorrow" (which the selection used to do) let a
        /// bare stored key out-rank the real OAuth session whenever that session had under a day left.
        /// </summary>
        public bool RanksAbove(GrokSessionCredentials other)
            => HasExpiry != other.HasExpiry ? HasExpiry : ExpiresAt > other.ExpiresAt;
    }

    internal sealed record BillingCreditsResponse(
        double? CreditUsagePercent,
        BillingPeriod? CurrentPeriod,
        decimal? OnDemandCap,
        decimal? OnDemandUsed,
        decimal? PrepaidBalance,
        decimal? MonthlyLimit,
        decimal? Used,
        string? SubscriptionTier,
        DateTimeOffset? BillingPeriodStart,
        DateTimeOffset? BillingPeriodEnd,
        bool? IsUnifiedBillingUser);

    internal sealed record BillingPeriod(string? Type, DateTimeOffset? Start, DateTimeOffset? End);

    private sealed record TokenRefreshResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
