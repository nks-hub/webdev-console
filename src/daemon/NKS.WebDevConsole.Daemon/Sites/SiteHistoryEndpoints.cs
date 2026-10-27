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

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// Site config history listing and rollback.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class SiteHistoryEndpoints
{
    public static void MapSiteHistoryEndpoints(this WebApplication app)
    {
        // List config history versions for a site (archived site TOMLs — the
        // source of truth the vhosts are rendered from).
        app.MapGet("/api/sites/{domain}/history", (string domain, SiteManager sm) =>
        {
            try { SiteManager.ValidateDomain(domain); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (sm.Get(domain) is null) return Results.NotFound();
            return Results.Ok(sm.ListHistory(domain).Select(e => new
            {
                timestamp = e.Timestamp,
                size = e.Size,
                createdAt = e.CreatedAt,
            }));
        });

        // Rollback a site's config to a historical version. Previously this copied
        // an archived *vhost* into generated/ and then ran ApplyAsync, which
        // re-rendered the vhost from the unchanged TOML — so the rollback was
        // silently undone. Restoring the TOML and re-applying is what actually
        // brings the old configuration back. The timestamp may come from the path
        // (CLI) or a JSON body {"timestamp": "..."} (GUI).
        async Task<IResult> RollbackSiteAsync(string domain, string? timestamp, SiteManager sm, SiteOrchestrator orchestrator)
        {
            try { SiteManager.ValidateDomain(domain); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (string.IsNullOrWhiteSpace(timestamp))
                return Results.BadRequest(new { error = "timestamp is required" });
            if (sm.Get(domain) is null) return Results.NotFound(new { error = $"Site {domain} not found" });

            SiteConfig? restored;
            try
            {
                restored = await sm.RestoreFromHistoryAsync(domain, timestamp);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
            if (restored is null)
                return Results.NotFound(new { error = $"History entry {timestamp} not found" });

            await orchestrator.ApplyAsync(restored);
            return Results.Ok(new { domain, restoredFrom = timestamp });
        }

        app.MapPost("/api/sites/{domain}/rollback/{timestamp}",
            (string domain, string timestamp, SiteManager sm, SiteOrchestrator orchestrator) =>
                RollbackSiteAsync(domain, timestamp, sm, orchestrator));

        app.MapPost("/api/sites/{domain}/rollback", async (string domain, HttpContext ctx, SiteManager sm, SiteOrchestrator orchestrator) =>
        {
            string? timestamp = null;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("timestamp", out var ts) &&
                    ts.ValueKind == System.Text.Json.JsonValueKind.String)
                    timestamp = ts.GetString();
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON body" });
            }
            return await RollbackSiteAsync(domain, timestamp, sm, orchestrator);
        });
    }
}
