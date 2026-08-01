namespace CodexBarWindows;

/// <summary>
/// The opaque per-provider identity used as a dictionary key everywhere usage is tracked.
/// Codex is keyed per configured CLI entry (two accounts are two providers); Claude, Grok
/// and Cursor are singletons.
/// </summary>
/// <remarks>
/// The WinForms UI declares the same strings on <c>UsagePopupForm</c>. They must stay
/// byte-identical: both UIs read the same registry-backed settings and the same journal.
/// </remarks>
public static class ProviderKeys
{
    public const string Claude = "claude";
    public const string Grok = "grok";
    public const string Cursor = "cursor";

    public static string Codex(string id) => $"codex:{id}";

    public static bool IsCodex(string providerKey) =>
        providerKey.StartsWith("codex:", StringComparison.Ordinal);

    public static UsageProvider ProviderOf(string providerKey) => providerKey switch
    {
        Claude => UsageProvider.Claude,
        Grok => UsageProvider.Grok,
        Cursor => UsageProvider.Cursor,
        _ => UsageProvider.Codex
    };
}
