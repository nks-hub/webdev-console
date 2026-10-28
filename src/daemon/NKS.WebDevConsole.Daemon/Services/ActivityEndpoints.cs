using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using Dapper;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;
using NKS.WebDevConsole.Daemon.Cloudflare;
using NKS.WebDevConsole.Daemon.Ssl;
using NKS.WebDevConsole.Daemon.Hosts;
using NKS.WebDevConsole.Daemon.Deploy;
using CliWrap.Buffered;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Recent config-change activity timeline.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this WebApplication app)
    {
        // Recent activity — Phase 4 Dashboard timeline. Returns the most recent rows
        // from the config_history table (populated by SiteManager / ConfigValidator /
        // plugin lifecycle events) so the Dashboard can render an el-timeline of
        // what has happened recently: "created myapp.loc", "edited php.ini", etc.
        //
        // Returns a JSON array newest-first, shape:
        //   [{ id, entityType, entityName, operation, changedFields, source, createdAt }]
        //
        // Limit is clamped to [1, 200] so the timeline query stays bounded even if
        // a misbehaving client passes ?limit=999999.
        app.MapGet("/api/activity", async (Database db, int? limit) =>
        {
            var clamped = Math.Clamp(limit ?? 20, 1, 200);
            try
            {
                return Results.Ok(await ReadRecentAsync(db, clamped));
            }
            catch (Exception ex)
            {
                // Table may not exist on very first boot before migrations complete.
                // Return empty list rather than 500 so the Dashboard renders cleanly.
                return Results.Ok(new { error = ex.Message, entries = Array.Empty<object>() });
            }
        });
    }

    /// <summary>
    /// The settings a user changes in the app, and so the only ones the feed lists. The
    /// settings table also holds what the daemon writes on its own (<c>sync.deviceId</c>,
    /// the sign-in token and e-mail, <c>sync.lastSyncTime</c>, <c>catalog.plugin.*</c>); those
    /// are not activity. The Overview has a translated label for every name here
    /// (<c>dashboard.activity.setting.*</c>), and a test keeps the two lists in step.
    /// Adding a user-facing setting means adding its name here and its label there.
    /// </summary>
    internal static readonly string[] UserSettings =
    [
        "ports.http", "ports.https", "ports.mysql", "ports.redis", "ports.postgresql",
        "ports.mailpitSmtp", "ports.mailpitHttp", "ports.phpFpmBase",
        "general.defaultPhp", "php.activeVersion",
        "daemon.autoStartEnabled", "daemon.catalogUrl", "plugins.autoSyncEnabled",
        "deploy.enabled", "deploy.useLegacyHostHandlers",
        "mcp.enabled", "mcp.strict_kinds", "mcp.always_confirm_kinds",
        "mcp.grant_expired_retention_days", "mcp.grant_revoked_retention_days", "mcp.toolCallRetentionDays",
        "backup.scheduleHours", "backup.retainCount", "backup.dir",
        "backup.content.vhosts", "backup.content.pluginConfigs", "backup.content.ssl",
        "backup.content.databases", "backup.content.docroots",
        "sync.deviceName",
    ];

    /// <summary>
    /// Newest rows first. Setting rows are limited to <see cref="UserSettings"/> (and the
    /// per-service auto-start switch, <c>service.&lt;id&gt;.autoStart</c>) in the query, so the
    /// limit counts rows the feed shows.
    /// </summary>
    internal static async Task<IReadOnlyList<dynamic>> ReadRecentAsync(Database db, int limit)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync(
            @"SELECT id, entity_type AS entityType, entity_name AS entityName,
                     operation, changed_fields AS changedFields, source,
                     created_at AS createdAt
              FROM config_history
              WHERE entity_type <> 'setting'
                 OR entity_name IN @UserSettings
                 OR entity_name LIKE 'service.%.autoStart'
              ORDER BY id DESC
              LIMIT @Limit",
            new { Limit = limit, UserSettings });
        return rows.ToList();
    }
}
