namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Rules for the flat <c>category.key</c> names used by <c>/api/settings</c>.
/// </summary>
internal static class SettingsKeys
{
    /// <summary>
    /// Stored for the daemon's own use (catalog heartbeat, sync proxy) but never
    /// returned by <c>GET /api/settings</c>: the catalog account JWT ended up in
    /// settings exports, cloud pushes and in front of MCP clients.
    /// </summary>
    public static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase)
    {
        "sync.accountToken",
    };

    /// <summary>
    /// Names from <paramref name="incoming"/> that are new or carry a value
    /// different from <paramref name="stored"/>. Side effects of a save (vhost
    /// regeneration, service reloads) key off this, not off what was sent.
    /// </summary>
    public static HashSet<string> Changed(
        IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string> incoming) =>
        incoming
            .Where(kv => !stored.TryGetValue(kv.Key, out var old) || old != kv.Value)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Splits <c>backup.content.ssl</c> into <c>backup</c> + <c>content.ssl</c>.
    /// Returns false for names without a category or without a key, which used
    /// to land under <c>general.*</c> and silently save nothing the caller meant.
    /// </summary>
    public static bool TrySplit(string compositeKey, out string category, out string key)
    {
        var dot = compositeKey.IndexOf('.');
        category = dot > 0 ? compositeKey[..dot] : "";
        key = dot > 0 ? compositeKey[(dot + 1)..] : "";
        return category.Length > 0 && key.Length > 0;
    }
}
