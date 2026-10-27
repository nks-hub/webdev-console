using System.Globalization;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Mcp;

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
    /// What the daemon uses for a key nobody has saved yet, taken from the code
    /// that reads it. <c>GET /api/settings</c> merges these in, so a fresh
    /// install shows its effective configuration: an AI client reading it saw
    /// only <c>mcp.enabled</c> and <c>sync.deviceId</c>. Keys resolved at run
    /// time (MySQL, Redis, Mailpit and PostgreSQL ports, the catalog URL) have
    /// no fixed default and stay absent until saved.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = BuildDefaults();

    private static Dictionary<string, string> BuildDefaults()
    {
        static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ports.http"] = "80",
            ["ports.https"] = "443",
            ["backup.scheduleHours"] = Num(BackupScheduler.DefaultScheduleHours),
            ["backup.retainCount"] = Num(BackupScheduler.DefaultRetainCount),
            ["backup.content.vhosts"] = "true",
            ["backup.content.pluginConfigs"] = "true",
            ["backup.content.ssl"] = "true",
            ["backup.content.databases"] = "false",
            ["backup.content.docroots"] = "false",
            ["daemon.autoStartEnabled"] = "true",
            ["plugins.autoSyncEnabled"] = "false",
            ["deploy.enabled"] = "true",
            ["deploy.useLegacyHostHandlers"] = "true",
            ["mcp.enabled"] = "false",
            ["mcp.strict_kinds"] = "false",
            ["mcp.always_confirm_kinds"] = "",
            ["mcp.grant_expired_retention_days"] = Num(GrantSweeperService.DefaultExpiredRetentionDays),
            ["mcp.grant_revoked_retention_days"] = Num(GrantSweeperService.DefaultRevokedRetentionDays),
            ["mcp.toolCallRetentionDays"] = Num(McpToolCallsSweeperService.DefaultRetentionDays),
        };
        foreach (var capability in McpCapabilities.All)
            defaults[capability.SettingKey] = capability.DefaultEnabled ? "true" : "false";
        return defaults;
    }

    /// <summary>
    /// The <c>GET /api/settings</c> view: <see cref="Defaults"/> overlaid with
    /// the stored rows, without <see cref="Secret"/> keys.
    /// </summary>
    public static Dictionary<string, string> ForRead(IEnumerable<KeyValuePair<string, string>> stored)
    {
        var view = new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in stored)
        {
            if (!Secret.Contains(name)) view[name] = value;
        }
        return view;
    }

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
