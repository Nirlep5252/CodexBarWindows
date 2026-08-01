using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBarWindows;

/// <summary>
/// Reads OpenCode Go quota windows from the signed-in opencode.ai workspace page. OpenCode does
/// not expose a public quota API, so this mirrors the web strategy used by the original CodexBar:
/// discover a workspace through its server function, then parse the Go page's serialized data.
/// </summary>
public sealed partial class OpenCodeGoUsageReader
{
    private const string DefaultCookieName = "auth";
    private static readonly string[] AllowedCookieNames = [DefaultCookieName, "__Host-auth"];
    private const string WorkspacesServerId = "def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f";
    private static readonly Uri BaseUri = new("https://opencode.ai");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly Func<HttpClient> httpClientFactory;
    private readonly string? cookieHeaderOverride;
    private readonly string? workspaceIdOverride;

    public OpenCodeGoUsageReader()
        : this(null, null, null)
    {
    }

    public OpenCodeGoUsageReader(string? cookieHeader, string? workspaceId = null)
        : this(null, cookieHeader, workspaceId)
    {
    }

    internal OpenCodeGoUsageReader(
        Func<HttpClient>? httpClientFactory,
        string? cookieHeader = null,
        string? workspaceId = null)
    {
        this.httpClientFactory = httpClientFactory ?? CreateHttpClient;
        cookieHeaderOverride = cookieHeader;
        workspaceIdOverride = workspaceId;
    }

    public async Task<ProviderUsageLookupResult> ReadLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cookieHeader = NormalizeCookieHeader(cookieHeaderOverride ?? OpenCodeGoSettings.LoadCookieHeader());
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                return new ProviderUsageLookupResult(
                    null,
                    "OpenCode Go session value is not configured. Open Settings and paste the auth cookie value from opencode.ai.");
            }

            var rawWorkspace = workspaceIdOverride ?? OpenCodeGoSettings.LoadWorkspaceId();
            var workspaceId = NormalizeWorkspaceId(rawWorkspace);
            if (!string.IsNullOrWhiteSpace(rawWorkspace) && workspaceId is null)
            {
                return new ProviderUsageLookupResult(
                    null,
                    "The OpenCode Go workspace override must be a wrk_… id or an opencode.ai workspace URL.");
            }

            using var client = httpClientFactory();
            workspaceId ??= await FetchWorkspaceIdAsync(client, cookieHeader, cancellationToken).ConfigureAwait(false);
            var body = await FetchUsagePageAsync(client, workspaceId, cookieHeader, cancellationToken).ConfigureAwait(false);
            return new ProviderUsageLookupResult(ParseUsage(body, DateTimeOffset.Now), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ProviderUsageLookupResult(null, $"Could not read OpenCode Go usage: {exception.Message}");
        }
    }

    public static string NormalizeCookieHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Cookie:".Length..].Trim();
        }

        foreach (var part in normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                continue;
            }

            var name = part[..separator].Trim();
            if (AllowedCookieNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                var canonicalName = name.Equals("__Host-auth", StringComparison.OrdinalIgnoreCase)
                    ? "__Host-auth"
                    : DefaultCookieName;
                return $"{canonicalName}={part[(separator + 1)..].Trim()}";
            }
        }

        // The simple settings path: paste only the auth cookie's value. Preserve the complete
        // value (including any '=' padding) and construct the request cookie internally.
        return normalized.Contains(';') ? string.Empty : $"{DefaultCookieName}={normalized}";
    }

    public static string SessionValue(string? value)
    {
        var normalized = NormalizeCookieHeader(value);
        var separator = normalized.IndexOf('=');
        return separator < 0 ? string.Empty : normalized[(separator + 1)..];
    }

    public static string? NormalizeWorkspaceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = WorkspaceIdRegex().Match(value.Trim());
        return match.Success ? match.Value : null;
    }

    internal static ProviderUsageSnapshot ParseUsage(string text, DateTimeOffset observedAt)
    {
        if (TryParseJsonUsage(text, observedAt, out var jsonSnapshot))
        {
            return jsonSnapshot;
        }

        var rolling = ParseSerializedWindow(text, "rollingUsage", observedAt)
            ?? throw new InvalidOperationException("OpenCode Go response did not contain rolling usage fields.");
        var weekly = ParseSerializedWindow(text, "weeklyUsage", observedAt);
        var monthly = ParseSerializedWindow(text, "monthlyUsage", observedAt);

        return BuildSnapshot(rolling, weekly, monthly, observedAt);
    }

    private static async Task<string> FetchWorkspaceIdAsync(
        HttpClient client,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var body = await FetchWorkspaceTextAsync(client, cookieHeader, HttpMethod.Get, cancellationToken)
            .ConfigureAwait(false);
        var workspaceId = ParseWorkspaceId(body);
        if (workspaceId is not null)
        {
            return workspaceId;
        }

        body = await FetchWorkspaceTextAsync(client, cookieHeader, HttpMethod.Post, cancellationToken)
            .ConfigureAwait(false);
        return ParseWorkspaceId(body)
            ?? throw new InvalidOperationException("OpenCode Go could not find a workspace for this session.");
    }

    private static async Task<string> FetchWorkspaceTextAsync(
        HttpClient client,
        string cookieHeader,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        var uri = method == HttpMethod.Get
            ? new Uri(BaseUri, $"/_server?id={Uri.EscapeDataString(WorkspacesServerId)}")
            : new Uri(BaseUri, "/_server");
        using var request = CreateRequest(method, uri, cookieHeader);
        request.Headers.TryAddWithoutValidation("X-Server-Id", WorkspacesServerId);
        request.Headers.TryAddWithoutValidation("X-Server-Instance", $"server-fn:{Guid.NewGuid()}");
        request.Headers.Referrer = BaseUri;
        request.Headers.TryAddWithoutValidation("Origin", BaseUri.GetLeftPart(UriPartial.Authority));
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("[]", Encoding.UTF8, "application/json");
        }

        return await SendForTextAsync(client, request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> FetchUsagePageAsync(
        HttpClient client,
        string workspaceId,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(BaseUri, $"/workspace/{Uri.EscapeDataString(workspaceId)}/go");
        using var request = CreateRequest(HttpMethod.Get, uri, cookieHeader);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml, application/json;q=0.9, */*;q=0.8");
        return await SendForTextAsync(client, request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string cookieHeader)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<string> SendForTextAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            (int)response.StatusCode is >= 300 and < 400)
        {
            throw new InvalidOperationException(
                "OpenCode Go rejected the session. Sign in to opencode.ai and update the auth value in Settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenCode Go returned HTTP {(int)response.StatusCode}.");
        }

        return body;
    }

    private static HttpClient CreateHttpClient()
    {
        // Never follow a redirect with a manually attached Cookie header: that could forward the
        // credential to a different host. A sign-in redirect is reported as expired credentials.
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/143.0.0.0 Safari/537.36");
        return client;
    }

    private static string? ParseWorkspaceId(string text)
    {
        var property = WorkspacePropertyRegex().Match(text);
        if (property.Success)
        {
            return property.Groups["id"].Value;
        }

        return NormalizeWorkspaceId(text);
    }

    private sealed record ParsedWindow(double UsedPercent, DateTimeOffset? ResetsAt);

    private static ProviderUsageSnapshot BuildSnapshot(
        ParsedWindow rolling,
        ParsedWindow? weekly,
        ParsedWindow? monthly,
        DateTimeOffset observedAt)
    {
        var additional = monthly is null
            ? null
            : new[] { new ProviderUsageWindow("Monthly limit", monthly.UsedPercent, 30 * 24 * 60, monthly.ResetsAt) };

        return new ProviderUsageSnapshot(
            UsageProvider.OpenCodeGo,
            observedAt,
            null,
            new ProviderUsageWindow("5 hour limit", rolling.UsedPercent, 300, rolling.ResetsAt),
            weekly is null
                ? null
                : new ProviderUsageWindow("Weekly limit", weekly.UsedPercent, 7 * 24 * 60, weekly.ResetsAt),
            "opencode.ai",
            AdditionalWindows: additional);
    }

    private static ParsedWindow? ParseSerializedWindow(
        string text,
        string propertyName,
        DateTimeOffset observedAt)
    {
        var block = Regex.Match(
            text,
            $@"{Regex.Escape(propertyName)}[^{{}}]*\{{(?<body>[^{{}}]*)\}}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (!block.Success)
        {
            return null;
        }

        var body = block.Groups["body"].Value;
        var percent = ExtractNumber(body, PercentFieldNames);
        var resetSeconds = ExtractNumber(body, ResetSecondsFieldNames);
        var resetAt = ExtractDate(body);
        if (percent is null || (resetSeconds is null && resetAt is null))
        {
            return null;
        }

        // The dashboard's serialized `usagePercent` fields are already on a 0...100 scale.
        // In particular, 1 means 1%, not the fractional representation 100%.
        return new ParsedWindow(
            Math.Clamp(percent.Value, 0, 100),
            resetAt ?? observedAt.AddSeconds(Math.Max(0, resetSeconds!.Value)));
    }

    private static readonly string[] PercentFieldNames =
        [
            "usagePercent", "usedPercent", "percentUsed", "percent", "usage_percent", "used_percent",
            "utilization", "utilizationPercent", "utilization_percent", "usage"
        ];

    private static readonly string[] ResetSecondsFieldNames =
        [
            "resetInSec", "resetInSeconds", "resetSeconds", "reset_sec", "reset_in_sec", "resetsInSec",
            "resetsInSeconds", "resetIn", "resetSec"
        ];

    private static readonly string[] ResetAtFieldNames =
        ["resetAt", "resetsAt", "reset_at", "resets_at", "nextReset", "next_reset", "renewAt", "renew_at"];

    private static double? ExtractNumber(string text, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var match = Regex.Match(
                text,
                $@"[\""']?{Regex.Escape(name)}[\""']?\s*:\s*(?<value>-?[0-9]+(?:\.[0-9]+)?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? ExtractDate(string text)
    {
        foreach (var name in ResetAtFieldNames)
        {
            var match = Regex.Match(
                text,
                $@"[\""']?{Regex.Escape(name)}[\""']?\s*:\s*[\""'](?<value>[^\""']+)[\""']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && DateTimeOffset.TryParse(
                match.Groups["value"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var date))
            {
                return date;
            }
        }

        return null;
    }

    private static bool TryParseJsonUsage(
        string text,
        DateTimeOffset observedAt,
        out ProviderUsageSnapshot snapshot)
    {
        snapshot = null!;
        try
        {
            using var document = JsonDocument.Parse(text);
            return TryFindJsonUsage(document.RootElement, observedAt, 0, out snapshot);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindJsonUsage(
        JsonElement element,
        DateTimeOffset observedAt,
        int depth,
        out ProviderUsageSnapshot snapshot)
    {
        snapshot = null!;
        if (depth > 6)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var rolling = FindObject(element, "rollingUsage", "rolling", "rolling_usage", "rollingWindow", "rolling_window");
            if (rolling is { } rollingElement && TryParseJsonWindow(rollingElement, observedAt, out var rollingWindow))
            {
                ParsedWindow? weekly = null;
                ParsedWindow? monthly = null;
                if (FindObject(element, "weeklyUsage", "weekly", "weekly_usage", "weeklyWindow", "weekly_window") is { } weeklyElement &&
                    TryParseJsonWindow(weeklyElement, observedAt, out var weeklyWindow))
                {
                    weekly = weeklyWindow;
                }

                if (FindObject(element, "monthlyUsage", "monthly", "monthly_usage", "monthlyWindow", "monthly_window") is { } monthlyElement &&
                    TryParseJsonWindow(monthlyElement, observedAt, out var monthlyWindow))
                {
                    monthly = monthlyWindow;
                }

                snapshot = BuildSnapshot(rollingWindow, weekly, monthly, observedAt);
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                    TryFindJsonUsage(property.Value, observedAt, depth + 1, out snapshot))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindJsonUsage(item, observedAt, depth + 1, out snapshot))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonElement? FindObject(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static bool TryParseJsonWindow(
        JsonElement element,
        DateTimeOffset observedAt,
        out ParsedWindow window)
    {
        window = null!;
        var percent = JsonNumber(element, PercentFieldNames);
        if (percent is null)
        {
            var used = JsonNumber(element, "used", "usage", "consumed", "count", "usedTokens");
            var limit = JsonNumber(element, "limit", "total", "quota", "max", "cap", "tokenLimit");
            if (used is { } usedValue && limit is > 0)
            {
                percent = usedValue / limit.Value * 100;
            }
        }

        if (percent is null)
        {
            return false;
        }

        DateTimeOffset? resetAt = null;
        var seconds = JsonNumber(element, ResetSecondsFieldNames);
        if (seconds is not null)
        {
            resetAt = observedAt.AddSeconds(Math.Max(0, seconds.Value));
        }
        else
        {
            resetAt = JsonDate(element, ResetAtFieldNames);
        }

        if (resetAt is null)
        {
            return false;
        }

        window = new ParsedWindow(NormalizePercent(percent.Value), resetAt);
        return true;
    }

    private static double? JsonNumber(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
            {
                return number;
            }

            if (property.Value.ValueKind == JsonValueKind.String && double.TryParse(
                property.Value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number))
            {
                return number;
            }
        }

        return null;
    }

    private static DateTimeOffset? JsonDate(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(
                property.Value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var date))
            {
                return date;
            }

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var unix))
            {
                if (unix > 10_000_000_000)
                {
                    unix /= 1000;
                }

                try
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unix);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static double NormalizePercent(double value)
    {
        var percent = value is >= 0 and <= 1 ? value * 100 : value;
        return Math.Clamp(percent, 0, 100);
    }

    [GeneratedRegex(@"wrk_[A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex WorkspaceIdRegex();

    [GeneratedRegex(@"(?:^|[,{\s])(?:[\""']?id[\""']?)\s*:\s*[\""'](?<id>wrk_[A-Za-z0-9]+)[\""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkspacePropertyRegex();
}
