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

namespace NKS.WebDevConsole.Daemon.Plugin;

/// <summary>
/// Plugin listing, marketplace, UI schema, install/uninstall/restore and icon routes.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class PluginEndpoints
{
    public static void MapPluginEndpoints(this WebApplication app, PluginLoader pluginLoader, string pluginDir)
    {
        app.MapGet("/api/plugins", (IServiceProvider sp, PluginState pluginState) =>
        {
            var modules = sp.GetServices<IServiceModule>();
            // F87: pre-compute the set of enabled plugin IDs once so every row
            // can cheaply check its dependencies against the live state.
            var enabledIds = new HashSet<string>(
                pluginLoader.Plugins
                    .Where(pl => pluginState.IsEnabled(pl.Instance.Id))
                    .Select(pl => pl.Instance.Id),
                StringComparer.OrdinalIgnoreCase);

            // F91.6: extract plugin-contributed UI fragments (<PluginSlot> inputs).
            // Same cross-ALC reflection pattern as everything else in this file.
            static object[] ExtractContributions(object instance)
            {
                try
                {
                    var uiMethod = instance.GetType().GetMethod("GetUiDefinition");
                    var def = uiMethod?.Invoke(instance, null);
                    if (def is null) return Array.Empty<object>();
                    if (def.GetType().GetProperty("Contributions")?.GetValue(def)
                        is not System.Collections.IEnumerable contribEnum) return Array.Empty<object>();
                    var list = new List<object>();
                    foreach (var c in contribEnum)
                    {
                        if (c is null) continue;
                        var t = c.GetType();
                        // Props is a Dictionary<string,object> across the ALC boundary —
                        // flatten to a plain dict so System.Text.Json emits it as a JSON
                        // object instead of opaque type metadata.
                        var propsRaw = t.GetProperty("Props")?.GetValue(c);
                        var propsDict = new Dictionary<string, object?>();
                        if (propsRaw is System.Collections.IDictionary d)
                        {
                            foreach (System.Collections.DictionaryEntry e in d)
                                propsDict[e.Key?.ToString() ?? ""] = e.Value;
                        }
                        list.Add(new
                        {
                            slot = t.GetProperty("Slot")?.GetValue(c)?.ToString() ?? "",
                            componentType = t.GetProperty("ComponentType")?.GetValue(c)?.ToString() ?? "",
                            props = propsDict,
                            order = t.GetProperty("Order")?.GetValue(c) is int o ? o : 100,
                        });
                    }
                    return list.ToArray();
                }
                catch { return Array.Empty<object>(); }
            }

            // F91.2: extract the generic UI surfaces the plugin declares via
            // UiSchemaBuilder (nav:{route}, site-tab:{id}, dashboard-card:{id}, …)
            // using the same cross-ALC reflection idiom as /api/plugins/ui.
            // Returned for EVERY plugin (enabled or not) so the frontend can do
            // "is surface X owned by any plugin, and is that plugin currently on?"
            static string[] ExtractUiSurfaces(object instance)
            {
                try
                {
                    var uiMethod = instance.GetType().GetMethod("GetUiDefinition");
                    var def = uiMethod?.Invoke(instance, null);
                    if (def is null) return Array.Empty<string>();
                    var surfaces = new List<string>();
                    if (def.GetType().GetProperty("UiSurfaces")?.GetValue(def)
                        is System.Collections.IEnumerable sEnum)
                    {
                        foreach (var s in sEnum)
                        {
                            if (s is null) continue;
                            var key = s.ToString();
                            if (!string.IsNullOrWhiteSpace(key)) surfaces.Add(key);
                        }
                    }
                    // Fallback / compat: if UiSurfaces was left null but NavEntries
                    // exist, still project "nav:{Route}" so older plugins keep
                    // working without touching their code.
                    if (surfaces.Count == 0 &&
                        def.GetType().GetProperty("NavEntries")?.GetValue(def)
                            is System.Collections.IEnumerable navEnum)
                    {
                        foreach (var n in navEnum)
                        {
                            if (n is null) continue;
                            var route = n.GetType().GetProperty("Route")?.GetValue(n)?.ToString();
                            if (!string.IsNullOrWhiteSpace(route)) surfaces.Add($"nav:{route}");
                        }
                    }
                    return surfaces.ToArray();
                }
                catch { return Array.Empty<string>(); }
            }

            return Results.Ok(pluginLoader.Plugins.Select(p =>
            {
                // Resolve service module by exact ServiceId match on the last
                // Id token (nks.wdc.apache → apache). Substring match was broken:
                // "".Contains("") is always true, so a plugin with an empty
                // last-token would falsely map to the first module in the list;
                // and "mailpit".Contains("mail") would cross-map nks.wdc.mail
                // (hypothetical) to the Mailpit module.
                var svcId = p.Instance.Id.Split('.').LastOrDefault() ?? "";
                var serviceModule = string.IsNullOrEmpty(svcId)
                    ? null
                    : modules.FirstOrDefault(m => m.ServiceId.Equals(svcId, StringComparison.OrdinalIgnoreCase));
                var hasService = serviceModule is not null;
                var resolvedServiceId = serviceModule?.ServiceId;

                // F87: evaluate dependency graph against currently-enabled plugins.
                // Returns empty when no deps declared or all satisfied. Frontend
                // can render each diagnostic as a warning banner on the plugin
                // card so the user knows why a plugin may not start.
                var depDiagnostics = PluginLoaderInternals.ValidateDependencies(
                    p.Manifest?.Dependencies, enabledIds);

                // Resolve description from three ordered sources so every plugin
                // surfaces something meaningful in the Plugins settings page:
                //   1. Plugin-overridden IWdcPlugin.Description property (code-owned)
                //   2. plugin.json `description` field (metadata-owned, preferred
                //      because users can edit it without a rebuild)
                //   3. Fallback to the display name so the card never shows "null"
                var codeDescription = p.Instance.Description;
                var manifestDescription = p.Manifest?.Description;
                var description = !string.IsNullOrWhiteSpace(codeDescription)
                    ? codeDescription
                    : !string.IsNullOrWhiteSpace(manifestDescription)
                        ? manifestDescription
                        : $"{p.Instance.DisplayName} plugin";

                return new
                {
                    id = p.Instance.Id,
                    name = p.Instance.DisplayName,
                    version = p.Instance.Version,
                    type = hasService ? "service" : "tool",
                    serviceId = resolvedServiceId,
                    enabled = pluginState.IsEnabled(p.Instance.Id),
                    description,
                    author = p.Manifest?.Author ?? "NKS",
                    license = p.Manifest?.License ?? "MIT",
                    capabilities = p.Manifest?.Capabilities ?? Array.Empty<string>(),
                    supportedPlatforms = p.Manifest?.SupportedPlatforms ?? Array.Empty<string>(),
                    // F87: dependency graph exposed to UI. dependencies mirrors
                    // the manifest verbatim; missingDependencies is the live
                    // diagnostic against enabledIds (empty = OK).
                    dependencies = p.Manifest?.Dependencies,
                    missingDependencies = depDiagnostics,
                    // F91.2: ALL UI surfaces owned by this plugin (nav entries,
                    // site-edit tabs, dashboard cards, …). Always present even when
                    // the plugin is disabled so the frontend can decide "this
                    // surface is plugin-owned by X, is X currently on?" without any
                    // hardcoded per-surface table.
                    uiSurfaces = ExtractUiSurfaces(p.Instance),
                    // F91.6: dynamic UI contributions rendered by <PluginSlot>.
                    // Enabled plugins' contributions drive the actual render; for
                    // disabled plugins the list is still returned so the shell can
                    // decide "this slot has potential content, it's just off now".
                    contributions = ExtractContributions(p.Instance),
                    // Task 25b: optional UMD/ESM bundle URL for custom plugin pages.
                    // Null when the plugin ships no custom UI.
                    pageBundleUrl = p.Manifest?.PageBundleUrl,
                    // Task 25b: static port defaults from plugin.json (secondary data
                    // path — runtime DI IPortMetadata registrations are primary).
                    manifestPorts = p.Manifest?.Ports?.Select(mp => new
                    {
                        key = mp.Key,
                        label = mp.Label,
                        defaultPort = mp.Default,
                    }) ?? [],
                };
            }));
        });

        // Plugin marketplace — Phase 5 plan item.
        // Fetches a JSON manifest from a configurable URL (NKS_WDC_MARKETPLACE_URL env or default)
        // and returns the list of available plugins. Cross-references installed plugin ids so the
        // UI can mark entries as "installed" / "update available". Graceful fallback to an empty
        // list if the remote manifest is unreachable — the feature is best-effort, not critical path.
        // Built-in fallback catalogue — used when no external marketplace is reachable.
        // Every entry is a first-party plugin shipped with the NKS WDC source tree, so
        // the list is always meaningful even on a fresh install with no network. When
        // a real marketplace server comes online it takes precedence.
        static IEnumerable<object> BuiltInMarketplaceCatalogue(HashSet<string> installedIds)
        {
            var entries = new (string id, string name, string version, string description, string category)[]
            {
                ("nks.wdc.apache",  "Apache HTTP Server",  "1.0.0", "Bundled httpd with Scriban-generated vhosts, SSL via mkcert, per-site PHP FastCGI.", "Web Servers"),
                ("nks.wdc.caddy",   "Caddy",               "1.0.0", "Modern HTTP/2 + automatic HTTPS alternative to Apache, drives sites via Caddyfile fragments.", "Web Servers"),
                ("nks.wdc.nginx",   "Nginx",               "1.0.0", "High-performance HTTP server and reverse proxy — Scriban server-block templates, reload via SIGHUP, shares mkcert for TLS.", "Web Servers"),
                ("nks.wdc.php",     "PHP (Multi-version)", "1.0.0", "Multi-version PHP manager with per-version php.ini + extensions + CLI alias shims.", "Runtimes"),
                ("nks.wdc.node",    "Node.js",             "1.0.0", "Multi-version Node.js manager — detected binaries under ~/.wdc/binaries/node/ with shims for npm/npx and active-version switching.", "Runtimes"),
                ("nks.wdc.mysql",   "MySQL",               "1.0.0", "Managed MySQL server with DPAPI-protected root password, my.ini templates, database tooling.", "Databases"),
                ("nks.wdc.mariadb", "MariaDB",             "1.0.0", "Drop-in MySQL-compatible RDBMS — bundled mariadbd with DPAPI-protected root password, my.cnf templates. Alternative to MySQL for new installs.", "Databases"),
                ("nks.wdc.redis",   "Redis",               "1.0.0", "Redis cache server with managed redis.conf, graceful shutdown via redis-cli SHUTDOWN.", "Caches"),
                ("nks.wdc.mailpit", "Mailpit",             "1.0.0", "Local SMTP sink with web UI for development email testing.", "Mail"),
                ("nks.wdc.ssl",     "SSL (mkcert)",        "1.0.0", "Per-site TLS certificates via mkcert, local root CA install.", "Security"),
                ("nks.wdc.cloudflare", "Cloudflare (DNS + Tunnels)", "1.0.0", "Cloudflare DNS + Tunnel API integration — provisioned via account token, not bound to a binary service.", "Networking"),
                ("nks.wdc.hosts",   "Hosts Manager",       "1.0.0", "Windows hosts file manager with 5-backup rotation and managed block delimiters.", "System"),
                ("nks.wdc.composer", "Composer",           "1.0.0", "PHP dependency manager — per-site composer.json/lock management, framework auto-install, runs under active PHP version.", "Runtimes"),
            };
            foreach (var e in entries)
            {
                yield return new
                {
                    id = e.id,
                    name = e.name,
                    version = e.version,
                    description = e.description,
                    downloadUrl = (string?)null,
                    author = "NKS",
                    license = "MIT",
                    category = e.category,
                    installed = installedIds.Contains(e.id),
                    builtIn = true,
                };
            }
        }

        app.MapGet("/api/plugins/marketplace", async (IHttpClientFactory httpFactory) =>
        {
            var marketplaceUrl = Environment.GetEnvironmentVariable("NKS_WDC_MARKETPLACE_URL")
                ?? "https://wdc.nks-hub.cz/plugins.json";

            var installedIds = new HashSet<string>(
                pluginLoader.Plugins.Select(p => p.Instance.Id),
                StringComparer.OrdinalIgnoreCase);

            try
            {
                using var client = httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                using var response = await client.GetAsync(marketplaceUrl);
                if (!response.IsSuccessStatusCode)
                {
                    var fallback = BuiltInMarketplaceCatalogue(installedIds).ToList();
                    return Results.Ok(new
                    {
                        source = "built-in",
                        reachable = true,
                        plugins = fallback,
                        count = fallback.Count,
                        error = $"Remote marketplace unreachable ({(int)response.StatusCode}) — showing built-in catalogue"
                    });
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var entries = new List<object>();
                if (doc.RootElement.TryGetProperty("plugins", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? id : id;
                        var version = item.TryGetProperty("version", out var vEl) ? vEl.GetString() ?? "" : "";
                        var description = item.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
                        var downloadUrl = item.TryGetProperty("downloadUrl", out var duEl) ? duEl.GetString() ?? "" : "";
                        var author = item.TryGetProperty("author", out var aEl) ? aEl.GetString() ?? "" : "";
                        var license = item.TryGetProperty("license", out var lEl) ? lEl.GetString() ?? "" : "";
                        var sha256 = item.TryGetProperty("sha256", out var shEl) ? shEl.GetString() : null;
                        entries.Add(new
                        {
                            id,
                            name,
                            version,
                            description,
                            downloadUrl,
                            sha256,
                            author,
                            license,
                            installed = installedIds.Contains(id),
                        });
                    }
                }

                return Results.Ok(new
                {
                    source = marketplaceUrl,
                    reachable = true,
                    plugins = entries,
                    count = entries.Count
                });
            }
            catch (Exception ex)
            {
                // Network/DNS/timeout — fall back to the built-in catalogue so the
                // user always has something to install from the Marketplace tab on a
                // fresh install with no network. The error field is surfaced by
                // PluginManager.vue as a warning banner.
                var fallback = BuiltInMarketplaceCatalogue(installedIds).ToList();
                return Results.Ok(new
                {
                    source = "built-in",
                    reachable = true,
                    plugins = fallback,
                    count = fallback.Count,
                    error = $"Remote marketplace unreachable ({ex.Message}) — showing built-in catalogue"
                });
            }
        });

        // GET /api/plugins/ports — returns all IPortMetadata registrations from every loaded plugin.
        // Plugins that do not register any IPortMetadata simply contribute nothing to the list.
        // Returns an empty array [] when no plugin has opted in (safe for frontend to poll).
        app.MapGet("/api/plugins/ports", (IServiceProvider sp) =>
        {
            var ports = sp.GetServices<NKS.WebDevConsole.Core.Interfaces.IPortMetadata>();
            var dtos = ports.Select(p => new
            {
                key = p.Key,
                label = p.Label,
                pluginId = p.PluginId,
                defaultPort = p.DefaultPort,
                currentPort = p.CurrentPort,
                isActive = p.IsActive,
            });
            return Results.Ok(dtos);
        });

        app.MapGet("/api/plugins/{id}/ui", (string id, IServiceProvider sp) =>
        {
            var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
            if (plugin == null) return Results.NotFound();

            try
            {
                // Check if the plugin implements IFrontendPanelProvider via reflection (cross-ALC).
                // Must project panels by reflection too because PanelDef lives in the Core assembly
                // loaded under the plugin's ALC, not the daemon's default ALC — direct cast fails.
                var uiMethod = plugin.Instance.GetType().GetMethod("GetUiDefinition");
                if (uiMethod != null)
                {
                    var def = uiMethod.Invoke(plugin.Instance, null);
                    if (def != null)
                    {
                        var pidProp = def.GetType().GetProperty("PluginId");
                        var catProp = def.GetType().GetProperty("Category");
                        var iconProp = def.GetType().GetProperty("Icon");
                        var panelsProp = def.GetType().GetProperty("Panels");

                        var panelsOut = new List<object>();
                        if (panelsProp?.GetValue(def) is System.Collections.IEnumerable panelsEnum)
                        {
                            foreach (var p in panelsEnum)
                            {
                                if (p is null) continue;
                                var t = p.GetType();
                                var typeStr = t.GetProperty("Type")?.GetValue(p)?.ToString() ?? "";
                                var propsRaw = t.GetProperty("Props")?.GetValue(p);
                                // PanelDef.Props is IDictionary<string,object> — flatten to a plain
                                // dictionary so System.Text.Json serialises it as a JSON object.
                                var propsDict = new Dictionary<string, object?>();
                                if (propsRaw is System.Collections.IDictionary dict)
                                {
                                    foreach (System.Collections.DictionaryEntry entry in dict)
                                    {
                                        propsDict[entry.Key?.ToString() ?? ""] = entry.Value;
                                    }
                                }
                                panelsOut.Add(new { type = typeStr, props = propsDict });
                            }
                        }

                        // F91: project NavEntries (nullable array of NavContribution) across
                        // the plugin ALC boundary using the same reflection idiom as panels.
                        var navProp = def.GetType().GetProperty("NavEntries");
                        var navOut = new List<object>();
                        if (navProp?.GetValue(def) is System.Collections.IEnumerable navEnum)
                        {
                            foreach (var n in navEnum)
                            {
                                if (n is null) continue;
                                var t = n.GetType();
                                navOut.Add(new
                                {
                                    id = t.GetProperty("Id")?.GetValue(n)?.ToString() ?? "",
                                    label = t.GetProperty("Label")?.GetValue(n)?.ToString() ?? "",
                                    icon = t.GetProperty("Icon")?.GetValue(n)?.ToString() ?? "",
                                    route = t.GetProperty("Route")?.GetValue(n)?.ToString() ?? "",
                                    order = t.GetProperty("Order")?.GetValue(n) is int o ? o : 100
                                });
                            }
                        }

                        // If the plugin gave us an empty panel list, fall through to the default
                        // (it's most likely a plugin scaffolding bug — don't punish the user with
                        // a blank page).
                        if (panelsOut.Count > 0)
                        {
                            return Results.Ok(new
                            {
                                pluginId = pidProp?.GetValue(def)?.ToString() ?? id,
                                category = catProp?.GetValue(def)?.ToString() ?? "Services",
                                icon = iconProp?.GetValue(def)?.ToString() ?? "el-icon-setting",
                                panels = panelsOut,
                                navEntries = navOut
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Cross-ALC reflection on GetUiDefinition failed for plugin {Id} — falling back to default UI", id);
            }

            // Fallback: generic UI schema for plugins that don't provide their own
            return Results.Ok(new
            {
                pluginId = id,
                category = "Services",
                icon = "el-icon-setting",
                panels = new[]
                {
                    new { type = "service-status-card", props = (object)new { serviceId = id } },
                    new { type = "log-viewer", props = (object)new { serviceId = id } }
                },
                navEntries = Array.Empty<object>()
            });
        });

        // F91 aggregator — single round-trip for the sidebar. Returns nav entries
        // from every currently-enabled plugin, flattened + sorted by (category,
        // order, label). Disabled plugins contribute nothing. Replaces the need
        // for the frontend to fetch /api/plugins/{id}/ui N times when building
        // the sidebar.
        app.MapGet("/api/plugins/ui", (IServiceProvider sp, PluginState pluginState) =>
        {
            var result = new List<object>();
            foreach (var plugin in pluginLoader.Plugins)
            {
                if (!pluginState.IsEnabled(plugin.Instance.Id)) continue;
                try
                {
                    var uiMethod = plugin.Instance.GetType().GetMethod("GetUiDefinition");
                    var def = uiMethod?.Invoke(plugin.Instance, null);
                    if (def is null) continue;

                    var pluginId = def.GetType().GetProperty("PluginId")?.GetValue(def)?.ToString()
                        ?? plugin.Instance.Id;
                    var category = def.GetType().GetProperty("Category")?.GetValue(def)?.ToString()
                        ?? "Services";
                    var fallbackIcon = def.GetType().GetProperty("Icon")?.GetValue(def)?.ToString()
                        ?? "el-icon-setting";

                    if (def.GetType().GetProperty("NavEntries")?.GetValue(def)
                        is System.Collections.IEnumerable navEnum)
                    {
                        foreach (var n in navEnum)
                        {
                            if (n is null) continue;
                            var t = n.GetType();
                            result.Add(new
                            {
                                pluginId,
                                category,
                                id = t.GetProperty("Id")?.GetValue(n)?.ToString() ?? "",
                                label = t.GetProperty("Label")?.GetValue(n)?.ToString() ?? "",
                                icon = (t.GetProperty("Icon")?.GetValue(n)?.ToString() is { Length: > 0 } ic)
                                    ? ic : fallbackIcon,
                                route = t.GetProperty("Route")?.GetValue(n)?.ToString() ?? "",
                                order = t.GetProperty("Order")?.GetValue(n) is int o ? o : 100
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    app.Logger.LogWarning(ex, "F91 nav aggregation failed for plugin {Id}", plugin.Instance.Id);
                }
            }
            return Results.Ok(new { entries = result });
        });

        // Plugin install — Phase 7. Downloads a plugin .zip from a marketplace URL, validates,
        // extracts into the plugins directory. Daemon restart required to load the new DLL
        // because AssemblyLoadContext does not unload while assemblies are referenced.
        // Returns { installed: true, restartRequired: true, path: "..." }.
        // MCP intent-gated under kind=plugin_install. Installing a new plugin
        // loads its DLL via PluginLoadContext on next daemon boot — arbitrary
        // code execution if the downloadUrl is operator-untrusted. Header-
        // driven gate so the GUI marketplace install button stays untouched.
        app.MapPost("/api/plugins/install", async (
            HttpContext ctx,
            IHttpClientFactory httpFactory,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            CancellationToken ct) =>
        {
            Dictionary<string, string>? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ct);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
            }
            var downloadUrl = body?.GetValueOrDefault("downloadUrl") ?? "";
            var pluginId = body?.GetValueOrDefault("id") ?? "";
            var expectedSha256 = (body?.GetValueOrDefault("sha256") ?? "").Trim();

            if (string.IsNullOrWhiteSpace(downloadUrl))
                return Results.BadRequest(new { error = "downloadUrl required" });
            if (string.IsNullOrWhiteSpace(pluginId))
                return Results.BadRequest(new { error = "id required" });

            var piIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(piIntentToken) || NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                var piAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var piVerdict = await intentValidator.ValidateAndConsumeAsync(
                    piIntentToken ?? "", "plugin_install", domain: pluginId, host: "*plugin*", piAllowUnconfirmed, ct);
                if (!piVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = piVerdict.Reason, detail = piVerdict.Detail },
                        statusCode: piVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            // Validate plugin id — only [a-z0-9.-]+ to prevent directory traversal in extract path
            if (!System.Text.RegularExpressions.Regex.IsMatch(pluginId, @"^[a-zA-Z0-9._\-]{1,128}$"))
                return Results.BadRequest(new { error = "Invalid plugin id" });

            // Validate download URL — must be HTTPS (or HTTP for localhost dev)
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
                return Results.BadRequest(new { error = "Invalid downloadUrl" });
            var schemeOk = uri.Scheme == "https" ||
                           (uri.Scheme == "http" && (uri.Host == "localhost" || uri.Host == "127.0.0.1"));
            if (!schemeOk)
                return Results.BadRequest(new { error = "downloadUrl must be HTTPS (or HTTP localhost)" });

            // A plugin DLL runs inside the daemon (elevated on Windows), so an archive
            // from an arbitrary URL must match a SHA-256 the caller got from a trusted
            // catalogue entry. Loopback URLs stay hash-optional for local plugin dev.
            var isLoopbackUrl = uri.IsLoopback;
            if (expectedSha256.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(expectedSha256, "^[0-9a-fA-F]{64}$"))
                return Results.BadRequest(new { error = "sha256 must be 64 hex characters" });
            if (expectedSha256.Length == 0 && !isLoopbackUrl)
                return Results.BadRequest(new { error = "sha256 is required for plugin downloads" });

            var pluginsRoot = Path.GetFullPath(pluginDir);
            var targetDir = Path.GetFullPath(Path.Combine(pluginsRoot, pluginId));
            if (!NKS.WebDevConsole.Daemon.Config.PathContainment.IsUnder(pluginsRoot, targetDir))
                return Results.BadRequest(new { error = "Resolved path escapes plugins root" });

            var cacheDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.CacheRoot, "plugin-installs");
            Directory.CreateDirectory(cacheDir);
            var tempZip = Path.Combine(cacheDir, $"{pluginId}-{Guid.NewGuid():N}.zip");

            try
            {
                using (var client = httpFactory.CreateClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(2);
                    using var stream = await client.GetStreamAsync(uri);
                    using var fs = File.Create(tempZip);
                    await stream.CopyToAsync(fs);
                }

                if (expectedSha256.Length > 0)
                {
                    string actualSha256;
                    using (var verify = File.OpenRead(tempZip))
                        actualSha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(verify, ct));
                    if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tempZip); } catch { /* best effort */ }
                        return Results.BadRequest(new { error = $"SHA-256 mismatch for plugin {pluginId}: expected {expectedSha256}, got {actualSha256}" });
                    }
                }

                // Extract to staging dir first, then atomic rename onto target
                var stagingDir = targetDir + ".staging";
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
                Directory.CreateDirectory(stagingDir);

                using (var fs = File.OpenRead(tempZip))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        if (entry.FullName.Contains("..")) continue; // zip-slip defense
                        var dest = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!PathContainment.IsUnder(stagingDir, dest))
                            continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        using var entryStream = entry.Open();
                        using var outFs = File.Create(dest);
                        await entryStream.CopyToAsync(outFs);
                    }
                }

                // Sanity check: at least one .dll matching the plugin id
                var dllExists = Directory.EnumerateFiles(stagingDir, $"*{pluginId}*.dll", SearchOption.AllDirectories).Any()
                             || Directory.EnumerateFiles(stagingDir, "*.dll", SearchOption.AllDirectories).Any();
                if (!dllExists)
                {
                    Directory.Delete(stagingDir, recursive: true);
                    return Results.BadRequest(new { error = "Archive contains no DLL" });
                }

                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
                Directory.Move(stagingDir, targetDir);

                return Results.Ok(new
                {
                    installed = true,
                    id = pluginId,
                    path = targetDir,
                    restartRequired = true,
                    message = "Plugin extracted. Restart the daemon to load it."
                });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Install failed: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        });

        // F91.4: uninstall a plugin. Must be disabled first (enabled plugins would
        // have their DLL locked by the running ALC). Also refuses when another
        // enabled plugin still depends on this one. Removes the plugin directory
        // on disk — DLL stays loaded in the current process (AssemblyLoadContext
        // doesn't unload while referenced), so the response flags restartRequired
        // and the UI surfaces that note so users know to restart.
        // MCP intent-gated under kind=plugin_uninstall. Uninstalling a plugin
        // removes a capability (deploy, SSL, MariaDB, …) from the daemon and
        // can break running services. Header-driven gate; GUI uninstall button
        // keeps working without a token.
        app.MapDelete("/api/plugins/{id}", async (
            string id,
            PluginState pluginState,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            // Path safety: same [a-z0-9.-]+ rule as install so the computed plugin
            // directory can't escape the plugins root.
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9._\-]{1,128}$"))
                return Results.BadRequest(new { error = "Invalid plugin id" });

            var puIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(puIntentToken) || NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                var puAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var puVerdict = await intentValidator.ValidateAndConsumeAsync(
                    puIntentToken ?? "", "plugin_uninstall", domain: id, host: "*plugin*", puAllowUnconfirmed, ct);
                if (!puVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = puVerdict.Reason, detail = puVerdict.Detail },
                        statusCode: puVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
            if (plugin == null) return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });

            if (pluginState.IsEnabled(id))
            {
                return Results.BadRequest(new
                {
                    error = "plugin-is-enabled",
                    id,
                    message = "Disable the plugin before uninstalling.",
                });
            }

            // Refuse if any enabled plugin would lose a dependency. Uses the same
            // traversal as /disable so the UX is consistent.
            var dependents = new List<string>();
            foreach (var other in pluginLoader.Plugins)
            {
                if (other.Instance.Id == id) continue;
                if (!pluginState.IsEnabled(other.Instance.Id)) continue;
                var deps = other.Manifest?.Dependencies;
                if (deps is null) continue;
                var hardHit = deps.Hard?.Any(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase)) == true;
                if (hardHit) dependents.Add(other.Instance.Id);
            }
            if (dependents.Count > 0)
            {
                return Results.BadRequest(new { error = "has-dependents", id, dependents });
            }

            // F91.7: collect all files belonging to this plugin. Two layouts are
            // supported side-by-side:
            //   A) per-plugin subfolder (what /install writes):
            //        {pluginDir}/{id}/*
            //   B) flat build output (what the solution emits + user typically ships):
            //        {pluginDir}/{AssemblyName}.dll + .pdb + .deps.json + .runtimeconfig.json
            // Layout B is detected via the already-loaded Assembly.Location, which
            // is the authoritative source of where the DLL came from, and we
            // gather its sibling artifacts by stripping extensions.
            var targetsToDelete = new List<string>();
            string? targetDesc = null;
            try
            {
                var pluginsRoot = Path.GetFullPath(pluginDir);

                // A) dedicated folder
                var candidateDir = Path.GetFullPath(Path.Combine(pluginsRoot, id));
                if (NKS.WebDevConsole.Daemon.Config.PathContainment.IsUnder(pluginsRoot, candidateDir)
                    && Directory.Exists(candidateDir))
                {
                    targetsToDelete.AddRange(Directory.EnumerateFiles(candidateDir, "*", SearchOption.AllDirectories));
                    targetDesc = candidateDir;
                }

                // A.bis) plugin.json-based folder lookup
                if (targetsToDelete.Count == 0)
                {
                    foreach (var dir in Directory.EnumerateDirectories(pluginsRoot))
                    {
                        var manifest = Path.Combine(dir, "plugin.json");
                        if (!File.Exists(manifest)) continue;
                        try
                        {
                            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                            if (doc.RootElement.TryGetProperty("id", out var idProp)
                                && string.Equals(idProp.GetString(), id, StringComparison.OrdinalIgnoreCase))
                            {
                                targetsToDelete.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
                                targetDesc = dir;
                                break;
                            }
                        }
                        catch { /* malformed manifest — skip */ }
                    }
                }

                // B) flat layout — use the loaded assembly's location to find DLL
                // + siblings (pdb, deps.json, runtimeconfig.json, xml doc). Guard
                // against symlinks / location outside pluginsRoot so we never delete
                // unrelated files sitting somewhere else on disk.
                if (targetsToDelete.Count == 0)
                {
                    try
                    {
                        var asmPath = plugin.Assembly.Location;
                        if (!string.IsNullOrEmpty(asmPath) && File.Exists(asmPath))
                        {
                            var asmFull = Path.GetFullPath(asmPath);
                            if (NKS.WebDevConsole.Daemon.Config.PathContainment.IsUnder(pluginsRoot, asmFull))
                            {
                                var baseName = Path.GetFileNameWithoutExtension(asmPath);
                                var dir = Path.GetDirectoryName(asmFull)!;
                                foreach (var f in Directory.EnumerateFiles(dir, baseName + ".*"))
                                    targetsToDelete.Add(f);
                                targetDesc = asmFull;
                            }
                        }
                    }
                    catch { /* some assemblies lack Location (in-memory loads) */ }
                }
            }
            catch (Exception ex)
            {
                return Results.Problem($"Uninstall lookup failed: {ex.Message}");
            }

            if (targetsToDelete.Count == 0)
            {
                return Results.Ok(new
                {
                    uninstalled = false,
                    id,
                    restartRequired = true,
                    message = "Plugin files not found on disk; DLL stays loaded until daemon restart.",
                });
            }

            int deleted = 0, locked = 0;
            foreach (var file in targetsToDelete)
            {
                try { File.Delete(file); deleted++; }
                catch { locked++; }
            }
            // Try removing an empty folder if we were operating on layout A
            if (targetDesc is not null && Directory.Exists(targetDesc))
            {
                try { Directory.Delete(targetDesc, recursive: true); } catch { /* leftover locked files */ }
            }

            // F91.9: record the uninstall on disk so the next daemon boot skips
            // loading this plugin AND purges any leftover locked files that we
            // couldn't delete here. Without this the plugin gets reloaded on
            // restart because the DLL survived on disk, and it reappears in the
            // UI as if nothing happened.
            pluginState.MarkUninstalled(id);

            return Results.Ok(new
            {
                uninstalled = true,
                id,
                path = targetDesc,
                deletedFiles = deleted,
                lockedFiles = locked,
                restartRequired = locked > 0 || plugin.Assembly.Location is { Length: > 0 },
                message = locked == 0
                    ? $"Plugin files removed ({deleted} file(s))."
                    : $"Removed {deleted} file(s); {locked} were locked by the running daemon — restart to fully unload.",
            });
        });

        // F91.12: restore a previously uninstalled built-in plugin. Removes the
        // id from the uninstall blacklist so the next daemon boot loads it —
        // but only IF the DLL is still on disk. For built-in plugins that were
        // purged (locked DLL deleted on restart), the user has to rebuild the
        // solution first (dev) or reinstall from catalog (prod). We detect the
        // situation and tell the UI instead of silently failing.
        app.MapPost("/api/plugins/restore/{id}", (string id, PluginState pluginState) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9._\-]{1,128}$"))
                return Results.BadRequest(new { error = "Invalid plugin id" });

            // Check whether any DLL still matches the plugin id (either via
            // plugin.json in a subfolder or NKS.WebDevConsole.Plugin.*.dll flat).
            bool dllPresent = false;
            try
            {
                var pluginsRoot = Path.GetFullPath(pluginDir);
                if (Directory.Exists(pluginsRoot))
                {
                    // Subfolder layout
                    var idDir = Path.Combine(pluginsRoot, id);
                    if (Directory.Exists(idDir) &&
                        Directory.EnumerateFiles(idDir, "*.dll", SearchOption.AllDirectories).Any())
                    {
                        dllPresent = true;
                    }
                    // Flat layout — match by assembly name suffix (e.g. NKS.WebDevConsole.Plugin.Caddy.dll)
                    if (!dllPresent)
                    {
                        var shortName = id.Split('.').LastOrDefault() ?? id;
                        foreach (var dll in Directory.EnumerateFiles(pluginsRoot, "NKS.WebDevConsole.Plugin.*.dll"))
                        {
                            if (Path.GetFileNameWithoutExtension(dll).EndsWith("." + shortName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                dllPresent = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch { /* any IO glitch — fall through to "needs rebuild" branch */ }

            pluginState.ClearUninstalled(id);

            if (!dllPresent)
            {
                return Results.Ok(new
                {
                    restored = false,
                    id,
                    restartRequired = false,
                    rebuildRequired = true,
                    message = "Plugin DLL není na disku. V dev módu spusť `dotnet build`, "
                            + "pak klikni Obnovit znovu. V produkci stáhni plugin z katalogu.",
                });
            }

            // Fire restart so the loader re-scans with the cleared blacklist.
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                try
                {
                    var pf = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
                    if (File.Exists(pf)) File.Delete(pf);
                }
                catch { }
                Environment.Exit(99);
            });

            return Results.Ok(new
            {
                restored = true,
                id,
                restartRequired = true,
                rebuildRequired = false,
                message = "Plugin obnoven. Restartuji daemon…",
            });
        });

        // Plugin brand icon: streams embedded SVG resource from plugin DLL
        app.MapGet("/api/plugins/{id}/icon", (string id) =>
        {
            // Accept either full plugin id (nks.wdc.apache) or short service id (apache)
            var plugin = pluginLoader.Plugins.FirstOrDefault(p =>
                p.Instance.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || p.Instance.Id.EndsWith("." + id, StringComparison.OrdinalIgnoreCase)
                || p.Instance.Id.Split('.').Last().Equals(id, StringComparison.OrdinalIgnoreCase));

            if (plugin is null)
                return Results.NotFound();

            var asm = plugin.Instance.GetType().Assembly;
            // Resource name pattern: {AssemblyName}.Resources.icon.svg
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith(".Resources.icon.svg", StringComparison.OrdinalIgnoreCase)
                                  || r.EndsWith(".icon.svg", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
                return Results.NotFound();

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null)
                return Results.NotFound();

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Results.File(ms.ToArray(), "image/svg+xml");
        });
    }
}
