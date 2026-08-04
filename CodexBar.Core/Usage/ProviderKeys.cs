namespace CodexBarWindows;

/// <summary>
/// The opaque per-provider identity used as a dictionary key everywhere usage is tracked.
/// Codex and Grok are keyed per configured account (two accounts are two providers); Claude,
/// Cursor, and OpenCode Go are singletons.
/// </summary>
/// <remarks>
/// The WinForms UI declares the same strings on <c>UsagePopupForm</c>. They must stay
/// byte-identical: both UIs read the same registry-backed settings and the same journal.
/// The WinForms shell predates multi-account Grok and still uses the bare <c>"grok"</c> string
/// for its single card; nothing persists a Grok provider key, so the two never have to agree.
/// </remarks>
public static class ProviderKeys
{
    public const string Claude = "claude";
    public const string Cursor = "cursor";
    public const string OpenCodeGo = "opencodego";

    public static string Codex(string id) => $"codex:{id}";

    public static string Grok(string id) => $"grok:{id}";

    public static bool IsCodex(string providerKey) =>
        providerKey.StartsWith("codex:", StringComparison.Ordinal);

    public static bool IsGrok(string providerKey) =>
        providerKey.StartsWith("grok:", StringComparison.Ordinal);

    public static UsageProvider ProviderOf(string providerKey) => providerKey switch
    {
        Claude => UsageProvider.Claude,
        Cursor => UsageProvider.Cursor,
        OpenCodeGo => UsageProvider.OpenCodeGo,
        _ when IsGrok(providerKey) => UsageProvider.Grok,
        _ => UsageProvider.Codex
    };
}
