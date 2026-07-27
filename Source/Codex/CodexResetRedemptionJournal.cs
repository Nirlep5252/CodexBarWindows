using System.Text.Json;
using Microsoft.Win32;

namespace CodexBarWindows;

/// <summary>
/// Persists the in-flight reset redemption so a retry reuses its idempotency key.
/// </summary>
/// <remarks>
/// A consume that times out leaves the outcome genuinely unknown — the credit may already
/// be spent. Replaying the same <c>idempotencyKey</c> makes the backend answer
/// <see cref="CodexResetOutcome.AlreadyRedeemed"/> instead of burning a second credit, so the
/// key must survive both the failed attempt and an app restart in between.
/// </remarks>
public static class CodexResetRedemptionJournal
{
    private const string SettingsKeyPath = @"Software\CodexBarWindows";
    private const string PendingValueName = "PendingResetRedemption";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object Sync = new();

    /// <summary>
    /// Returns the idempotency key to use for redeeming <paramref name="creditId"/> on
    /// <paramref name="accountId"/>: the recorded key when the same attempt is being retried,
    /// otherwise a fresh one, recorded before the attempt starts.
    /// </summary>
    public static string BeginAttempt(string accountId, string creditId)
    {
        lock (Sync)
        {
            if (Read() is { } pending &&
                string.Equals(pending.AccountId, accountId, StringComparison.Ordinal) &&
                string.Equals(pending.CreditId, creditId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(pending.IdempotencyKey))
            {
                return pending.IdempotencyKey;
            }

            var key = Guid.NewGuid().ToString("D");
            Write(new PendingRedemption(accountId, creditId, key));
            return key;
        }
    }

    /// <summary>
    /// Clears the record once the backend has given a definitive answer. Any of the four
    /// outcomes is definitive; a transport failure is not, and must leave the record intact.
    /// </summary>
    public static void CompleteAttempt()
    {
        lock (Sync)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true);
                key?.DeleteValue(PendingValueName, throwOnMissingValue: false);
            }
            catch
            {
                // A stale record only costs a replayed idempotency key, which is harmless.
            }
        }
    }

    private static PendingRedemption? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
            if (key?.GetValue(PendingValueName) is not string json || string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<PendingRedemption>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void Write(PendingRedemption pending)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
            key.SetValue(PendingValueName, JsonSerializer.Serialize(pending, JsonOptions), RegistryValueKind.String);
        }
        catch
        {
            // Losing the record only weakens retry safety; the redeem itself may still proceed.
        }
    }

    private sealed record PendingRedemption(string AccountId, string CreditId, string IdempotencyKey);
}
