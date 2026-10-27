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

namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Settings read/update routes.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        // Settings CRUD
        app.MapGet("/api/settings", async (Database db) =>
        {
            using var conn = db.CreateConnection();
            var settings = await conn.QueryAsync<dynamic>("SELECT category, key, value FROM settings");
            var dict = new Dictionary<string, string>();
            foreach (var s in settings)
            {
                string name = $"{s.category}.{s.key}";
                if (!SettingsKeys.Secret.Contains(name)) dict[name] = s.value;
            }
            return Results.Ok(dict);
        });

        // Settings → Ports asks before saving a port another process listens on.
        app.MapGet("/api/ports/check", (int port) =>
        {
            if (port is < 1 or > 65535) return Results.BadRequest(new { error = "port must be 1-65535" });
            var conflict = PortConflictDetector.CheckPort(port);
            return Results.Ok(new { port, inUse = conflict is not null, ownerPid = conflict?.OwnerPid, ownerName = conflict?.OwnerProcessName });
        });

        app.MapPut("/api/settings", async (
            HttpContext ctx, Database db,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus) =>
        {
            // Frontend sends mixed-type JSON — booleans for toggles, numbers for
            // timeouts, strings for URLs. Hard-typing to Dictionary<string,string>
            // rejected any non-string value with a 500 (JsonException: "could not
            // be converted to string"). Read as JsonElement and stringify here so
            // the storage layer stays TEXT-only (simple schema) but the API is
            // permissive.
            Dictionary<string, JsonElement>? raw;
            try
            {
                raw = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
            }
            if (raw == null) return Results.BadRequest();

            var malformed = raw.Keys.Where(k => !SettingsKeys.TrySplit(k, out _, out _)).ToList();
            if (malformed.Count > 0)
                return Results.BadRequest(new { error = $"Setting names must look like category.key: {string.Join(", ", malformed)}" });

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in raw)
            {
                settings[k] = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    JsonValueKind.Number => v.GetRawText(),
                    // Objects/arrays → store as JSON string. The read side can parse
                    // it back. This keeps richer types usable without a schema change.
                    _ => v.GetRawText(),
                };
            }

            using var conn = db.CreateConnection();
            conn.Open();
            // Wrap the batch in a transaction so a sync-pull that touches many
            // keys is atomic: either all rows land or none do. Without this, a
            // partial failure (disk full, connection reset) could leave the
            // settings table with a half-applied snapshot where some keys are
            // from the new device and others are stale.
            using var tx = conn.BeginTransaction();
            HashSet<string> changed;
            try
            {
                var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in await conn.QueryAsync<dynamic>("SELECT category, key, value FROM settings", transaction: tx))
                    stored[$"{row.category}.{row.key}"] = (string)row.value;
                changed = SettingsKeys.Changed(stored, settings);

                foreach (var (compositeKey, value) in settings)
                {
                    SettingsKeys.TrySplit(compositeKey, out var category, out var key);
                    await conn.ExecuteAsync(
                        "INSERT INTO settings (category, key, value) VALUES (@Category, @Key, @Value) ON CONFLICT(category, key) DO UPDATE SET value = @Value, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')",
                        new { Category = category, Key = key, Value = value },
                        transaction: tx);
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            // Phase 7.5+++ — broadcast mcp:settings-changed when any mcp.* key
            // was touched in this batch so live MCP pages (Kinds, Hub, Settings
            // open in another tab) re-fetch and reflect the change without
            // operator hitting Refresh. Lists the affected keys for telemetry.
            var mcpKeys = settings.Keys.Where(k => k.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)).ToList();
            if (mcpKeys.Count > 0)
            {
                try { await eventsBus.BroadcastAsync("mcp:settings-changed", new { keys = mcpKeys }); }
                catch { /* best-effort SSE — never block the save path */ }
            }

            // Phase 7.4 #109-D1+ iter 28 — deploy.* settings changes broadcast
            // their own SSE event so DeploySettingsPanel + Settings.vue popovers
            // refresh readiness without polling. Today only useLegacyHostHandlers
            // matters for popovers; future deploy.* keys (timeouts, default
            // hooks, etc.) get the same live-refresh for free.
            var deployKeys = settings.Keys.Where(k => k.StartsWith("deploy.", StringComparison.OrdinalIgnoreCase)).ToList();
            if (deployKeys.Count > 0)
            {
                try { await eventsBus.BroadcastAsync("deploy:settings-changed", new { keys = deployKeys }); }
                catch { /* best-effort SSE — never block the save path */ }
            }

            // Propagate port changes to live plugin config — without this, edits
            // to ports.http / ports.https / ports.redis / ports.mailpit* sat in
            // SQLite forever and never reached the service module. We look for
            // any port key in the batch and nudge the matching service to reload
            // its config (regenerate httpd.conf etc). Reflection is used because
            // IServiceModule lives in Core but each plugin's module type is
            // cross-ALC — we only need the ReloadAsync method to be callable.
            // Only ports whose value actually changed count: a save that
            // re-sends the current ports must not regenerate every vhost and
            // reload Apache, MySQL, Redis and Mailpit.
            var portKeys = changed.Where(k => k.StartsWith("ports.", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (portKeys.Length > 0)
            {
                var sp = ctx.RequestServices;
                var modules = sp.GetServices<IServiceModule>().ToArray();
                foreach (var module in modules)
                {
                    // Heuristic: match `ports.<serviceId>` or any port key that
                    // looks like it could belong to this module (http/https map
                    // to apache/caddy/nginx; database/cache keys map 1:1).
                    var moduleId = module.ServiceId.ToLowerInvariant();
                    var relevant = portKeys.Any(k =>
                    {
                        var key = k.Substring("ports.".Length).ToLowerInvariant();
                        if (key == moduleId) return true;
                        if (key.StartsWith(moduleId)) return true;
                        // http/https touch any webserver module
                        if ((key == "http" || key == "https") &&
                            (moduleId == "apache" || moduleId == "caddy" || moduleId == "nginx"))
                            return true;
                        return false;
                    });
                    if (!relevant) continue;

                    // Poke the module's private config object BEFORE reload so
                    // the regenerated config file actually reflects the new port.
                    // The plugin's SDK version doesn't expose a hydrated
                    // IWdcSettings-aware constructor (would need a new SDK
                    // release), so we reach into the `_config` field via
                    // reflection and set HttpPort/HttpsPort directly when the
                    // property names look right. Private-field access across the
                    // plugin ALC is allowed because AssemblyLoadContext shares
                    // the same CLR and reflection isn't blocked by default.
                    try
                    {
                        var configField = module.GetType().GetField("_config",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        var config = configField?.GetValue(module);
                        if (config is not null)
                        {
                            foreach (var pk in portKeys)
                            {
                                var key = pk.Substring("ports.".Length).ToLowerInvariant();
                                // Map common Settings keys to plugin-config property
                                // names. Extend this table when new plugins get
                                // their own port keys (mailpit/redis/mysql all
                                // use different SettingsKey → PropertyName names).
                                var propName = key switch
                                {
                                    "http"  when moduleId == "apache" || moduleId == "caddy" || moduleId == "nginx" => "HttpPort",
                                    "https" when moduleId == "apache" || moduleId == "caddy" || moduleId == "nginx" => "HttpsPort",
                                    "mysql"       when moduleId == "mysql"        => "Port",
                                    "mariadb"     when moduleId == "mariadb"      => "Port",
                                    "postgresql"  when moduleId == "postgresql"   => "Port",
                                    "redis"       when moduleId == "redis"        => "Port",
                                    "mailpitsmtp" when moduleId == "mailpit"      => "SmtpPort",
                                    "mailpithttp" when moduleId == "mailpit"      => "HttpPort",
                                    _ => null,
                                };
                                if (propName is null) continue;
                                var prop = config.GetType().GetProperty(propName);
                                if (prop?.CanWrite == true && int.TryParse(settings[pk], out var port) && port > 0)
                                {
                                    prop.SetValue(config, port);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var logger = sp.GetRequiredService<ILogger<Program>>();
                        logger.LogWarning(ex, "Port-change config hydration for {Service} failed (reflection)", module.ServiceId);
                    }

                    // Phase 6.20 — when the changed key is ports.http or ports.https
                    // AND the module is a webserver (apache/caddy/nginx), per-site
                    // vhost configs in {VhostsDirectory}/*.conf bake the port number
                    // into `<VirtualHost *:PORT>` at write-time. A pure ReloadAsync
                    // re-runs httpd.conf but leaves stale per-site files referencing
                    // the OLD port, so Apache binds the new Listen but no vhost
                    // matches → all requests fall to the default htdocs page.
                    //
                    // Fix: for webserver modules with HTTP/HTTPS port changes, iterate
                    // every registered site and call the module's GenerateVhostAsync
                    // BEFORE reload. Reflection-based for the same cross-ALC reasons
                    // as the config-hydration block above.
                    var webServerModule = moduleId is "apache" or "caddy" or "nginx";
                    var httpPortChanged = portKeys.Any(k =>
                    {
                        var key = k.Substring("ports.".Length).ToLowerInvariant();
                        return key == "http" || key == "https";
                    });
                    if (webServerModule && httpPortChanged)
                    {
                        try
                        {
                            var generateVhost = module.GetType().GetMethod("GenerateVhostAsync");
                            if (generateVhost is not null)
                            {
                                var siteRegistry = sp.GetService<NKS.WebDevConsole.Core.Interfaces.ISiteRegistry>();
                                if (siteRegistry is not null)
                                {
                                    int regenerated = 0;
                                    foreach (var (_, siteCfg) in siteRegistry.Sites)
                                    {
                                        try
                                        {
                                            // Signature is GenerateVhostAsync(SiteConfig site, CancellationToken ct = default).
                                            // Pass the optional CT explicitly so reflection doesn't trip on default-arg metadata.
                                            var task = generateVhost.Invoke(module,
                                                new object[] { siteCfg, ctx.RequestAborted }) as Task;
                                            if (task is not null) await task;
                                            regenerated++;
                                        }
                                        catch (Exception perSiteEx)
                                        {
                                            // Per-site failure (template missing for one site, e.g.) must not abort
                                            // the whole regenerate sweep. Other sites still get fresh vhosts.
                                            var logger = sp.GetRequiredService<ILogger<Program>>();
                                            logger.LogWarning(perSiteEx,
                                                "Port-change vhost regen for {Domain} failed (continuing with rest)",
                                                siteCfg.Domain);
                                        }
                                    }
                                    var rlog = sp.GetRequiredService<ILogger<Program>>();
                                    rlog.LogInformation(
                                        "Port-change vhost bulk-regen completed: {Count} site(s) refreshed for {Service}",
                                        regenerated, module.ServiceId);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            var logger = sp.GetRequiredService<ILogger<Program>>();
                            logger.LogWarning(ex,
                                "Port-change bulk vhost regen for {Service} failed", module.ServiceId);
                        }
                    }

                    var reload = module.GetType().GetMethod("ReloadAsync", new[] { typeof(CancellationToken) });
                    if (reload is null) continue;
                    try
                    {
                        var result = reload.Invoke(module, new object[] { ctx.RequestAborted });
                        if (result is Task t) await t;
                    }
                    catch (Exception ex)
                    {
                        // A plugin's reload fault must not fail the PUT — the
                        // settings row is already persisted, the next daemon
                        // restart will pick it up regardless.
                        var logger = sp.GetRequiredService<ILogger<Program>>();
                        logger.LogWarning(ex, "Port-change reload for {Service} failed", module.ServiceId);
                    }
                }
            }

            return Results.Ok(settings);
        });
    }
}
