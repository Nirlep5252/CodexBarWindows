using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows;

internal sealed record ModelsDevPricingInfo(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillion,
    decimal? CacheCreationPerMillion,
    int? ThresholdTokens,
    decimal? InputPerMillionAboveThreshold,
    decimal? OutputPerMillionAboveThreshold,
    decimal? CacheReadPerMillionAboveThreshold,
    decimal? CacheCreationPerMillionAboveThreshold);

internal static class ModelsDevPricing
{
    private const int ArtifactVersion = 1;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly Uri CatalogUri = new("https://models.dev/api.json");
    private static readonly object CacheLock = new();
    private static int refreshStarted;
    private static bool cacheLoaded;
    private static JsonElement? cachedCatalog;
    private static DateTimeOffset cachedFetchedAt;

    public static void RefreshInBackgroundIfNeeded()
    {
        if (Interlocked.Exchange(ref refreshStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshIfNeededAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref refreshStarted, 0);
            }
        });
    }

    public static ModelsDevPricingInfo? Lookup(string providerId, string modelId)
    {
        var load = Load();
        return load.Catalog is { } catalog
            ? Lookup(catalog, providerId, modelId)
            : null;
    }

    private static async Task RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        var load = Load();
        if (load.Catalog is not null && DateTimeOffset.UtcNow - load.FetchedAt <= Ttl)
        {
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var json = await client.GetStringAsync(CatalogUri, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var catalog = document.RootElement.Clone();
            if (!ContainsProvider(catalog, "anthropic"))
            {
                return;
            }

            Save(catalog, DateTimeOffset.UtcNow);
        }
        catch
        {
            // Best-effort only. Callers continue with stale cache or built-in prices.
        }
    }

    private static (JsonElement? Catalog, DateTimeOffset FetchedAt) Load()
    {
        lock (CacheLock)
        {
            if (cacheLoaded)
            {
                return (cachedCatalog, cachedFetchedAt);
            }

            cacheLoaded = true;
            try
            {
                var path = CacheFilePath();
                if (!File.Exists(path))
                {
                    return (null, default);
                }

                using var stream = File.OpenRead(path);
                var artifact = JsonSerializer.Deserialize<CacheArtifact>(stream, JsonOptions());
                if (artifact?.Version != ArtifactVersion || artifact.Catalog.ValueKind == JsonValueKind.Undefined)
                {
                    return (null, default);
                }

                cachedCatalog = artifact.Catalog.Clone();
                cachedFetchedAt = artifact.FetchedAt;
                return (cachedCatalog, cachedFetchedAt);
            }
            catch
            {
                return (null, default);
            }
        }
    }

    private static void Save(JsonElement catalog, DateTimeOffset fetchedAt)
    {
        try
        {
            var path = CacheFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = Path.Combine(Path.GetDirectoryName(path)!, $".tmp-{Guid.NewGuid():N}.json");
            var artifact = new CacheArtifact(ArtifactVersion, fetchedAt, catalog);
            File.WriteAllText(temp, JsonSerializer.Serialize(artifact, JsonOptions()));
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
            lock (CacheLock)
            {
                cachedCatalog = catalog.Clone();
                cachedFetchedAt = fetchedAt;
                cacheLoaded = true;
            }
        }
        catch
        {
            // Cache writes are non-critical.
        }
    }

    private static string CacheFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CodexBarWindows", "model-pricing", $"models-dev-v{ArtifactVersion}.json");
    }

    private static bool ContainsProvider(JsonElement catalog, string providerId)
    {
        return TryGetProvider(catalog, providerId, out _);
    }

    private static ModelsDevPricingInfo? Lookup(JsonElement catalog, string providerId, string modelId)
    {
        if (!TryGetProvider(catalog, providerId, out var provider) ||
            !provider.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var candidate in ModelCandidates(modelId))
        {
            if (models.TryGetProperty(candidate, out var model) && TryReadPricing(model, out var pricing))
            {
                return pricing;
            }
        }

        var candidates = ModelCandidates(modelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in models.EnumerateObject())
        {
            var model = property.Value;
            var id = ReadString(model, "id") ?? property.Name;
            if (!ModelCandidates(id).Any(candidates.Contains))
            {
                continue;
            }

            if (TryReadPricing(model, out var pricing))
            {
                return pricing;
            }
        }

        return null;
    }

    private static bool TryGetProvider(JsonElement catalog, string providerId, out JsonElement provider)
    {
        provider = default;
        var providers = catalog.TryGetProperty("providers", out var providersObject) && providersObject.ValueKind == JsonValueKind.Object
            ? providersObject
            : catalog;
        if (providers.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in providers.EnumerateObject())
        {
            var id = ReadString(property.Value, "id") ?? property.Name;
            if (string.Equals(NormalizeProvider(id), NormalizeProvider(providerId), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeProvider(property.Name), NormalizeProvider(providerId), StringComparison.OrdinalIgnoreCase))
            {
                provider = property.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadPricing(JsonElement model, out ModelsDevPricingInfo pricing)
    {
        pricing = default!;
        if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (ReadDecimal(cost, "input") is not { } input || ReadDecimal(cost, "output") is not { } output)
        {
            return false;
        }

        int? threshold = null;
        decimal? inputAbove = null;
        decimal? outputAbove = null;
        decimal? cacheReadAbove = null;
        decimal? cacheWriteAbove = null;
        if (cost.TryGetProperty("context_over_200k", out var over200k) && over200k.ValueKind == JsonValueKind.Object)
        {
            threshold = 200_000;
            inputAbove = ReadDecimal(over200k, "input");
            outputAbove = ReadDecimal(over200k, "output");
            cacheReadAbove = ReadDecimal(over200k, "cache_read");
            cacheWriteAbove = ReadDecimal(over200k, "cache_write");
        }

        pricing = new ModelsDevPricingInfo(
            input,
            output,
            ReadDecimal(cost, "cache_read"),
            ReadDecimal(cost, "cache_write"),
            threshold,
            inputAbove,
            outputAbove,
            cacheReadAbove,
            cacheWriteAbove);
        return true;
    }

    private static IEnumerable<string> ModelCandidates(string raw)
    {
        var candidates = new List<string>();
        void Add(string? value)
        {
            value = value?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && !candidates.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(value);
            }
        }

        Add(raw);
        if (raw.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase))
        {
            Add(raw["anthropic.".Length..]);
        }

        var lastDot = raw.LastIndexOf('.');
        if (lastDot >= 0 && raw.Contains("claude-", StringComparison.OrdinalIgnoreCase))
        {
            var tail = raw[(lastDot + 1)..];
            if (tail.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            {
                Add(tail);
            }
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var at = candidate.IndexOf('@');
            if (at > 0)
            {
                var baseName = candidate[..at];
                Add(baseName);
                var suffix = candidate[(at + 1)..];
                if (suffix.Length == 8 && suffix.All(char.IsDigit))
                {
                    Add($"{baseName}-{suffix}");
                }
            }
            else if (candidate.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            {
                Add($"{candidate}@default");
            }

            var compactDate = System.Text.RegularExpressions.Regex.Match(candidate, "-\\d{8}$");
            if (compactDate.Success)
            {
                Add(candidate[..compactDate.Index]);
            }

            var dated = System.Text.RegularExpressions.Regex.Match(candidate, "-\\d{4}-\\d{2}-\\d{2}$");
            if (dated.Success)
            {
                Add(candidate[..dated.Index]);
            }

            var version = System.Text.RegularExpressions.Regex.Match(candidate, "-v\\d+:\\d+$");
            if (version.Success)
            {
                Add(candidate[..version.Index]);
            }
        }

        return candidates;
    }

    private static string NormalizeProvider(string raw) => raw.Trim().ToLowerInvariant();

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }

    private sealed record CacheArtifact(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("fetchedAt")] DateTimeOffset FetchedAt,
        [property: JsonPropertyName("catalog")] JsonElement Catalog);
}
