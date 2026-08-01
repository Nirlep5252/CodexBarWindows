using System.Text.RegularExpressions;

namespace CodexBarWindows;

internal static class ProviderPlanFormatter
{
    private static readonly Regex MultiplierPattern = new(
        @"(?<![A-Za-z0-9])(?<multiplier>[0-9]+x)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CodexProMultiplierPattern = new(
        @"^pro[\s_-]?(?<multiplier>[0-9]+x)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string? ClaudePlanType(string? subscriptionType, string? rateLimitTier)
    {
        var subscription = Clean(subscriptionType);
        var tier = Clean(rateLimitTier);
        var multiplier = ExtractMultiplier(tier);

        if (subscription is not null &&
            multiplier is not null &&
            !subscription.Contains(multiplier, StringComparison.OrdinalIgnoreCase))
        {
            return $"{subscription} {multiplier}";
        }

        return subscription ?? tier;
    }

    internal static string DisplayName(UsageProvider provider, string planType)
    {
        var value = planType.Trim();
        if (provider == UsageProvider.Codex)
        {
            if (string.Equals(value, "prolite", StringComparison.OrdinalIgnoreCase))
            {
                return "Pro 5x";
            }

            if (string.Equals(value, "pro", StringComparison.OrdinalIgnoreCase))
            {
                return "Pro 20x";
            }

            var multiplierMatch = CodexProMultiplierPattern.Match(value);
            if (multiplierMatch.Success)
            {
                return $"Pro {multiplierMatch.Groups["multiplier"].Value.ToLowerInvariant()}";
            }
        }

        // Grok's tiers arrive already cased as brands ("SuperGrok", "SuperGrok Heavy"). ToTitleCase
        // lowercases everything after the first letter, which turned those into "Supergrok heavy".
        // Anything carrying an interior capital is a name, not a slug, so it is passed through.
        if (provider == UsageProvider.Grok && value.Skip(1).Any(char.IsUpper))
        {
            return value;
        }

        return ToTitleCase(value.Replace('_', ' ').Replace('-', ' '));
    }

    private static string? ExtractMultiplier(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = MultiplierPattern.Match(value);
        return match.Success
            ? match.Groups["multiplier"].Value.ToLowerInvariant()
            : null;
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ToTitleCase(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}
