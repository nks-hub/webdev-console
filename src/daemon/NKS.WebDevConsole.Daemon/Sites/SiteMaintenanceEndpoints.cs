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
/// MAMP discovery/import and reapply-all.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class SiteMaintenanceEndpoints
{
    public static void MapSiteMaintenanceEndpoints(this WebApplication app)
    {
        // MAMP PRO migration — Phase 5 plan item.
        // Discover-only: scan well-known MAMP install paths for vhost files, return parsed sites
        // without writing anything. Use POST /api/sites/migrate-mamp to actually import.
        app.MapGet("/api/sites/discover-mamp", (MampMigrator migrator) =>
        {
            try
            {
                var found = migrator.Discover();
                return Results.Ok(new { count = found.Count, sites = found });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Discovery failed: {ex.Message}");
            }
        });

        app.MapPost("/api/sites/migrate-mamp", async (MampMigrator migrator, SiteManager sm, SiteOrchestrator orchestrator) =>
        {
            try
            {
                var imported = await migrator.ImportAsync(sm);
                // Importing only wrote the site TOMLs, so the sites showed up in the
                // list but had no Apache vhost, hosts entry or certificate until the
                // user happened to save each one. Apply them like a normal create.
                var applied = new List<object>();
                foreach (var domain in imported)
                {
                    var site = sm.Get(domain);
                    if (site is null) continue;
                    try
                    {
                        var r = await orchestrator.ApplyAsync(site);
                        applied.Add(new { domain, ok = r.Success, steps = r.Steps });
                    }
                    catch (Exception ex)
                    {
                        applied.Add(new { domain, ok = false, error = ex.Message });
                    }
                }
                return Results.Ok(new { count = imported.Count, imported, applied });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Migration failed: {ex.Message}");
            }
        });

        app.MapPost("/api/sites/reapply-all", async (SiteManager sm, SiteOrchestrator orchestrator) =>
        {
            var results = new List<object>();
            foreach (var site in sm.Sites.Values)
            {
                try
                {
                    var r = await orchestrator.ApplyAsync(site);
                    results.Add(new { domain = site.Domain, ok = r.Success, steps = r.Steps });
                }
                catch (Exception ex)
                {
                    results.Add(new { domain = site.Domain, ok = false, error = ex.Message });
                }
            }
            return Results.Ok(results);
        });
    }
}
