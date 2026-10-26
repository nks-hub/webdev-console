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
                using var conn = db.CreateConnection();
                var rows = await conn.QueryAsync(
                    @"SELECT id, entity_type AS entityType, entity_name AS entityName,
                             operation, changed_fields AS changedFields, source,
                             created_at AS createdAt
                      FROM config_history
                      ORDER BY id DESC
                      LIMIT @Limit",
                    new { Limit = clamped });
                return Results.Ok(rows);
            }
            catch (Exception ex)
            {
                // Table may not exist on very first boot before migrations complete.
                // Return empty list rather than 500 so the Dashboard renders cleanly.
                return Results.Ok(new { error = ex.Message, entries = Array.Empty<object>() });
            }
        });
    }
}
