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
/// Admin routes: plugin readiness, shutdown, restart and reset.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app, bool legacyHostHandlersAtBoot)
    {
        // Phase 7.4 #109-D1 — plugin migration readiness diagnostic. Operator
        // (or future automation) reads this before flipping
        // deploy.useLegacyHostHandlers=false to know whether the plugin has
        // feature parity. Returns a structured advisory:
        //   - mode: "built-in" | "plugin" — current authoritative backend
        //   - pluginLoaded: bool, pluginVersion: string|null
        //   - skippedRoutes: int — host-conflict-skipped plugin endpoints
        //     (these would activate after a flip)
        //   - readyToFlip: bool — true only when phase B (endpoint parity) +
        //     phase C (plugin-side ZIP/SSE/Slack) are observably done. For
        //     today: always false because plugin scaffold uses the obsolete
        //     PHP-CLI-via-CliWrap design and can't replace the live native
        //     LocalDeployBackend without phase B/C/D work.
        //   - blockers: string[] — human-readable reasons a flip would break
        //     things. Empty when readyToFlip=true.
        //   - bootLegacyHostHandlers: bool — value baked at daemon boot.
        //   - restartPending: bool — true when current setting differs from boot.
        app.MapGet("/api/admin/plugin-readiness", (
            PluginLoader pl,
            SettingsStore settings,
            HttpContext ctx) =>
        {
            var deployPlugin = pl.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.deploy");
            var pluginLoaded = deployPlugin is not null;
            var pluginVersion = deployPlugin?.Instance.Version;
            var legacyHandlers = settings.GetBool("deploy", "useLegacyHostHandlers", defaultValue: true);
            var mode = legacyHandlers ? "built-in" : "plugin";
            // Today's static blockers for the flip — surfaced even when no
            // plugin is loaded so operators see WHY they can't flip yet.
            // Iter 17 #109-D1+: each blocker also gets a structured detail
            // record (phase + remediation) used when ?explain=true. Keeping
            // the flat blockers[] for back-compat with iter 5/6 consumers.
            var blockerDetails = new List<(string Summary, string Phase, string Remediation)>();
            if (!pluginLoaded)
                blockerDetails.Add((
                    "nks.wdc.deploy plugin not loaded — DLL missing in build/plugins/ or plugin disabled",
                    "A",
                    "Stage NksDeploy plugin DLL into build/plugins/ and restart daemon. Verify via GET /api/plugins."));
            // Phase B: complete in plugin@feature/nksdeploy-wdc-integration
            // (commits 8958565, 51a1d19, 6f3c12e, c7b87ff, 74c61c8). All 5
            // host-only endpoints — test-host-connection, sites/{domain}/deploy,
            // rollback-to, hooks/test, notifications/test — now ship in
            // NksDeployRoutes.cs. The route-conflict guard still defers to the
            // daemon handlers while useLegacyHostHandlers=true.
            // Phase C: test-hook executor + Slack dispatch shipped (commits
            // 2893d0b, abf5090) — ZIP snapshot writer + SSE deploy:hook bridge
            // remain.
            // Phase C complete: ZIP snapshot service (commit 6cd22a5/6608838),
            // test-hook executor (2893d0b), Slack dispatch (abf5090), SSE
            // deploy:hook bridge (967c6ef) all shipped on plugin side.
            blockerDetails.Add((
                "phase D: plugin-only e2e on blog.loc + shop.loc not yet validated",
                "D",
                "Run tools/e2e-mcp-deploy.sh against a daemon with deploy.useLegacyHostHandlers=false; all 264 sections must pass."));
            var blockers = blockerDetails.ConvertAll(b => b.Summary);
            var readyToFlip = blockers.Count == 0;
            // Phase D (#258) — operator flipped the setting but the daemon is
            // still running with the boot-time value. The 3 conditional host
            // handlers honour the boot value, so the GUI needs to know "restart
            // to apply" and the readiness recommendation should reflect drift.
            var restartPending = legacyHandlers != legacyHostHandlersAtBoot;
            // Iter 62 — surface exactly which daemon endpoints are wrapped in
            // `if (legacyHostHandlersAtBoot) { ... }` blocks. Operators reading
            // the diagnostic see the cutover scope at a glance instead of having
            // to grep Program.cs. Each entry pairs HTTP verb with the route. The
            // list grows as more handlers join the gate; today's set covers the
            // 11 handlers shipped via iter 55-66 (incl. the snapshot restore aliases).
            var gatedEndpoints = new[]
            {
                "POST /test-host-connection",
                "POST /sites/{domain}/hooks/test",
                "POST /sites/{domain}/notifications/test",
                "POST /sites/{domain}/snapshot-now",
                "POST /sites/{domain}/restore",
                "POST /sites/{domain}/snapshots/{snapshotId}/restore",
                "GET /sites/{domain}/history",
                "GET /sites/{domain}/deploys/{deployId}",
                "GET /sites/{domain}/snapshots",
                "GET /sites/{domain}/settings",
                "GET /sites/{domain}/groups",
            };
            var explain = ctx.Request.Query["explain"].ToString() == "true";
            var baseRecommendation = readyToFlip
                ? "Plugin parity proven — safe to flip useLegacyHostHandlers=false"
                : $"Stay on built-in mode — {blockers.Count} blocker(s) listed above";
            var recommendation = restartPending
                ? baseRecommendation + " — daemon restart required to apply current setting"
                : baseRecommendation;
            if (explain)
            {
                return Results.Ok(new
                {
                    mode,
                    pluginLoaded,
                    pluginVersion,
                    useLegacyHostHandlers = legacyHandlers,
                    bootLegacyHostHandlers = legacyHostHandlersAtBoot,
                    restartPending,
                    readyToFlip,
                    blockers,
                    blockerDetails = blockerDetails.ConvertAll(b => new
                    {
                        summary = b.Summary,
                        phase = b.Phase,
                        remediation = b.Remediation,
                    }),
                    gatedEndpoints,
                    recommendation,
                });
            }
            return Results.Ok(new
            {
                mode,
                pluginLoaded,
                pluginVersion,
                useLegacyHostHandlers = legacyHandlers,
                bootLegacyHostHandlers = legacyHostHandlersAtBoot,
                restartPending,
                readyToFlip,
                blockers,
                gatedEndpoints,
                // Diagnostic: pretty-print this in the GUI so operators don't
                // have to dig in /api/plugins separately.
                recommendation,
            });
        });

        app.MapPost("/api/admin/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            _ = Task.Run(() => lifetime.StopApplication());
            return Results.Accepted();
        });

        // F91.7/F91.8: graceful restart — daemon exits with code 99, which
        // Electron's main process treats as "respawn me". Used after uninstall to
        // fully unload locked plugin DLLs without forcing the user to kill the app.
        // Uses Environment.Exit(99) rather than ExitCode + StopApplication because
        // Program.cs does not `return 99` from Main — after graceful shutdown the
        // process exits with code 0 and Electron never sees the restart signal.
        app.MapPost("/api/admin/restart", () =>
        {
            _ = Task.Run(async () =>
            {
                // Delay so the HTTP 202 response flushes + port file unlock path
                // completes before we slam the process. 300ms is empirically safe
                // on both Windows (locked file handles) and Linux.
                await Task.Delay(300);
                // Best-effort port file cleanup so the respawned daemon doesn't
                // reuse a stale port/token combo while it is still booting.
                try
                {
                    var pf = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
                    if (File.Exists(pf)) File.Delete(pf);
                }
                catch { /* Electron will just see stale file briefly — harmless */ }
                Environment.Exit(99);
            });
            return Results.Accepted();
        });

        // POST /api/admin/reset?scope=settings|factory — destructive reset.
        //   scope=settings → wipes only the `settings` table (ports, paths, catalog
        //     URL, autoStart flags, sync tokens). Sites/databases/binaries are kept.
        //   scope=factory  → TRUE factory reset. Stops every service (Apache, MySQL,
        //     MariaDB, PHP-FPM, Redis, …), signals Electron that WDC_DATA_DIR and
        //     the renderer's Electron userData (localStorage with accountToken,
        //     cookies, caches) must be wiped before the next spawn. Daemon exits
        //     with special code 98 — Electron's daemon.on('exit') handler
        //     recognises 98 as "wipe ~/.wdc/ then respawn". On next boot the
        //     app comes up exactly like a fresh install: no sites, no databases,
        //     no binaries, no signed-in account. Previous scope=factory kept
        //     ~/.wdc/binaries/ (1 GB Apache/PHP/MySQL) + ~/.wdc/data/{mysql,mariadb}/
        //     which was a UX footgun — user clicked "Factory reset" and saw their
        //     data / credentials / installed PHP versions still there.
        app.MapPost("/api/admin/reset", async (HttpContext ctx, Database db, SiteManager siteManager, SiteOrchestrator orchestrator, string? scope) =>
        {
            // Scope is mandatory — missing param used to default to "settings" and
            // that was a footgun (blank curl call silently wiped the DB). Force the
            // caller to say what they want.
            if (string.IsNullOrWhiteSpace(scope))
                return Results.BadRequest(new { error = "scope query parameter is required: 'settings' or 'factory'" });
            var effectiveScope = scope.Trim().ToLowerInvariant();
            if (effectiveScope != "settings" && effectiveScope != "factory")
                return Results.BadRequest(new { error = "scope must be 'settings' or 'factory'" });

            var resetLogger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();

            if (effectiveScope == "factory")
            {
                // Delete every site from SiteManager FIRST (wipes TOML files on
                // disk) so the following UpdateHostsBlockAsync call collects an
                // empty domain set and actually clears the /etc/hosts managed
                // block. Before this ordering fix the hosts call collected the
                // still-loaded sites from SiteManager and kept the block intact.
                var domainsToDelete = siteManager.Sites.Keys.ToList();
                foreach (var d in domainsToDelete)
                {
                    try { siteManager.Delete(d); }
                    catch (Exception ex) { resetLogger.LogWarning(ex, "[factory-reset] site {Domain} delete failed", d); }
                }

                // Strip the managed /etc/hosts block BEFORE stopping services —
                // this needs elevation (osascript admin / pkexec / UAC) and the
                // dialog is easier to reason about while the UI is still live.
                // Without this, a user's `/etc/hosts` keeps `127.0.0.1 myapp.loc`
                // entries pointing at a server that no longer exists after reset.
                try
                {
                    await orchestrator.UpdateHostsBlockAsync(Array.Empty<string>(), ctx.RequestAborted);
                    resetLogger.LogInformation("[factory-reset] /etc/hosts managed block cleared");
                }
                catch (Exception ex)
                {
                    resetLogger.LogWarning(ex, "[factory-reset] hosts block cleanup failed — user may need to manually remove the # BEGIN NKS WebDev Console block");
                }

                // Stop every service module BEFORE we nuke the data dir — otherwise
                // mariadbd / mysqld / httpd / redis-server keep open file handles
                // inside ~/.wdc/data/, causing either undeletable files on macOS/
                // Linux or partial wipe leaving corrupted pid/lock files that poison
                // the next boot. 5 s cooperative cancellation per module — any that
                // hang past that get orphaned and will be SIGKILLed when daemon
                // exits.
                var modules = ctx.RequestServices.GetServices<IServiceModule>().ToArray();
                foreach (var mod in modules)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await mod.StopAsync(cts.Token);
                        resetLogger.LogInformation("[factory-reset] {Service} stopped", mod.ServiceId);
                    }
                    catch (Exception ex)
                    {
                        resetLogger.LogWarning(ex, "[factory-reset] {Service} stop failed — continuing", mod.ServiceId);
                    }
                }
            }

            try
            {
                using var conn = db.CreateConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();
                await conn.ExecuteAsync("DELETE FROM settings", transaction: tx);
                await conn.ExecuteAsync("DELETE FROM config_history WHERE entity_type = 'setting'", transaction: tx);
                tx.Commit();
                conn.Close();  // Release SQLite handle so the file can be unlinked below.
            }
            catch (Exception ex)
            {
                return Results.Problem($"Reset failed: {ex.Message}");
            }

            // For scope=factory we rely on Electron to rm -rf ~/.wdc/ after the
            // daemon exits with code 98. The daemon can't delete its OWN running
            // directory reliably (Mach-O binary mmapped on macOS, OS file locks
            // on Windows), so we signal the parent and let Electron do the nuke
            // between daemon invocations. See src/frontend/electron/main.ts
            // `daemon.on('exit', code => ...)` — code 98 branch.
            var exitCode = effectiveScope == "factory" ? 98 : 99;

            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                try
                {
                    var pf = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
                    if (File.Exists(pf)) File.Delete(pf);
                }
                catch { }
                Environment.Exit(exitCode);
            });
            return Results.Ok(new { scope = effectiveScope, restarting = true, exitCode });
        });
    }
}
