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
/// Uninstall / cleanup of managed state.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class UninstallEndpoints
{
    public static void MapUninstallEndpoints(this WebApplication app)
    {
        // Uninstall / cleanup — removes the managed state (sites, db, certs, logs,
        // optionally binaries and the managed hosts file block). ALWAYS creates a
        // safety backup before touching anything. Destructive endpoint: requires a
        // confirmation token in the body to prevent accidental triggers.
        //
        // Modes:
        //   dryRun:true   — report what would be removed, no writes
        //   purge:true    — also wipe ~/.wdc/binaries and ~/.wdc (default: preserve binaries)
        //   hosts:true    — also strip the managed BEGIN/END block from hosts file
        app.MapPost("/api/uninstall", async (HttpContext ctx, BackupManager backupManager, SiteOrchestrator orchestrator, SiteManager sm, IServiceProvider sp, ILoggerFactory lf) =>
        {
            Dictionary<string, object?>? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>();
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
            }
            bool flag(string key) => body is not null && body.TryGetValue(key, out var v) && v switch
            {
                bool b => b,
                System.Text.Json.JsonElement j when j.ValueKind == System.Text.Json.JsonValueKind.True => true,
                _ => false,
            };
            string? str(string key) => body?.TryGetValue(key, out var v) == true ? v?.ToString() : null;

            var confirm = str("confirm");
            var dryRun = flag("dryRun");
            var purge = flag("purge");
            var removeHostsBlock = flag("hosts");

            if (!dryRun && confirm != "YES-UNINSTALL")
                return Results.BadRequest(new { error = "Destructive operation — pass confirm: \"YES-UNINSTALL\" in the body, or set dryRun: true." });

            var log = lf.CreateLogger("Uninstall");
            var wdcRoot = NKS.WebDevConsole.Core.Services.WdcPaths.Root;
            var plan = new List<string>();
            string? safetyBackupPath = null;

            // 1. Stop all services (best-effort, continue on failure).
            var modules = sp.GetServices<IServiceModule>();
            foreach (var m in modules)
            {
                plan.Add($"stop service {m.ServiceId}");
                if (!dryRun)
                {
                    try
                    {
                        var status = await m.GetStatusAsync(CancellationToken.None);
                        if (status.State != ServiceState.Stopped)
                            await m.StopAsync(CancellationToken.None);
                    }
                    catch (Exception ex) { log.LogWarning(ex, "Stop {Service} during uninstall", m.ServiceId); }
                }
            }

            // 2. Safety backup before ANY deletion.
            plan.Add("create pre-uninstall safety backup");
            if (!dryRun)
            {
                try
                {
                    safetyBackupPath = backupManager.CreateBackup().Path;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Pre-uninstall safety backup failed");
                    return Results.Problem(title: "Safety backup failed — uninstall aborted", detail: ex.Message, statusCode: 500);
                }
            }

            // 3. Remove managed hosts file block (only if requested — user may have other tooling).
            if (removeHostsBlock)
            {
                plan.Add("strip managed block from hosts file");
                if (!dryRun)
                {
                    try
                    {
                        // Reuse the orchestrator's hosts update path with an empty domain list — that
                        // regenerates the managed block as empty (or removes it entirely).
                        await orchestrator.UpdateHostsBlockAsync(Array.Empty<string>(), CancellationToken.None);
                    }
                    catch (Exception ex) { log.LogWarning(ex, "Hosts block cleanup"); }
                }
            }

            // 4. Remove generated vhosts + site TOMLs + DB + logs. Binaries kept unless purge.
            var subpathsToRemove = new List<string>
            {
                Path.Combine(wdcRoot, "sites"),
                Path.Combine(wdcRoot, "generated"),
                Path.Combine(wdcRoot, "data"),
                Path.Combine(wdcRoot, "logs"),
                Path.Combine(wdcRoot, "ssl"),
                Path.Combine(wdcRoot, "caddy"),
                Path.Combine(wdcRoot, "cache"),
            };
            if (purge)
                subpathsToRemove.Add(Path.Combine(wdcRoot, "binaries"));

            foreach (var path in subpathsToRemove)
            {
                if (!Directory.Exists(path)) continue;
                plan.Add($"delete {path}");
                if (!dryRun)
                {
                    try { Directory.Delete(path, recursive: true); }
                    catch (Exception ex) { log.LogWarning(ex, "Failed to delete {Path}", path); }
                }
            }

            return Results.Ok(new
            {
                dryRun,
                purge,
                removeHostsBlock,
                safetyBackup = safetyBackupPath,
                plan,
                message = dryRun
                    ? "Dry run — no changes made. Re-send with confirm: \"YES-UNINSTALL\" to execute."
                    : "Uninstall complete. Stop the daemon and delete the wdc-cli binary to fully remove.",
            });
        });
    }
}
