using System.Text.Json;

namespace CodexBarWindows;

/// <summary>Definitive results of <c>account/rateLimitResetCredit/consume</c>.</summary>
public enum CodexResetOutcome
{
    /// <summary>A credit was consumed and the eligible rate-limit windows were reset.</summary>
    Reset,

    /// <summary>No current rate-limit window is eligible for a reset.</summary>
    NothingToReset,

    /// <summary>The account has no earned reset credits available.</summary>
    NoCredit,

    /// <summary>The same idempotency key already completed a reset successfully.</summary>
    AlreadyRedeemed,

    /// <summary>The attempt did not reach a definitive answer; see the error message.</summary>
    Failed
}

public sealed record CodexResetRedeemResult(CodexResetOutcome Outcome, string? Error = null)
{
    /// <summary>Whether usage windows may have changed and are worth re-reading.</summary>
    public bool ChangedUsage => Outcome is CodexResetOutcome.Reset;
}

/// <summary>
/// Redeems a banked Codex rate-limit reset credit through the Codex CLI app-server, which
/// carries the CLI's own account authentication.
/// </summary>
/// <remarks>
/// One redeemer targets one Codex CLI binary, and therefore one account. The credit id is
/// always sent explicitly: omitting it lets the backend pick from whichever account that
/// binary is signed into, turning an account-routing bug into a silent wrong-account spend
/// instead of a loud <see cref="CodexResetOutcome.NoCredit"/>.
/// </remarks>
public sealed class CodexResetCreditRedeemer
{
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(30);
    private readonly string? codexPath;
    private readonly string accountId;

    public CodexResetCreditRedeemer(string accountId, string? codexPath)
    {
        this.accountId = accountId;
        this.codexPath = string.IsNullOrWhiteSpace(codexPath) ? null : codexPath;
    }

    /// <summary>
    /// Consumes <paramref name="creditId"/>. Spends a real, non-refundable credit when the
    /// outcome is <see cref="CodexResetOutcome.Reset"/>.
    /// </summary>
    public CodexResetRedeemResult Redeem(string creditId)
    {
        if (string.IsNullOrWhiteSpace(creditId))
        {
            return new CodexResetRedeemResult(CodexResetOutcome.Failed, "No reset credit was selected.");
        }

        var resolvedCodexPath = CodexAppServerSession.ResolveExecutable(codexPath);
        if (resolvedCodexPath is null)
        {
            return new CodexResetRedeemResult(
                CodexResetOutcome.Failed,
                codexPath is null
                    ? "Codex CLI was not found on PATH."
                    : $"Codex CLI was not found: {codexPath}");
        }

        var idempotencyKey = CodexResetRedemptionJournal.BeginAttempt(accountId, creditId);

        try
        {
            using var session = CodexAppServerSession.Start(resolvedCodexPath, RpcTimeout);
            var response = session.Request(
                "account/rateLimitResetCredit/consume",
                new { idempotencyKey, creditId });

            var outcome = ParseOutcome(response);
            if (outcome is null)
            {
                // An unreadable reply is not a definitive answer, so the idempotency key
                // stays recorded and a retry cannot double-spend.
                return new CodexResetRedeemResult(
                    CodexResetOutcome.Failed,
                    "Codex CLI returned an unrecognised reset outcome.");
            }

            CodexResetRedemptionJournal.CompleteAttempt();
            return new CodexResetRedeemResult(outcome.Value);
        }
        catch (Exception exception)
        {
            return new CodexResetRedeemResult(CodexResetOutcome.Failed, exception.Message);
        }
    }

    internal static CodexResetOutcome? ParseOutcome(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("outcome", out var outcome) ||
            outcome.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return outcome.GetString() switch
        {
            "reset" => CodexResetOutcome.Reset,
            "nothingToReset" or "nothing_to_reset" => CodexResetOutcome.NothingToReset,
            "noCredit" or "no_credit" => CodexResetOutcome.NoCredit,
            "alreadyRedeemed" or "already_redeemed" => CodexResetOutcome.AlreadyRedeemed,
            _ => null
        };
    }

    /// <summary>User-facing summary of an attempt.</summary>
    public static string DescribeOutcome(CodexResetRedeemResult result)
    {
        return result.Outcome switch
        {
            CodexResetOutcome.Reset => "Reset applied. Refreshing usage…",
            CodexResetOutcome.NothingToReset => "No limit is eligible for a reset right now.",
            CodexResetOutcome.NoCredit => "No resets are available on this account.",
            CodexResetOutcome.AlreadyRedeemed => "That reset was already used.",
            _ => string.IsNullOrWhiteSpace(result.Error)
                ? "Couldn't reset usage. Please try again."
                : $"Couldn't reset usage: {result.Error}"
        };
    }
}
