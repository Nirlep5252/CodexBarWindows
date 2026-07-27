# CodexBarWindows

CodexBarWindows presents local assistant usage information from developer tools in a Windows tray interface.

## Language

**Claude History**:
A local-only token and estimated-cost history derived from native Claude Code project session files.
_Avoid_: Claude API usage, Claude quota usage, pi Claude sessions

**Claude Quota Usage**:
The current Claude rate-limit utilization shown from Claude OAuth usage data.
_Avoid_: Claude history, token cost

**Codex History**:
A local token and estimated-cost history derived from Codex and pi session files.
_Avoid_: Codex quota usage

**Provider History**:
A shared presentation of local token and estimated-cost history for a supported assistant provider over calendar days.
_Avoid_: Codex-only history UI, active session history

**Cache Creation Tokens**:
Claude input tokens charged for creating a reusable prompt cache entry.
_Avoid_: cached input, regular input

**Codex Reset Credit**:
A banked, expiring grant that clears the current Codex usage windows when redeemed. Owned per Codex account and consumed irreversibly.
_Avoid_: Codex credits balance, prepaid spend, plan renewal

**Codex Credits Balance**:
The prepaid spend reported as `credits` on a Codex rate-limit bucket (`hasCredits`, `balance`).
_Avoid_: Codex Reset Credit

## Relationships

- **Claude History** is separate from **Claude Quota Usage**.
- **Claude History** and **Codex History** both report token/cost history from local session logs.
- **Provider History** presents either **Claude History** or **Codex History** using the same user-facing structure labeled as usage history.
- **Claude History** distinguishes **Cache Creation Tokens** from regular and cached input because each has different pricing.
- A **Codex Reset Credit** is not a **Codex Credits Balance**: one resets a usage window, the other pays for usage. Both arrive in the same `account/rateLimits/read` response.
- Redeeming a **Codex Reset Credit** changes **Codex Quota Usage** and leaves **Codex History** untouched.

## Example dialogue

> **Dev:** "Should Claude History call the Claude API?"
> **Domain expert:** "No — Claude History is local-only; Claude Quota Usage may continue using OAuth usage data."

## Flagged ambiguities

- "Claude usage" can mean either **Claude History** or **Claude Quota Usage** — resolved: token/cost history is local-only, while quota usage remains the existing OAuth-backed feature.
- "local-only" for **Claude History** means no Claude API or Claude account access for session/token data; model pricing may be refreshed from a non-Claude pricing catalog when cached fallback is available.
- "Codex credits" can mean either a **Codex Reset Credit** or a **Codex Credits Balance** — resolved: reset credits are the redeemable window resets; the balance is prepaid spend and is not redeemable from this app.
