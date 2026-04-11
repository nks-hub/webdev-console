using System.Text.Json;
using Dapper;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using CliWrap.Buffered;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

// Windows: create the Job Object before anything spawns child processes so every
// subsequent Process.Start() from ProcessManager + plugins gets assigned to it and
// gets killed when the daemon exits (no orphaned httpd/mysqld/php-cgi processes).
NKS.WebDevConsole.Core.Services.DaemonJobObject.EnsureInitialized();

var builder = WebApplication.CreateBuilder(args);

// CORS for Electron renderer
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000", "app://.")
              .AllowAnyMethod().AllowAnyHeader());
});

// OpenAPI metadata so /openapi/v1.json can be consumed by NSwag/swagger-typescript-api
// to generate TS types in CI (prevents contract drift between daemon and frontend).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

builder.Services.AddSingleton<TelemetryConsent>();
builder.Services.AddSingleton<PluginState>();
builder.Services.AddSingleton<PhpExtensionOverrides>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<WindowsFirewallManager>();
builder.Services.AddSingleton<SseService>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<ShutdownCoordinator>();
builder.Services.AddHostedService<HealthMonitor>();
builder.Services.AddSingleton<TemplateEngine>();
builder.Services.AddSingleton<ConfigValidator>();
builder.Services.AddSingleton<AtomicWriter>();
builder.Services.AddSingleton(sp => new ServiceConfigManager(
    sp.GetRequiredService<AtomicWriter>(),
    NKS.WebDevConsole.Core.Services.WdcPaths.Root
));
builder.Services.AddSingleton(sp => new SiteManager(
    sp.GetRequiredService<ILogger<SiteManager>>(),
    sp.GetRequiredService<TemplateEngine>(),
    sp.GetRequiredService<ConfigValidator>(),
    sp.GetRequiredService<AtomicWriter>(),
    NKS.WebDevConsole.Core.Services.WdcPaths.SitesRoot,
    NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot
));
builder.Services.AddSingleton<SiteOrchestrator>();
builder.Services.AddSingleton<MampMigrator>();
builder.Services.AddSingleton<BackupManager>();

// Binary catalog / downloader / manager — own binaries under ~/.wdc/binaries/
builder.Services.AddHttpClient("binary-downloader");
builder.Services.AddHttpClient("catalog-client");
builder.Services.AddSingleton<CatalogClientOptions>(sp =>
{
    // Source precedence: SettingsStore (user-editable via /api/settings
    // and the Settings page in the Electron UI) → NKS_WDC_CATALOG_URL env
    // → localhost:8765 fallback (matches the dev run.cmd in
    // services/catalog-api/).
    var settings = sp.GetRequiredService<SettingsStore>();
    return new CatalogClientOptions { BaseUrl = settings.CatalogUrl };
});
builder.Services.AddSingleton<CatalogClient>();
builder.Services.AddSingleton<BinaryDownloader>();
builder.Services.AddSingleton<BinaryManager>();

// Phase 1: Load plugin assemblies and call Initialize (registers DI services) BEFORE Build
var earlyLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var pluginLoader = new PluginLoader(earlyLoggerFactory.CreateLogger<PluginLoader>());
// Production: plugins/ next to daemon binary. Dev: build/plugins/ at repo root.
var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
if (!Directory.Exists(pluginDir))
{
    var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    pluginDir = Path.Combine(repoRoot, "build", "plugins");
}
pluginLoader.LoadPlugins(pluginDir);

builder.Services.AddSingleton(pluginLoader);

// Call Initialize on each plugin so they can register their services into the DI container.
// Wrap per-plugin in try/catch so one buggy plugin DLL cannot prevent the daemon from starting.
var initContext = PluginContext.ForInitPhase(earlyLoggerFactory);
var initLogger = earlyLoggerFactory.CreateLogger("PluginInit");
var failedInitPlugins = new List<string>();
foreach (var p in pluginLoader.Plugins)
{
    try
    {
        p.Instance.Initialize(builder.Services, initContext);
    }
    catch (Exception ex)
    {
        initLogger.LogError(ex, "Plugin {Id} failed to Initialize — will be skipped", p.Instance.Id);
        failedInitPlugins.Add(p.Instance.Id);
    }
}

// Initialize SQLite database and run migrations
var dbPath = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "state.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var database = new Database(dbPath);
builder.Services.AddSingleton(database);

var migrationRunner = new MigrationRunner(earlyLoggerFactory.CreateLogger<MigrationRunner>());
migrationRunner.Run(database.ConnectionString);

var app = builder.Build();
app.UseCors();

// Sentry crash reporting — opt-in only. The TelemetryConsent singleton is the
// sole gate: if the user hasn't ticked the Settings page toggle, Sentry is
// never initialised and no network calls happen. DSN comes from the env var
// NKS_WDC_SENTRY_DSN; if absent, the feature is inert even with consent so
// self-hosters can point at their own Sentry instance before enabling.
//
// Privacy scrubbing matches the doc comment in TelemetryConsent.cs:
// allowed — .NET version, OS version, daemon version, stack trace
// forbidden — file paths, hostnames, site names, DB contents, passwords
{
    var consent = app.Services.GetRequiredService<TelemetryConsent>();
    var dsn = Environment.GetEnvironmentVariable("NKS_WDC_SENTRY_DSN");
    var sentryLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sentry");
    if (consent.Enabled && consent.CrashReports && !string.IsNullOrWhiteSpace(dsn))
    {
        try
        {
            Sentry.SentrySdk.Init(o =>
            {
                o.Dsn = dsn;
                o.Release = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev";
                o.Environment = "production";
                // Never capture anything we can't prove is safe.
                // SendDefaultPii:false strips IP, username, email, cookies, headers.
                o.SendDefaultPii = false;
                o.AutoSessionTracking = false;
                o.AttachStacktrace = true;
                o.ServerName = ""; // never include the hostname
                // Additional scrubbing: drop the machine server name, any context
                // the SDK auto-populates that could leak local paths, and reset
                // the user object on every event.
                o.SetBeforeSend((sentryEvent, _) =>
                {
                    sentryEvent.ServerName = "";
                    sentryEvent.User = new Sentry.SentryUser();
                    return sentryEvent;
                });
            });
            sentryLog.LogInformation("Sentry crash reporting initialised (consent given)");
        }
        catch (Exception ex)
        {
            sentryLog.LogWarning(ex, "Sentry init failed — crash reporting disabled for this session");
        }
    }
    else if (consent.Enabled && consent.CrashReports && string.IsNullOrWhiteSpace(dsn))
    {
        sentryLog.LogInformation("Sentry consent given but NKS_WDC_SENTRY_DSN not set — no reports will be sent");
    }
}

// Expose OpenAPI spec at /openapi/v1.json — used by CI TS type generation
app.MapOpenApi();

// Refresh binary catalog from the catalog API before plugins start so they can use it
var catalogClient = app.Services.GetRequiredService<CatalogClient>();
await catalogClient.RefreshAsync();

// Phase 2: Start plugins with the fully-built service provider.
// Wrap per-plugin in try/catch so one failing StartAsync cannot break the daemon — other
// plugins and the REST API remain available; the failing service will just report Crashed.
var pluginContext = new PluginContext(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>());

var startLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PluginStart");
foreach (var p in pluginLoader.Plugins)
{
    if (failedInitPlugins.Contains(p.Instance.Id))
    {
        startLogger.LogWarning("Skipping StartAsync for {Id} (Initialize failed)", p.Instance.Id);
        continue;
    }
    try
    {
        await p.Instance.StartAsync(pluginContext, CancellationToken.None);
    }
    catch (Exception ex)
    {
        startLogger.LogError(ex, "Plugin {Id} failed to Start — daemon continues without it", p.Instance.Id);
    }
}

// Auth token generated early so middleware can reference it
var portFile = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
var authToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

// Write port file AFTER server starts listening (avoids race condition)
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Urls.FirstOrDefault() ?? "http://localhost:5000";
    var port = new Uri(address.Replace("+", "localhost")).Port;
    File.WriteAllText(portFile, $"{port}\n{authToken}");
    Console.WriteLine($"[daemon] listening on port {port}, port file: {portFile}");
});

// Health endpoint — no auth required (for monitoring + Electron daemon detection)
app.MapGet("/healthz", () => Results.Ok(new { ok = true, timestamp = DateTime.UtcNow }));
app.MapPost("/api/admin/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(() => lifetime.StopApplication());
    return Results.Accepted();
});

// Auth middleware for /api/* requests.
// SECURITY: use constant-time comparison to prevent timing attacks that could leak the token.
var authTokenBytes = System.Text.Encoding.UTF8.GetBytes(authToken);
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        // Parse Authorization: Bearer <token> — use prefix check instead of Replace() which
        // is fragile (multiple occurrences, whitespace) and doesn't handle case variants.
        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        string? provided = null;
        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            provided = authHeader.Substring(7).Trim();
        }
        provided ??= ctx.Request.Query["token"].FirstOrDefault();

        bool ok = false;
        if (!string.IsNullOrEmpty(provided))
        {
            var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided);
            // FixedTimeEquals requires same length — reject different length fast
            ok = providedBytes.Length == authTokenBytes.Length
                 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, authTokenBytes);
        }

        if (!ok)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Unauthorized");
            return;
        }
    }
    await next();
});

// REST Endpoints
app.MapGet("/api/status", () => Results.Ok(new
{
    status = "running",
    version = "0.1.0",
    plugins = pluginLoader.Plugins.Count,
    uptime = Environment.TickCount64 / 1000
}));

app.MapGet("/api/system", async (IServiceProvider sp, BinaryManager bm, SiteManager sm) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var running = 0; var total = 0;
    foreach (var m in modules) { total++; var s = await m.GetStatusAsync(CancellationToken.None); if (s.State == ServiceState.Running) running++; }
    return Results.Ok(new
    {
        daemon = new { version = "0.1.0", uptime = Environment.TickCount64 / 1000, pid = Environment.ProcessId },
        services = new { running, total },
        sites = sm.Sites.Count,
        plugins = pluginLoader.Plugins.Count,
        binaries = bm.ListInstalled().Count,
        os = new { platform = Environment.OSVersion.Platform.ToString(), version = Environment.OSVersion.VersionString, machine = Environment.MachineName },
        runtime = new { dotnet = Environment.Version.ToString(), arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString() }
    });
});

app.MapGet("/api/plugins", (IServiceProvider sp, PluginState pluginState) =>
{
    var modules = sp.GetServices<IServiceModule>();
    return Results.Ok(pluginLoader.Plugins.Select(p =>
    {
        var svcId = p.Instance.Id.Split('.').LastOrDefault() ?? "";
        var hasService = modules.Any(m => m.ServiceId.Contains(svcId, StringComparison.OrdinalIgnoreCase));

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
            enabled = pluginState.IsEnabled(p.Instance.Id),
            description,
            author = p.Manifest?.Author ?? "NKS",
            license = p.Manifest?.License ?? "MIT",
            capabilities = p.Manifest?.Capabilities ?? Array.Empty<string>(),
            supportedPlatforms = p.Manifest?.SupportedPlatforms ?? Array.Empty<string>(),
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
        ("nks.wdc.php",     "PHP (Multi-version)", "1.0.0", "Multi-version PHP manager with per-version php.ini + extensions + CLI alias shims.", "Runtimes"),
        ("nks.wdc.mysql",   "MySQL",               "1.0.0", "Managed MySQL server with DPAPI-protected root password, my.ini templates, database tooling.", "Databases"),
        ("nks.wdc.redis",   "Redis",               "1.0.0", "Redis cache server with managed redis.conf, graceful shutdown via redis-cli SHUTDOWN.", "Caches"),
        ("nks.wdc.mailpit", "Mailpit",             "1.0.0", "Local SMTP sink with web UI for development email testing.", "Mail"),
        ("nks.wdc.ssl",     "SSL (mkcert)",        "1.0.0", "Per-site TLS certificates via mkcert, local root CA install.", "Security"),
        ("nks.wdc.hosts",   "Hosts Manager",       "1.0.0", "Windows hosts file manager with 5-backup rotation and managed block delimiters.", "System"),
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
        ?? "http://127.0.0.1:8765/plugins.json";

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
                entries.Add(new
                {
                    id,
                    name,
                    version,
                    description,
                    downloadUrl,
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
                        panels = panelsOut
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
        }
    });
});

// Plugin install — Phase 7. Downloads a plugin .zip from a marketplace URL, validates,
// extracts into the plugins directory. Daemon restart required to load the new DLL
// because AssemblyLoadContext does not unload while assemblies are referenced.
// Returns { installed: true, restartRequired: true, path: "..." }.
app.MapPost("/api/plugins/install", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var downloadUrl = body?.GetValueOrDefault("downloadUrl") ?? "";
    var pluginId = body?.GetValueOrDefault("id") ?? "";

    if (string.IsNullOrWhiteSpace(downloadUrl))
        return Results.BadRequest(new { error = "downloadUrl required" });
    if (string.IsNullOrWhiteSpace(pluginId))
        return Results.BadRequest(new { error = "id required" });

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

    var pluginsRoot = Path.GetFullPath(pluginDir);
    var targetDir = Path.GetFullPath(Path.Combine(pluginsRoot, pluginId));
    if (!targetDir.StartsWith(pluginsRoot, StringComparison.OrdinalIgnoreCase))
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
                if (!dest.StartsWith(Path.GetFullPath(stagingDir), StringComparison.OrdinalIgnoreCase))
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

// Service management endpoints — query real plugin modules
app.MapGet("/api/services", async (IServiceProvider sp) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var statuses = new List<object>();
    foreach (var m in modules)
    {
        var status = await m.GetStatusAsync(CancellationToken.None);
        statuses.Add(status);
    }
    return Results.Ok(statuses);
});

app.MapGet("/api/services/{id}", async (string id, IServiceProvider sp) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound();
    var status = await module.GetStatusAsync(CancellationToken.None);
    return Results.Ok(status);
});

app.MapPost("/api/services/{id}/start", async (string id, IServiceProvider sp, ILoggerFactory lf) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

    try
    {
        await module.StartAsync(CancellationToken.None);
        var status = await module.GetStatusAsync(CancellationToken.None);
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        lf.CreateLogger("ServiceControl").LogError(ex, "Failed to start service {Id}", id);
        return Results.Problem(
            title: $"Failed to start {id}",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapPost("/api/services/{id}/stop", async (string id, IServiceProvider sp, ILoggerFactory lf) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

    try
    {
        await module.StopAsync(CancellationToken.None);
        var status = await module.GetStatusAsync(CancellationToken.None);
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        lf.CreateLogger("ServiceControl").LogError(ex, "Failed to stop service {Id}", id);
        return Results.Problem(
            title: $"Failed to stop {id}",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapPost("/api/services/{id}/restart", async (string id, IServiceProvider sp, ILoggerFactory lf) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

    try
    {
        await module.StopAsync(CancellationToken.None);
        await module.StartAsync(CancellationToken.None);
        var status = await module.GetStatusAsync(CancellationToken.None);
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        lf.CreateLogger("ServiceControl").LogError(ex, "Failed to restart service {Id}", id);
        return Results.Problem(
            title: $"Failed to restart {id}",
            detail: ex.Message,
            statusCode: 500);
    }
});

// Sites CRUD
var siteManager = app.Services.GetRequiredService<SiteManager>();
siteManager.LoadAll();

// On startup, re-apply all sites so vhost configs are regenerated against the
// current Apache install. (Vhosts live next to the binary, so a fresh install
// of a different Apache version starts with an empty sites-enabled/.)
var startupOrchestrator = app.Services.GetRequiredService<SiteOrchestrator>();
foreach (var siteToApply in siteManager.Sites.Values)
{
    try { await startupOrchestrator.ApplyAsync(siteToApply); }
    catch (Exception ex)
    {
        Console.WriteLine($"[startup] failed to re-apply site {siteToApply.Domain}: {ex.Message}");
    }
}

// Sweep orphan *.tmp files left over from a previous daemon crash or taskkill
// during an in-progress AtomicWriter.WriteAsync. Only touches files older than
// 1 hour so we don't clobber an in-flight write from a concurrent tool.
try
{
    var sitesRoot = NKS.WebDevConsole.Core.Services.WdcPaths.SitesRoot;
    var generatedRoot = NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot;
    var orphanCount = 0;
    if (Directory.Exists(sitesRoot))
        orphanCount += AtomicWriter.CleanupOrphanTempFiles(sitesRoot);
    if (Directory.Exists(generatedRoot))
        orphanCount += AtomicWriter.CleanupOrphanTempFiles(generatedRoot);
    if (orphanCount > 0)
        Console.WriteLine($"[startup] reaped {orphanCount} orphan *.tmp file(s) from prior daemon crash");
}
catch (Exception tmpEx)
{
    Console.WriteLine($"[startup] orphan tmp cleanup failed: {tmpEx.Message}");
}

// Pre-register Windows Defender Firewall rules for managed service ports
// so the user doesn't see the "Allow access" dialog on every first bind.
// Silently no-ops on non-Windows, non-admin, or when rules already exist.
// Best-effort: failures log warnings but never block daemon startup.
try
{
    var firewall = app.Services.GetRequiredService<WindowsFirewallManager>();
    var created = await firewall.EnsureRulesRegisteredAsync();
    if (created > 0)
        Console.WriteLine($"[firewall] registered {created} inbound rule(s) for managed service ports");
}
catch (Exception fwEx)
{
    Console.WriteLine($"[firewall] rule registration skipped: {fwEx.Message}");
}

// Auto-start services if setting enabled (default: true). Backed by SettingsStore
// so the user can flip it from the Settings page without recompiling. See
// GET/PUT /api/settings endpoints below.
var autoStartEnabled = app.Services.GetRequiredService<SettingsStore>().AutoStartEnabled;
if (autoStartEnabled)
{
    var modules = app.Services.GetServices<IServiceModule>();
    foreach (var module in modules)
    {
        try
        {
            await module.StartAsync(CancellationToken.None);
            var status = await module.GetStatusAsync(CancellationToken.None);
            Console.WriteLine($"[auto-start] {status.Id}: {status.State}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[auto-start] {module.ServiceId} failed: {ex.Message}");
        }
    }
}

app.MapGet("/api/sites", (SiteManager sm) => Results.Ok(sm.Sites.Values));

app.MapGet("/api/sites/{domain}", (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    return site is not null ? Results.Ok(site) : Results.NotFound();
});

app.MapPost("/api/sites", async (SiteConfig site, SiteManager sm, SiteOrchestrator orchestrator) =>
{
    try { SiteManager.ValidateDomain(site.Domain); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    if (sm.Get(site.Domain) is not null)
        return Results.Conflict(new { error = $"Site {site.Domain} already exists" });
    try
    {
        var created = await sm.CreateAsync(site);
        await orchestrator.ApplyAsync(created);
        return Results.Created($"/api/sites/{created.Domain}", created);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPut("/api/sites/{domain}", async (string domain, SiteConfig site, SiteManager sm, SiteOrchestrator orchestrator) =>
{
    if (sm.Get(domain) is null)
        return Results.NotFound();
    site.Domain = domain;
    try
    {
        var updated = await sm.UpdateAsync(site);
        await orchestrator.ApplyAsync(updated);
        return Results.Ok(updated);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/sites/{domain}", async (string domain, SiteManager sm, SiteOrchestrator orchestrator) =>
{
    try
    {
        if (!sm.Delete(domain)) return Results.NotFound();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    await orchestrator.RemoveAsync(domain);
    return Results.NoContent();
});

// List config history versions for a site
app.MapGet("/api/sites/{domain}/history", (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();

    var generatedDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot, "history");
    if (!Directory.Exists(generatedDir))
        return Results.Ok(Array.Empty<object>());

    var prefix = $"{domain}.conf.";
    var versions = Directory.GetFiles(generatedDir, prefix + "*")
        .Select(f => new
        {
            timestamp = Path.GetFileName(f).Substring(prefix.Length),
            size = new FileInfo(f).Length,
            createdAt = File.GetLastWriteTimeUtc(f),
            path = f,
        })
        .OrderByDescending(v => v.timestamp)
        .ToList();
    return Results.Ok(versions);
});

// Rollback a site's vhost config to a specific historical version
app.MapPost("/api/sites/{domain}/rollback/{timestamp}", async (string domain, string timestamp, SiteManager sm, SiteOrchestrator orchestrator) =>
{
    // SECURITY: validate both path segments before building filesystem paths. The
    // timestamp is user-supplied and could contain "../" or null bytes — without
    // validation an attacker (with a valid auth token) could overwrite any file
    // the daemon user can write. domain is already validated by sm.Get() (the
    // dictionary lookup will fail for traversal strings) but we re-check anyway
    // so the path-containment assertion below is definitely safe.
    try { SiteManager.ValidateDomain(domain); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

    // Timestamp format produced by config history rotation is ISO-like with digits,
    // letters and safe punctuation (e.g. "20260411T140522Z"). Reject anything else.
    if (string.IsNullOrWhiteSpace(timestamp) ||
        !System.Text.RegularExpressions.Regex.IsMatch(timestamp, @"^[a-zA-Z0-9_\-.:]{1,64}$"))
    {
        return Results.BadRequest(new { error = "Invalid timestamp format" });
    }

    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site {domain} not found" });

    var historyDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot, "history");
    var historyDirFull = Path.GetFullPath(historyDir);
    var historyFile = Path.GetFullPath(Path.Combine(historyDir, $"{domain}.conf.{timestamp}"));
    if (!historyFile.StartsWith(historyDirFull, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Resolved history path escapes history root" });
    if (!File.Exists(historyFile))
        return Results.NotFound(new { error = $"History entry {timestamp} not found" });

    var generatedRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot);
    var generatedFile = Path.GetFullPath(Path.Combine(generatedRoot, $"{domain}.conf"));
    if (!generatedFile.StartsWith(generatedRoot, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Resolved generated path escapes generated root" });

    File.Copy(historyFile, generatedFile, overwrite: true);
    // Re-apply through orchestrator so vhost is also written next to Apache binary
    await orchestrator.ApplyAsync(site);
    return Results.Ok(new { domain, restoredFrom = timestamp });
});

// Telemetry consent — Phase 7 plan item #111.
// NKS WDC sends NO telemetry by default. This endpoint lets the Settings page
// read the current consent state and update it. No actual Sentry/metrics
// transport is wired yet — this iteration establishes the consent gate and
// persistence so the wire-up can be a small future commit.
app.MapGet("/api/telemetry/consent", (TelemetryConsent consent) =>
{
    consent.Load(); // pick up manual edits to the file
    return Results.Ok(new
    {
        enabled = consent.Enabled,
        crashReports = consent.CrashReports,
        usageMetrics = consent.UsageMetrics,
        consentGivenUtc = consent.ConsentGivenUtc,
    });
});

app.MapPost("/api/telemetry/consent", async (HttpContext ctx, TelemetryConsent consent) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, bool>>();
    var enabled = body?.GetValueOrDefault("enabled") ?? false;
    var crashReports = body?.GetValueOrDefault("crashReports") ?? false;
    var usageMetrics = body?.GetValueOrDefault("usageMetrics") ?? false;
    consent.Save(enabled, crashReports, usageMetrics);
    return Results.Ok(new
    {
        enabled = consent.Enabled,
        crashReports = consent.CrashReports,
        usageMetrics = consent.UsageMetrics,
        consentGivenUtc = consent.ConsentGivenUtc,
    });
});

app.MapDelete("/api/telemetry/consent", (TelemetryConsent consent) =>
{
    consent.Revoke();
    return Results.NoContent();
});

// Onboarding state — Phase 7 plan item.
// First-run detection: a simple ~/.wdc/data/onboarding-complete.flag file marks the
// wizard as finished. Before that file exists the frontend shows the onboarding
// wizard. The wizard also returns which prerequisites are already satisfied so it
// can skip ahead past any steps the user has already completed manually.
app.MapGet("/api/onboarding/state", (BinaryManager bm) =>
{
    var flagFile = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "onboarding-complete.flag");

    var installed = bm.ListInstalled();
    var hasApache = installed.Any(b => b.App.Equals("apache", StringComparison.OrdinalIgnoreCase));
    var hasPhp = installed.Any(b => b.App.Equals("php", StringComparison.OrdinalIgnoreCase));
    var hasMysql = installed.Any(b => b.App.Equals("mysql", StringComparison.OrdinalIgnoreCase));
    var hasMkcert = installed.Any(b => b.App.Equals("mkcert", StringComparison.OrdinalIgnoreCase));

    // mkcert CA installed state — check presence of rootCA.pem in the mkcert CAROOT
    var mkcertRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mkcert");
    var mkcertCaInstalled = File.Exists(Path.Combine(mkcertRoot, "rootCA.pem"));

    return Results.Ok(new
    {
        completed = File.Exists(flagFile),
        prerequisites = new
        {
            apacheInstalled = hasApache,
            phpInstalled = hasPhp,
            mysqlInstalled = hasMysql,
            mkcertBinaryInstalled = hasMkcert,
            mkcertCaInstalled,
        }
    });
});

app.MapPost("/api/onboarding/complete", () =>
{
    var flagFile = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "onboarding-complete.flag");
    Directory.CreateDirectory(Path.GetDirectoryName(flagFile)!);
    File.WriteAllText(flagFile, DateTime.UtcNow.ToString("O"));
    return Results.Ok(new { completed = true });
});

// Backup / restore — Phase 7 plan item.
// Creates a zip of ~/.wdc/sites + ~/.wdc/data/state.db + ~/.wdc/ssl/sites + ~/.wdc/caddy.
// Restore is atomic with an automatic pre-restore safety backup.
app.MapGet("/api/backup/list", (BackupManager bm) =>
{
    var list = bm.ListBackups()
        .Select(b => new { path = b.Path, size = b.Size, createdUtc = b.Created })
        .ToList();
    return Results.Ok(new { count = list.Count, backups = list });
});

app.MapPost("/api/backup", (BackupManager bm, HttpContext ctx) =>
{
    try
    {
        var outPath = ctx.Request.Query["out"].FirstOrDefault();
        var (path, files, size) = bm.CreateBackup(outPath);
        return Results.Ok(new { path, files, size });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Backup failed: {ex.Message}");
    }
});

app.MapGet("/api/backup/download", (BackupManager bm, string? path) =>
{
    string target;
    if (string.IsNullOrEmpty(path))
    {
        var latest = bm.ListBackups().FirstOrDefault();
        if (latest.Path is null) return Results.NotFound(new { error = "No backups available" });
        target = latest.Path;
    }
    else
    {
        var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Path escapes backup root" });
        target = resolved;
    }
    if (!File.Exists(target)) return Results.NotFound(new { error = "Backup file not found" });
    return Results.File(target, "application/zip", Path.GetFileName(target));
});

app.MapPost("/api/restore", async (BackupManager bm, HttpContext ctx) =>
{
    try
    {
        string archivePath;
        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null) return Results.BadRequest(new { error = "No file uploaded" });
            var tmp = Path.Combine(Path.GetTempPath(), $"wdc-restore-{Guid.NewGuid():N}.zip");
            using (var fs = File.Create(tmp))
                await file.CopyToAsync(fs);
            archivePath = tmp;
        }
        else
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            var requested = body?.GetValueOrDefault("path");
            if (string.IsNullOrWhiteSpace(requested))
                return Results.BadRequest(new { error = "path or multipart file required" });
            var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
            var resolved = Path.GetFullPath(requested);
            if (!resolved.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Path escapes backup root" });
            archivePath = resolved;
        }

        var (restored, safety) = bm.RestoreBackup(archivePath);
        return Results.Ok(new { restored, safetyBackup = safety, archive = archivePath });
    }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) { return Results.Problem($"Restore failed: {ex.Message}"); }
});

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

app.MapPost("/api/sites/migrate-mamp", async (MampMigrator migrator, SiteManager sm) =>
{
    try
    {
        var imported = await migrator.ImportAsync(sm);
        return Results.Ok(new { count = imported.Count, imported });
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
            await orchestrator.ApplyAsync(site);
            results.Add(new { domain = site.Domain, ok = true });
        }
        catch (Exception ex)
        {
            results.Add(new { domain = site.Domain, ok = false, error = ex.Message });
        }
    }
    return Results.Ok(results);
});

app.MapPost("/api/sites/{domain}/detect-framework", async (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();
    var framework = sm.DetectFramework(site.DocumentRoot);
    if (framework is not null && framework != site.Framework)
    {
        site.Framework = framework;
        await sm.UpdateAsync(site);
    }
    return Results.Ok(new { domain, framework });
});

// Filesystem browse — backs the FolderBrowser dialog in the site-edit UI
// so users can pick a document root via a tree instead of typing a raw
// Windows path. No sandboxing: the GUI runs with the same permissions as
// the user who launched it anyway, and the alternative (restricting to
// %USERPROFILE%) would block legitimate picks like C:\xampp\htdocs.
app.MapGet("/api/fs/browse", (string? path) =>
{
    try
    {
        string target;
        if (string.IsNullOrWhiteSpace(path))
        {
            // Default landing: drives on Windows, $HOME otherwise
            if (OperatingSystem.IsWindows())
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new
                    {
                        name = d.Name,
                        path = d.Name,
                        isDir = true,
                        isFile = false,
                        size = 0L,
                    })
                    .ToArray();
                return Results.Ok(new
                {
                    path = "",
                    parent = (string?)null,
                    entries = drives,
                });
            }
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else
        {
            target = Path.GetFullPath(path);
        }

        if (!Directory.Exists(target)) return Results.NotFound(new { error = $"Not a directory: {target}" });

        var parent = Directory.GetParent(target)?.FullName;

        var dirs = Directory.EnumerateDirectories(target)
            .Select(d => new DirectoryInfo(d))
            .Where(di => (di.Attributes & FileAttributes.Hidden) == 0
                     && (di.Attributes & FileAttributes.System) == 0)
            .Select(di => new
            {
                name = di.Name,
                path = di.FullName,
                isDir = true,
                isFile = false,
                size = 0L,
            });

        var files = Directory.EnumerateFiles(target)
            .Select(f => new FileInfo(f))
            .Where(fi => (fi.Attributes & FileAttributes.Hidden) == 0
                     && (fi.Attributes & FileAttributes.System) == 0)
            .Select(fi => new
            {
                name = fi.Name,
                path = fi.FullName,
                isDir = false,
                isFile = true,
                size = fi.Length,
            });

        return Results.Ok(new
        {
            path = target,
            parent,
            entries = dirs.OrderBy(e => e.name).Concat(files.OrderBy(e => e.name)).ToArray(),
        });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.StatusCode(403);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ── Cloudflare Tunnel plugin — passthrough endpoints ─────────────────────
// The Cloudflare plugin lives in a separate AssemblyLoadContext so we
// can't reference its types directly without polluting the daemon's
// dependency graph. Resolve the plugin instance + CloudflareApi /
// CloudflareConfig via reflection and invoke methods through the shared
// IServiceProvider. Everything returns the raw Cloudflare API JSON blob
// so the frontend can render any subset without hand-written DTOs.

object? ResolveCloudflareServiceOrNull(IServiceProvider sp, string typeName)
{
    var plugin = pluginLoader.Plugins
        .FirstOrDefault(p => p.Instance.Id == "nks.wdc.cloudflare");
    if (plugin == null) return null;
    var t = plugin.Assembly.GetTypes().FirstOrDefault(x => x.Name == typeName);
    if (t == null) return null;
    return sp.GetService(t);
}

async Task<IResult> InvokeCfAsync(string apiMethodName, object[] args, IServiceProvider sp)
{
    var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
    if (api == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });
    var method = api.GetType().GetMethod(apiMethodName);
    if (method == null) return Results.NotFound(new { error = $"Method {apiMethodName} not found" });
    try
    {
        var task = (Task)method.Invoke(api, args)!;
        await task.ConfigureAwait(false);
        var resultProp = task.GetType().GetProperty("Result");
        var value = resultProp?.GetValue(task);
        return Results.Ok(value);
    }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
    {
        return Results.BadRequest(new { error = tie.InnerException.Message });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}

// Settings: GET returns redacted config (secrets masked), PUT saves
app.MapGet("/api/cloudflare/config", (IServiceProvider sp) =>
{
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    if (cfg == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });
    var redactedMethod = cfg.GetType().GetMethod("Redacted");
    var redacted = redactedMethod?.Invoke(cfg, null);
    return Results.Ok(redacted);
});

app.MapPut("/api/cloudflare/config", async (HttpContext ctx, IServiceProvider sp) =>
{
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    if (cfg == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

    // Accept a loose JSON body and copy known properties onto the live
    // config instance. Fields omitted from the body stay untouched.
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    var root = doc.RootElement;
    var t = cfg.GetType();

    void Apply(string jsonKey, string propName)
    {
        if (!root.TryGetProperty(jsonKey, out var el)) return;
        if (el.ValueKind != System.Text.Json.JsonValueKind.String &&
            el.ValueKind != System.Text.Json.JsonValueKind.Null) return;
        var prop = t.GetProperty(propName);
        prop?.SetValue(cfg, el.ValueKind == System.Text.Json.JsonValueKind.Null ? null : el.GetString());
    }

    Apply("cloudflaredPath", "CloudflaredPath");
    Apply("tunnelToken", "TunnelToken");
    Apply("tunnelName", "TunnelName");
    Apply("tunnelId", "TunnelId");
    Apply("apiToken", "ApiToken");
    Apply("accountId", "AccountId");
    Apply("defaultZoneId", "DefaultZoneId");
    Apply("subdomainTemplate", "SubdomainTemplate");

    var saveMethod = t.GetMethod("Save");
    saveMethod?.Invoke(cfg, null);

    var redacted = t.GetMethod("Redacted")?.Invoke(cfg, null);
    return Results.Ok(redacted);
});

app.MapGet("/api/cloudflare/verify", (IServiceProvider sp) =>
    InvokeCfAsync("VerifyTokenAsync", new object[] { CancellationToken.None }, sp));

// Compute a suggested public subdomain for a local domain. The Cloudflare
// plugin owns the template + install-salt hash logic, so SiteEdit can show
// a stable auto-filled value like "myapp-bffa44" that doesn't drift.
app.MapGet("/api/cloudflare/suggest-subdomain", (string domain, IServiceProvider sp) =>
{
    if (string.IsNullOrWhiteSpace(domain))
        return Results.BadRequest(new { error = "domain query param required" });
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    if (cfg == null)
        return Results.NotFound(new { error = "Cloudflare plugin not loaded" });
    var renderMethod = cfg.GetType().GetMethod("RenderSubdomain");
    if (renderMethod == null)
        return Results.Problem("RenderSubdomain not available");
    var suggestion = renderMethod.Invoke(cfg, new object[] { domain }) as string;
    return Results.Ok(new { suggestion, domain });
});

// One-token auto-setup: user pastes an API token → we verify it, list
// accounts, pick the first one, find or create a tunnel named
// NKS-WDC-Tunnel-{md5[..12]}, fetch its JWT, and persist everything.
// After this the user never has to enter an account/tunnel/jwt manually —
// zones + per-site configuration are the only remaining inputs.
app.MapPost("/api/cloudflare/auto-setup", async (HttpContext ctx, IServiceProvider sp) =>
{
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
    if (cfg == null || api == null)
        return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    if (!doc.RootElement.TryGetProperty("apiToken", out var tokenEl) ||
        string.IsNullOrWhiteSpace(tokenEl.GetString()))
    {
        return Results.BadRequest(new { error = "apiToken is required" });
    }
    var token = tokenEl.GetString()!;

    // 1. Stage the token onto the live config so the API wrapper picks it up.
    var tCfg = cfg.GetType();
    tCfg.GetProperty("ApiToken")?.SetValue(cfg, token);

    try
    {
        // 2. Verify — fail fast with a readable error if the token is wrong
        var verifyMethod = api.GetType().GetMethod("VerifyTokenAsync");
        var verifyTask = (Task)verifyMethod!.Invoke(api, new object[] { CancellationToken.None })!;
        await verifyTask;

        // 3. Pick account — use first returned one, or fall back to whatever
        //    the user already had saved (lets power-users override)
        var listAccounts = api.GetType().GetMethod("ListAccountsAsync");
        var accountsTask = (Task)listAccounts!.Invoke(api, new object[] { CancellationToken.None })!;
        await accountsTask;
        var accountsJson = (System.Text.Json.JsonElement)accountsTask.GetType().GetProperty("Result")!.GetValue(accountsTask)!;
        string? accountId = null;
        string? accountName = null;
        if (accountsJson.TryGetProperty("result", out var arr) &&
            arr.ValueKind == System.Text.Json.JsonValueKind.Array &&
            arr.GetArrayLength() > 0)
        {
            accountId = arr[0].GetProperty("id").GetString();
            accountName = arr[0].GetProperty("name").GetString();
        }
        if (string.IsNullOrEmpty(accountId))
            return Results.BadRequest(new { error = "Token has no associated accounts — add Account read scope" });

        tCfg.GetProperty("AccountId")?.SetValue(cfg, accountId);

        // 4. Deterministic tunnel name — same token always resolves to the
        //    same tunnel so repeated auto-setup runs don't create dupes.
        var md5 = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        var md5Hex = Convert.ToHexString(md5).ToLowerInvariant()[..12];
        var tunnelName = $"NKS-WDC-Tunnel-{md5Hex}";

        var findOrCreate = api.GetType().GetMethod("FindOrCreateTunnelAsync");
        var tunnelTask = (Task)findOrCreate!.Invoke(api, new object[] { tunnelName, CancellationToken.None })!;
        await tunnelTask;
        var tunnelJson = (System.Text.Json.JsonElement)tunnelTask.GetType().GetProperty("Result")!.GetValue(tunnelTask)!;
        var tunnelId = tunnelJson.GetProperty("id").GetString();

        tCfg.GetProperty("TunnelId")?.SetValue(cfg, tunnelId);
        tCfg.GetProperty("TunnelName")?.SetValue(cfg, tunnelName);

        // 5. Fetch JWT — cloudflared needs this to run the tunnel locally
        var getToken = api.GetType().GetMethod("GetTunnelTokenAsync");
        var jwtTask = (Task)getToken!.Invoke(api, new object[] { tunnelId!, CancellationToken.None })!;
        await jwtTask;
        var jwt = (string?)jwtTask.GetType().GetProperty("Result")!.GetValue(jwtTask);
        if (!string.IsNullOrEmpty(jwt))
            tCfg.GetProperty("TunnelToken")?.SetValue(cfg, jwt);

        // 6. Persist everything
        tCfg.GetMethod("Save")?.Invoke(cfg, null);

        return Results.Ok(new
        {
            ok = true,
            account = new { id = accountId, name = accountName },
            tunnel = new { id = tunnelId, name = tunnelName },
            tokenFetched = !string.IsNullOrEmpty(jwt),
        });
    }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
    {
        return Results.BadRequest(new { error = tie.InnerException.Message });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Sync all sites that have Cloudflare.Enabled=true:
//   - Upserts a proxied CNAME per site → tunnelId.cfargotunnel.com
//   - Rebuilds the tunnel ingress config with one rule per site + 404
//   - Each ingress rule carries httpHostHeader = site.Domain so Apache
//     matches the LOCAL vhost rather than the public hostname
app.MapPost("/api/cloudflare/sync", async (IServiceProvider sp, SiteManager sm) =>
{
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
    if (cfg == null || api == null)
        return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

    var tCfg = cfg.GetType();
    var tunnelId = tCfg.GetProperty("TunnelId")?.GetValue(cfg) as string;
    if (string.IsNullOrWhiteSpace(tunnelId))
        return Results.BadRequest(new { error = "Tunnel not configured. Run auto-setup first." });

    var sitesWithCf = sm.Sites.Values
        .Where(s => s.Cloudflare is { Enabled: true }
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneId)
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.Subdomain))
        .ToList();
    var dormantCf = sm.Sites.Values
        .Where(s => s.Cloudflare is { Enabled: false }
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneId)
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.Subdomain)
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneName))
        .ToList();

    var upserted = new List<object>();
    var deleted = new List<object>();
    try
    {
        // DNS: one CNAME per enabled site
        var upsertMethod = api.GetType().GetMethod("UpsertCnameToTunnelAsync");
        foreach (var s in sitesWithCf)
        {
            var cf = s.Cloudflare!;
            var fullName = $"{cf.Subdomain}.{cf.ZoneName}";
            var task = (Task)upsertMethod!.Invoke(api,
                new object[] { cf.ZoneId, fullName, tunnelId, CancellationToken.None })!;
            await task;
            upserted.Add(new { domain = s.Domain, cname = fullName });
        }

        // DNS: delete CNAME for dormant (disabled-but-configured) sites so
        // toggling off in SiteEdit actually takes the public hostname down.
        var deleteMethod = api.GetType().GetMethod("DeleteCnameByNameAsync");
        foreach (var s in dormantCf)
        {
            var cf = s.Cloudflare!;
            var fullName = $"{cf.Subdomain}.{cf.ZoneName}";
            try
            {
                var task = (Task)deleteMethod!.Invoke(api,
                    new object[] { cf.ZoneId, fullName, CancellationToken.None })!;
                await task;
                deleted.Add(new { domain = s.Domain, cname = fullName });
            }
            catch { /* best-effort per-site */ }
        }

        // Ingress: one rule per site with httpHostHeader override
        var ruleType = api.GetType().Assembly.GetType(
            "NKS.WebDevConsole.Plugin.Cloudflare.TunnelIngressRule")!;
        var ruleListType = typeof(List<>).MakeGenericType(ruleType);
        var rules = (System.Collections.IList)Activator.CreateInstance(ruleListType)!;
        var ruleCtor = ruleType.GetConstructors().First();

        foreach (var s in sitesWithCf)
        {
            var cf = s.Cloudflare!;
            var hostname = $"{cf.Subdomain}.{cf.ZoneName}";

            // Mirror SiteOrchestrator.SyncCloudflareIfConfiguredAsync — pick HTTPS
            // when the local vhost has SSL so cloudflared bypasses the Apache
            // HTTP→HTTPS redirect. See that method for the full rationale.
            string service;
            string? originServerName = null;
            bool noTLSVerify = false;
            if (s.SslEnabled)
            {
                var httpsPort = s.HttpsPort > 0 ? s.HttpsPort : 443;
                service = $"https://localhost:{httpsPort}";
                originServerName = s.Domain;
                noTLSVerify = true;
            }
            else
            {
                var httpPort = s.HttpPort > 0 ? s.HttpPort : 80;
                service = $"http://localhost:{httpPort}";
            }

            rules.Add(ruleCtor.Invoke(new object?[]
            {
                hostname, service, s.Domain, originServerName, noTLSVerify,
            }));
        }

        var ingressMethod = api.GetType().GetMethod("UpdateTunnelIngressAsync");
        var ingressTask = (Task)ingressMethod!.Invoke(api,
            new object[] { tunnelId, rules, CancellationToken.None })!;
        await ingressTask;

        return Results.Ok(new
        {
            ok = true,
            synced = upserted.Count,
            sites = upserted,
            deleted = deleted.Count,
            dormant = deleted,
        });
    }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
    {
        return Results.BadRequest(new { error = tie.InnerException.Message });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/cloudflare/zones", (IServiceProvider sp) =>
    InvokeCfAsync("ListZonesAsync", new object[] { CancellationToken.None }, sp));

app.MapGet("/api/cloudflare/zones/{zoneId}", (string zoneId, IServiceProvider sp) =>
    InvokeCfAsync("GetZoneAsync", new object[] { zoneId, CancellationToken.None }, sp));

app.MapGet("/api/cloudflare/zones/{zoneId}/dns", (string zoneId, IServiceProvider sp) =>
    InvokeCfAsync("ListDnsRecordsAsync", new object[] { zoneId, CancellationToken.None }, sp));

app.MapPost("/api/cloudflare/zones/{zoneId}/dns", async (string zoneId, HttpContext ctx, IServiceProvider sp) =>
{
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    var root = doc.RootElement;
    var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "CNAME" : "CNAME";
    var name = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
    var content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
    var proxied = !root.TryGetProperty("proxied", out var pEl) || pEl.GetBoolean();
    var ttl = root.TryGetProperty("ttl", out var tEl2) ? tEl2.GetInt32() : 1;
    return await InvokeCfAsync("CreateDnsRecordAsync",
        new object[] { zoneId, type, name, content, proxied, ttl, CancellationToken.None }, sp);
});

app.MapDelete("/api/cloudflare/zones/{zoneId}/dns/{recordId}", (string zoneId, string recordId, IServiceProvider sp) =>
    InvokeCfAsync("DeleteDnsRecordAsync", new object[] { zoneId, recordId, CancellationToken.None }, sp));

app.MapGet("/api/cloudflare/tunnels", (IServiceProvider sp) =>
    InvokeCfAsync("ListTunnelsAsync", new object[] { CancellationToken.None }, sp));

app.MapGet("/api/cloudflare/tunnels/{tunnelId}/configuration", (string tunnelId, IServiceProvider sp) =>
    InvokeCfAsync("GetTunnelConfigurationAsync", new object[] { tunnelId, CancellationToken.None }, sp));

// Replace the tunnel's ingress rules. Body shape:
//   { "rules": [ { "hostname": "blog.nks-dev.cz", "service": "http://localhost:80" }, ... ] }
// CloudflareApi.UpdateTunnelIngressAsync appends the mandatory catch-all
// 404 rule automatically so callers don't have to know the protocol detail.
app.MapPut("/api/cloudflare/tunnels/{tunnelId}/configuration",
    async (string tunnelId, HttpContext ctx, IServiceProvider sp) =>
{
    var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
    if (api == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    if (!doc.RootElement.TryGetProperty("rules", out var rulesEl) ||
        rulesEl.ValueKind != System.Text.Json.JsonValueKind.Array)
    {
        return Results.BadRequest(new { error = "Missing 'rules' array" });
    }

    // Build the plugin's TunnelIngressRule record via reflection so we don't
    // need a direct type reference into the plugin's ALC.
    var ruleType = api.GetType().Assembly.GetType(
        "NKS.WebDevConsole.Plugin.Cloudflare.TunnelIngressRule");
    if (ruleType == null)
        return Results.Problem("TunnelIngressRule type not found in plugin assembly");

    var ruleListType = typeof(List<>).MakeGenericType(ruleType);
    var ruleList = (System.Collections.IList)Activator.CreateInstance(ruleListType)!;
    var ruleCtor = ruleType.GetConstructors().First();

    foreach (var el in rulesEl.EnumerateArray())
    {
        var hostname = el.TryGetProperty("hostname", out var h) ? h.GetString() ?? "" : "";
        var service = el.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(service)) continue;
        ruleList.Add(ruleCtor.Invoke(new object[] { hostname, service }));
    }

    var method = api.GetType().GetMethod("UpdateTunnelIngressAsync");
    if (method == null) return Results.Problem("UpdateTunnelIngressAsync not found");

    try
    {
        var task = (Task)method.Invoke(api, new object[] { tunnelId, ruleList, CancellationToken.None })!;
        await task.ConfigureAwait(false);
        var resultProp = task.GetType().GetProperty("Result");
        return Results.Ok(resultProp?.GetValue(task));
    }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
    {
        return Results.BadRequest(new { error = tie.InnerException.Message });
    }
});

// PHP versions — delegate to PhpPlugin via reflection
app.MapGet("/api/php/versions", () =>
{
    var phpPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.php");
    if (phpPlugin == null) return Results.NotFound();
    var method = phpPlugin.Instance.GetType().GetMethod("GetInstalledVersions");
    if (method != null)
    {
        var versions = method.Invoke(phpPlugin.Instance, null);
        return Results.Ok(versions);
    }
    return Results.Ok(Array.Empty<object>());
});

// PHP extensions for a given version
app.MapGet("/api/php/{version}/extensions", async (string version) =>
{
    var phpPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.php");
    if (phpPlugin == null) return Results.NotFound();
    var method = phpPlugin.Instance.GetType().GetMethod("GetExtensionsForVersion");
    if (method != null)
    {
        var task = method.Invoke(phpPlugin.Instance, new object[] { version }) as Task;
        if (task != null)
        {
            await task;
            var resultProp = task.GetType().GetProperty("Result");
            return Results.Ok(resultProp?.GetValue(task));
        }
    }
    return Results.Ok(Array.Empty<object>());
});

// Toggle a PHP extension on/off for a given version. Writes a persistent
// override via PhpExtensionOverrides, patches the live php.ini files in-place
// (comments / uncomments `extension=name` lines so the change survives the
// next daemon start even before the plugin re-runs its ini generator), and
// restarts the PHP module so the change takes effect immediately.
//
// Body: { "enabled": true|false }
// Path: /api/php/{version}/extensions/{name}  (version is major.minor, e.g. "8.4")
app.MapPost("/api/php/{version}/extensions/{name}", async (
    string version,
    string name,
    HttpContext ctx,
    PhpExtensionOverrides overrides,
    IServiceProvider sp,
    ILoggerFactory lf) =>
{
    // Input validation — extension names are lowercase alnum + underscore.
    if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+$"))
        return Results.BadRequest(new { error = "version must be major.minor, e.g. 8.4" });
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z0-9_]{1,64}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        return Results.BadRequest(new { error = "invalid extension name" });

    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>();
    var enabled = body is not null
        && body.TryGetValue("enabled", out var v)
        && v switch
        {
            bool b => b,
            JsonElement j when j.ValueKind == JsonValueKind.True => true,
            _ => false,
        };

    overrides.SetOverride(version, name, enabled);

    // Patch the live php.ini files — one next to the binary (used by
    // mod_fcgid-spawned php-cgi.exe) and one under _config.ConfigBaseDirectory
    // (used by the daemon's own invocations). We comment/uncomment the
    // `extension=name` directive so the change takes effect on next spawn.
    var phpRoot = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot, "php");
    var patched = new List<string>();
    if (Directory.Exists(phpRoot))
    {
        foreach (var verDir in Directory.GetDirectories(phpRoot))
        {
            var dirName = Path.GetFileName(verDir);
            if (!dirName.StartsWith(version)) continue;
            var iniPath = Path.Combine(verDir, "php.ini");
            if (!File.Exists(iniPath)) continue;
            try
            {
                PatchExtensionLine(iniPath, name, enabled);
                patched.Add(iniPath);
            }
            catch (Exception ex)
            {
                lf.CreateLogger("PhpExt").LogWarning(ex, "Failed to patch {Path}", iniPath);
            }
        }
    }

    // Restart the PHP module so the new ini is picked up. Non-fatal if it
    // fails — the override is already persisted for the next startup.
    try
    {
        var modules = sp.GetServices<IServiceModule>();
        var phpModule = modules.FirstOrDefault(m => m.ServiceId.Equals("php", StringComparison.OrdinalIgnoreCase));
        if (phpModule is not null)
        {
            await phpModule.StopAsync(CancellationToken.None);
            await phpModule.StartAsync(CancellationToken.None);
        }
    }
    catch (Exception ex)
    {
        lf.CreateLogger("PhpExt").LogWarning(ex, "Failed to restart PHP module after extension toggle");
    }

    return Results.Ok(new { version, name, enabled, patchedFiles = patched });

    // --- local helper ---
    static void PatchExtensionLine(string iniPath, string extName, bool shouldBeEnabled)
    {
        var lines = File.ReadAllLines(iniPath).ToList();
        var enableLine = $"extension={extName}";
        var disableLine = $";extension={extName}";
        var pattern = new System.Text.RegularExpressions.Regex(
            @"^\s*;?\s*extension\s*=\s*" + System.Text.RegularExpressions.Regex.Escape(extName) + @"\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        bool found = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!pattern.IsMatch(lines[i])) continue;
            lines[i] = shouldBeEnabled ? enableLine : disableLine;
            found = true;
        }
        if (!found)
        {
            // Extension not yet mentioned — add under [ext] section or EOF.
            lines.Add("");
            lines.Add(shouldBeEnabled ? enableLine : disableLine);
        }
        File.WriteAllLines(iniPath, lines);
    }
});

// Version validation (checks if a version string is available for a service)
app.MapPost("/api/services/{id}/validate-version", async (string id, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var version = body?.GetValueOrDefault("version") ?? "";
    // For PHP: check if the version exists in detected installations
    if (id.Contains("php", StringComparison.OrdinalIgnoreCase))
    {
        var phpPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.php");
        if (phpPlugin != null)
        {
            var method = phpPlugin.Instance.GetType().GetMethod("GetInstalledVersions");
            if (method?.Invoke(phpPlugin.Instance, null) is System.Collections.IEnumerable versions)
            {
                foreach (var v in versions)
                {
                    var vProp = v.GetType().GetProperty("Version");
                    if (vProp?.GetValue(v)?.ToString()?.StartsWith(version) == true)
                        return Results.Ok(new { valid = true, version = vProp.GetValue(v)?.ToString() });
                }
            }
        }
    }
    return Results.Ok(new { valid = !string.IsNullOrEmpty(version), version });
});

// Service logs
app.MapGet("/api/services/{id}/logs", async (string id, IServiceProvider sp, int? lines) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound();
    var logs = await module.GetLogsAsync(lines ?? 100, CancellationToken.None);
    return Results.Ok(logs);
});

// Service config read — returns the main config file content for a service
app.MapGet("/api/services/{id}/config", async (string id, ServiceConfigManager configManager) =>
{
    try
    {
        var files = await configManager.GetFilesAsync(id);
        return Results.Ok(new { serviceId = id, files });
    }
    catch (Exception ex)
    {
        // Graceful fallback: plugins like Cloudflare / SSL / Hosts manage
        // their settings via the REST API, not file-based config, and the
        // ServiceConfigManager throws if asked. Return an empty file list
        // plus an info flag so the frontend can surface a "manage via
        // dedicated page" hint instead of a 500 toast.
        return Results.Ok(new
        {
            serviceId = id,
            files = Array.Empty<object>(),
            info = ex.Message,
            managedExternally = true,
        });
    }
});

app.MapPost("/api/services/{id}/config", async (
    string id,
    HttpContext ctx,
    ServiceConfigManager configManager,
    IServiceProvider sp) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ServiceConfigWriteRequest>();
    if (body == null || string.IsNullOrWhiteSpace(body.Path))
        return Results.BadRequest(new { error = "path is required" });

    await configManager.SaveAsync(id, body.Path, body.Content ?? string.Empty, ctx.RequestAborted);

    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null)
        return Results.Ok(new { saved = true, applied = false, restarted = false, message = "Configuration saved. No matching service module found to apply changes." });

    var status = await module.GetStatusAsync(ctx.RequestAborted);
    if (status.State != ServiceState.Running)
        return Results.Ok(new { saved = true, applied = false, restarted = false, message = "Configuration saved. Service is stopped, so changes will apply on next start." });

    try
    {
        await module.StopAsync(ctx.RequestAborted);
        await module.StartAsync(ctx.RequestAborted);
        return Results.Ok(new { saved = true, applied = true, restarted = true, message = "Configuration saved and service restarted." });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { saved = true, applied = false, restarted = false, message = $"Configuration saved, but service restart failed: {ex.Message}" });
    }
});

// Config validation — dispatches to Apache / PHP / MySQL / Redis based on serviceId.
// Frontend sends serviceId from the ServiceConfig.vue editor; legacy callers that
// omit serviceId get Apache validation (backwards compat with the original endpoint).
// Broadcasts SSE `validation` events (phase: started|passed|failed) so the UI
// ValidationBadge can update live — originally specified in Phase 2 but never
// wired; filled in during the 2026-04-11 strict audit cycle.
app.MapPost("/api/config/validate", async (HttpContext ctx, ConfigValidator validator, BinaryManager bm, SseService sse, ServiceConfigManager configManager) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ConfigValidateRequest>();
    if (body == null) return Results.BadRequest();

    var service = (body.ServiceId ?? "apache").ToLowerInvariant();
    if (!configManager.TryNormalizeManagedPath(service, body.ConfigPath, out var normalizedConfigPath, out var pathError))
        return Results.BadRequest(new { isValid = false, output = pathError });

    // Emit "started" immediately so the UI can show the spinner while
    // the validator binary runs.
    await sse.BroadcastAsync("validation", new
    {
        phase = "started",
        serviceId = service,
        configPath = normalizedConfigPath,
    });

    async Task<IResult> Finish(bool isValid, string output)
    {
        await sse.BroadcastAsync("validation", new
        {
            phase = isValid ? "passed" : "failed",
            serviceId = service,
            configPath = normalizedConfigPath,
            output,
        });
        return Results.Ok(new { isValid, output });
    }

    var apacheRootConfigPath = service is "apache" or "httpd"
        ? (await configManager.GetFilesAsync("apache")).FirstOrDefault(f => f.Name.Equals("httpd.conf", StringComparison.OrdinalIgnoreCase))?.Path
        : null;

    async Task<(bool IsValid, string Output)> ValidateDraftAsync(Func<string, Task<(bool IsValid, string Output)>> run)
    {
        if (service is "apache" or "httpd"
            && !string.IsNullOrEmpty(apacheRootConfigPath)
            && !string.Equals(normalizedConfigPath, apacheRootConfigPath, StringComparison.OrdinalIgnoreCase))
        {
            if (body.Content is null)
                return await run(apacheRootConfigPath);

            var originalContent = await File.ReadAllTextAsync(normalizedConfigPath, ctx.RequestAborted);
            await File.WriteAllTextAsync(normalizedConfigPath, body.Content, ctx.RequestAborted);
            try
            {
                return await run(apacheRootConfigPath);
            }
            finally
            {
                await File.WriteAllTextAsync(normalizedConfigPath, originalContent, ctx.RequestAborted);
            }
        }

        if (body.Content is null)
            return await run(normalizedConfigPath);

        var draftPath = await configManager.WriteDraftAsync(service, normalizedConfigPath, body.Content, ctx.RequestAborted);
        try
        {
            return await run(draftPath);
        }
        finally
        {
            try
            {
                if (File.Exists(draftPath))
                    File.Delete(draftPath);
            }
            catch
            {
                // Best-effort cleanup only — startup tmp sweep catches leftovers.
            }
        }
    }

    // Helper: resolve a managed binary executable for a given app key, never falling
    // back to PATH. Rule: NKS WDC only uses its own binaries under ~/.wdc/binaries/.
    string? ResolveBinary(string appKey, string pluginId, string? pluginProp)
    {
        string? path = null;
        if (pluginProp is not null)
        {
            var plug = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == pluginId);
            var prop = plug?.Instance.GetType().GetProperty(pluginProp);
            if (prop != null) path = prop.GetValue(plug!.Instance) as string;
        }
        if (string.IsNullOrEmpty(path))
            path = bm.ListInstalled(appKey).FirstOrDefault()?.Executable;
        return path;
    }

    switch (service)
    {
        case "apache":
        case "httpd":
        {
            var httpdPath = ResolveBinary("apache", "nks.wdc.apache", "HttpdPath");
            if (string.IsNullOrEmpty(httpdPath) || !File.Exists(httpdPath))
                return await Finish(false, "Apache httpd not found in managed binaries — install it first via POST /api/binaries/install");
            var (isValid, output) = await ValidateDraftAsync(path => validator.ValidateApacheConfig(httpdPath, path));
            return await Finish(isValid, output);
        }
        case "php":
        {
            var phpPath = bm.ListInstalled("php").FirstOrDefault()?.Executable;
            if (string.IsNullOrEmpty(phpPath) || !File.Exists(phpPath))
                return await Finish(false, "PHP not found in managed binaries");
            var (isValid, output) = await ValidateDraftAsync(path => validator.ValidatePhpIni(phpPath, path));
            return await Finish(isValid, output);
        }
        case "mysql":
        case "mariadb":
        {
            var mysqldPath = bm.ListInstalled("mysql").FirstOrDefault()?.Executable;
            if (string.IsNullOrEmpty(mysqldPath) || !File.Exists(mysqldPath))
                return await Finish(false, "mysqld not found in managed binaries");
            var (isValid, output) = await ValidateDraftAsync(path => validator.ValidateMyCnf(mysqldPath, path));
            return await Finish(isValid, output);
        }
        case "redis":
        {
            var redisPath = bm.ListInstalled("redis").FirstOrDefault()?.Executable;
            if (string.IsNullOrEmpty(redisPath) || !File.Exists(redisPath))
                return await Finish(false, "redis-server not found in managed binaries");
            var (isValid, output) = await ValidateDraftAsync(path => validator.ValidateRedisConf(redisPath, path));
            return await Finish(isValid, output);
        }
        default:
            return await Finish(false, $"Unknown serviceId '{body.ServiceId}'. Use apache | php | mysql | redis.");
    }
});

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
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>();
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
            var (path, _, _) = backupManager.CreateBackup();
            safetyBackupPath = path;
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

// Plugin enable/disable — persists to ~/.wdc/data/plugin-state.json via PluginState.
// Plugins remain loaded either way; disabled plugins are hidden from the UI service
// list and skipped by Start All.
app.MapPost("/api/plugins/{id}/enable", (string id, PluginState pluginState) =>
{
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null) return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });
    pluginState.SetEnabled(id, true);
    return Results.Ok(new { id, enabled = true });
});

app.MapPost("/api/plugins/{id}/disable", (string id, PluginState pluginState) =>
{
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null) return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });
    pluginState.SetEnabled(id, false);
    return Results.Ok(new { id, enabled = false });
});

// SSL certificates
app.MapGet("/api/ssl/certs", () =>
{
    var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
    if (sslPlugin == null) return Results.Ok(new { certs = Array.Empty<object>(), mkcertInstalled = false });
    var method = sslPlugin.Instance.GetType().GetMethod("GetCerts");
    if (method != null)
    {
        var result = method.Invoke(sslPlugin.Instance, null);
        if (result is IDictionary<string, object> dict)
            return Results.Ok(new { certs = dict.Values, mkcertInstalled = true });
        // IReadOnlyDictionary — enumerate via reflection
        var values = new List<object>();
        if (result is System.Collections.IEnumerable enumerable)
            foreach (var item in enumerable)
            {
                var kvp = item.GetType();
                var valProp = kvp.GetProperty("Value");
                if (valProp != null) values.Add(valProp.GetValue(item)!);
                else values.Add(item);
            }
        return Results.Ok(new { certs = values, mkcertInstalled = true });
    }
    return Results.Ok(new { certs = Array.Empty<object>(), mkcertInstalled = false });
});

app.MapPost("/api/ssl/install-ca", async () =>
{
    var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
    if (sslPlugin == null) return Results.BadRequest(new { ok = false, message = "SSL plugin not loaded" });
    var method = sslPlugin.Instance.GetType().GetMethod("InstallCA");
    if (method == null) return Results.BadRequest(new { ok = false, message = "InstallCA method not found" });
    var task = (Task<bool>)method.Invoke(sslPlugin.Instance, null)!;
    var success = await task;
    return success
        ? Results.Ok(new { ok = true, message = "CA installed" })
        : Results.BadRequest(new { ok = false, message = "Failed to install CA" });
});

app.MapPost("/api/ssl/generate", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object>>();
    if (body == null || !body.ContainsKey("domain"))
        return Results.BadRequest(new { ok = false, message = "domain required" });

    var domain = body["domain"]?.ToString() ?? "";
    var aliases = Array.Empty<string>();
    if (body.TryGetValue("aliases", out var aliasesObj) && aliasesObj is JsonElement aliasArr && aliasArr.ValueKind == JsonValueKind.Array)
        aliases = aliasArr.EnumerateArray().Select(a => a.GetString() ?? "").Where(s => s.Length > 0).ToArray();

    var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
    if (sslPlugin == null) return Results.BadRequest(new { ok = false, message = "SSL plugin not loaded. Install mkcert first." });

    var method = sslPlugin.Instance.GetType().GetMethod("GenerateCert");
    if (method == null) return Results.BadRequest(new { ok = false, message = "GenerateCert method not found" });

    var task = (Task)method.Invoke(sslPlugin.Instance, new object[] { domain, aliases })!;
    await task;
    var resultProp = task.GetType().GetProperty("Result");
    var result = resultProp?.GetValue(task);

    if (result == null)
        return Results.BadRequest(new { ok = false, message = "mkcert not installed or failed" });

    return Results.Ok(new { ok = true, domain, message = $"Certificate generated for {domain}" });
});

app.MapDelete("/api/ssl/certs/{domain}", (string domain) =>
{
    var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
    if (sslPlugin == null) return Results.BadRequest(new { ok = false, message = "SSL plugin not loaded" });
    var method = sslPlugin.Instance.GetType().GetMethod("RevokeCert");
    if (method == null) return Results.BadRequest(new { ok = false, message = "RevokeCert not found" });
    var success = (bool)method.Invoke(sslPlugin.Instance, new object[] { domain })!;
    return success
        ? Results.Ok(new { ok = true, message = $"Certificate for {domain} revoked" })
        : Results.NotFound(new { ok = false, message = $"No certificate found for {domain}" });
});

// DNS flush
app.MapPost("/api/dns/flush", async () =>
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await CliWrap.Cli.Wrap("ipconfig")
                .WithArguments("/flushdns")
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteBufferedAsync();
            return Results.Ok(new { ok = result.ExitCode == 0, output = result.StandardOutput.Trim() });
        }
        return Results.Ok(new { ok = false, output = "Not supported on this platform" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, output = ex.Message });
    }
});

// Settings CRUD
app.MapGet("/api/settings", async (Database db) =>
{
    using var conn = db.CreateConnection();
    var settings = await conn.QueryAsync<dynamic>("SELECT category, key, value FROM settings");
    var dict = new Dictionary<string, string>();
    foreach (var s in settings) dict[$"{s.category}.{s.key}"] = s.value;
    return Results.Ok(dict);
});

app.MapPut("/api/settings", async (HttpContext ctx, Database db) =>
{
    var settings = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    if (settings == null) return Results.BadRequest();
    using var conn = db.CreateConnection();
    foreach (var (compositeKey, value) in settings)
    {
        var parts = compositeKey.Split('.', 2);
        var category = parts.Length > 1 ? parts[0] : "general";
        var key = parts.Length > 1 ? parts[1] : parts[0];
        await conn.ExecuteAsync(
            "INSERT INTO settings (category, key, value) VALUES (@Category, @Key, @Value) ON CONFLICT(category, key) DO UPDATE SET value = @Value, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')",
            new { Category = category, Key = key, Value = value });
    }
    return Results.Ok(settings);
});

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

// Database identifier validation.
// MySQL identifier rules allow unquoted names matching [a-zA-Z0-9_$] (and $ in some versions).
// We use a strict subset to avoid shell escape, path traversal, and SQL injection when the
// name is inlined into CLI args or (ab)used in SQL strings.
static bool IsValidDatabaseName(string? name)
{
    if (string.IsNullOrEmpty(name)) return false;
    if (name.Length > 64) return false; // MySQL hard limit
    return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_]+$");
}

// Databases — list MySQL databases via mysql CLI
app.MapGet("/api/databases", async (BinaryManager bm) =>
{
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.Ok(new { error = "MySQL not installed", databases = Array.Empty<string>() });

    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
    if (!File.Exists(mysqlCli))
        return Results.Ok(new { error = "mysql.exe not found", databases = Array.Empty<string>() });

    try
    {
        var result = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments("-h 127.0.0.1 -P 3306 -u root -N -e \"SHOW DATABASES\"")
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
            return Results.Ok(new { error = result.StandardError.Trim(), databases = Array.Empty<string>() });

        var dbs = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .Where(d => d != "information_schema" && d != "performance_schema" && d != "sys" && d != "mysql")
            .ToList();

        return Results.Ok(new { databases = dbs });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message, databases = Array.Empty<string>() });
    }
});

// Create database
app.MapPost("/api/databases", async (HttpContext ctx, BinaryManager bm) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var dbName = body?.GetValueOrDefault("name") ?? "";
    if (!IsValidDatabaseName(dbName))
        return Results.BadRequest(new { error = "Invalid database name — allowed chars: letters, digits, underscore (max 64 chars)" });

    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });

    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
    var result = await CliWrap.Cli.Wrap(mysqlCli)
        .WithArguments($"-h 127.0.0.1 -P 3306 -u root -e \"CREATE DATABASE IF NOT EXISTS `{dbName}`\"")
        .WithValidation(CliWrap.CommandResultValidation.None)
        .ExecuteBufferedAsync();

    return result.ExitCode == 0
        ? Results.Created($"/api/databases/{dbName}", new { name = dbName })
        : Results.BadRequest(new { error = result.StandardError.Trim() });
});

// Drop database
app.MapDelete("/api/databases/{name}", async (string name, BinaryManager bm) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });

    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });

    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
    var result = await CliWrap.Cli.Wrap(mysqlCli)
        .WithArguments($"-h 127.0.0.1 -P 3306 -u root -e \"DROP DATABASE IF EXISTS `{name}`\"")
        .WithValidation(CliWrap.CommandResultValidation.None)
        .ExecuteBufferedAsync();

    return result.ExitCode == 0
        ? Results.NoContent()
        : Results.BadRequest(new { error = result.StandardError.Trim() });
});

// Database tables
app.MapGet("/api/databases/{name}/tables", async (string name, BinaryManager bm) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
    var result = await CliWrap.Cli.Wrap(mysqlCli)
        .WithArguments($"-h 127.0.0.1 -P 3306 -u root -N -e \"SELECT TABLE_NAME, TABLE_ROWS, ROUND(((DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024), 2) AS size_mb FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{name}' ORDER BY TABLE_NAME\"")
        .WithValidation(CliWrap.CommandResultValidation.None)
        .ExecuteBufferedAsync();
    if (result.ExitCode != 0)
        return Results.BadRequest(new { error = result.StandardError.Trim() });
    var tables = result.StandardOutput.Trim()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            var parts = line.Split('\t');
            return new { name = parts[0], rows = parts.Length > 1 ? parts[1] : "0", size = parts.Length > 2 ? parts[2] + " MB" : "0 MB" };
        }).ToList();
    return Results.Ok(new { tables });
});

// Database size
app.MapGet("/api/databases/{name}/size", async (string name, BinaryManager bm) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
    var result = await CliWrap.Cli.Wrap(mysqlCli)
        .WithArguments($"-h 127.0.0.1 -P 3306 -u root -N -e \"SELECT ROUND(SUM(DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024, 2) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{name}'\"")
        .WithValidation(CliWrap.CommandResultValidation.None)
        .ExecuteBufferedAsync();
    if (result.ExitCode != 0)
        return Results.BadRequest(new { error = result.StandardError.Trim() });
    return Results.Ok(new { size = result.StandardOutput.Trim() + " MB" });
});

// Database query execution
app.MapPost("/api/databases/{name}/query", async (string name, HttpContext ctx, BinaryManager bm) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var sql = body?.GetValueOrDefault("sql") ?? "";
    if (string.IsNullOrWhiteSpace(sql))
        return Results.BadRequest(new { error = "sql required" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
    var result = await CliWrap.Cli.Wrap(mysqlCli)
        .WithArguments($"-h 127.0.0.1 -P 3306 -u root {name} -e \"{sql.Replace("\"", "\\\"")}\"")
        .WithValidation(CliWrap.CommandResultValidation.None)
        .ExecuteBufferedAsync();
    if (result.ExitCode != 0)
        return Results.BadRequest(new { error = result.StandardError.Trim() });
    // Parse tab-separated output to JSON
    var lines = result.StandardOutput.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length == 0)
        return Results.Ok(new { rows = Array.Empty<object>(), message = "Query executed successfully (no output)" });
    var headers = lines[0].Split('\t').Select(h => h.Trim()).ToArray();
    var rows = lines.Skip(1).Select(l =>
    {
        var vals = l.Split('\t').Select(v => v.Trim()).ToArray();
        var row = new Dictionary<string, string>();
        for (int i = 0; i < headers.Length && i < vals.Length; i++)
            row[headers[i]] = vals[i];
        return row;
    }).ToList();
    return Results.Ok(new { columns = headers, rows, rowCount = rows.Count });
});

// Database export (mysqldump)
app.MapGet("/api/databases/{name}/export", async (string name, BinaryManager bm) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqldump = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysqldump.exe");
    if (!File.Exists(mysqldump))
        return Results.BadRequest(new { error = "mysqldump.exe not found" });
    var result = await CliWrap.Cli.Wrap(mysqldump)
        .WithArguments($"-h 127.0.0.1 -P 3306 -u root {name}")
        .WithValidation(CliWrap.CommandResultValidation.None)
        .ExecuteBufferedAsync();
    if (result.ExitCode != 0)
        return Results.BadRequest(new { error = result.StandardError.Trim() });
    return Results.Text(result.StandardOutput, "application/sql");
});

// Database import (mysql < file)
app.MapPost("/api/databases/{name}/import", async (string name, HttpContext ctx, BinaryManager bm) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");

    // Read uploaded SQL file or raw body
    string sql;
    if (ctx.Request.HasFormContentType)
    {
        var form = await ctx.Request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        if (file is null) return Results.BadRequest(new { error = "No file uploaded" });
        using var reader = new StreamReader(file.OpenReadStream());
        sql = await reader.ReadToEndAsync();
    }
    else
    {
        sql = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    }

    var tmpFile = Path.GetTempFileName();
    await File.WriteAllTextAsync(tmpFile, sql);
    try
    {
        var result = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments($"-h 127.0.0.1 -P 3306 -u root {name}")
            .WithStandardInputPipe(CliWrap.PipeSource.FromFile(tmpFile))
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();
        return result.ExitCode == 0
            ? Results.Ok(new { ok = true, message = "Import completed" })
            : Results.BadRequest(new { error = result.StandardError.Trim() });
    }
    finally
    {
        File.Delete(tmpFile);
    }
});

// ── Binary catalog + installation management ──────────────────────────────
// GET /api/binaries/catalog          → all known releases
// GET /api/binaries/catalog/{app}    → releases for one app
// GET /api/binaries/installed        → currently installed binaries
// GET /api/binaries/installed/{app}  → installed versions for one app
// POST /api/binaries/install         → { app, version } downloads + extracts
// DELETE /api/binaries/{app}/{version} → uninstall

app.MapGet("/api/binaries/catalog", (CatalogClient cc) =>
    Results.Ok(cc.CachedReleases));

app.MapGet("/api/binaries/catalog/{app}", (string app, CatalogClient cc) =>
    Results.Ok(cc.ForApp(app)));

app.MapPost("/api/binaries/catalog/refresh", async (CatalogClient cc, CancellationToken ct) =>
{
    var count = await cc.RefreshAsync(ct);
    return Results.Ok(new { count, lastFetch = cc.LastFetch });
});

app.MapGet("/api/binaries/installed", (BinaryManager bm) =>
    Results.Ok(bm.ListInstalled()));

app.MapGet("/api/binaries/installed/{app}", (string app, BinaryManager bm) =>
    Results.Ok(bm.ListInstalled(app)));

app.MapPost("/api/binaries/install", async (HttpContext ctx, BinaryManager bm) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<InstallBinaryRequest>();
    if (req is null || string.IsNullOrWhiteSpace(req.App) || string.IsNullOrWhiteSpace(req.Version))
        return Results.BadRequest(new { error = "app and version required" });

    try
    {
        BinaryManager.ValidateAppVersion(req.App, req.Version);
        var installed = await bm.EnsureInstalledAsync(req.App, req.Version, progress: null, ct: ctx.RequestAborted);
        return Results.Ok(installed);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) { return Results.Problem($"Install failed: {ex.Message}"); }
});

app.MapDelete("/api/binaries/{app}/{version}", (string app, string version, BinaryManager bm) =>
{
    try
    {
        BinaryManager.ValidateAppVersion(app, version);
        bm.Uninstall(app, version);
        return Results.NoContent();
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// SSE endpoint
app.MapGet("/api/events", async (HttpContext ctx, SseService sse) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var clientId = sse.AddClient(ctx.Response);

    // Send initial connected event
    await ctx.Response.WriteAsync($"event: connected\ndata: {{\"clientId\":\"{clientId}\"}}\n\n");
    await ctx.Response.Body.FlushAsync();

    // Keep connection alive until client disconnects
    try
    {
        await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
    }
    catch (TaskCanceledException) { }
    finally
    {
        sse.RemoveClient(clientId);
    }
});

// Graceful shutdown — stop all services + cleanup
app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Services.GetRequiredService<ShutdownCoordinator>()
        .StopAllAsync(
            app.Services.GetServices<IServiceModule>(),
            pluginLoader.Plugins,
            portFile,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();
});

// Route uncaught exceptions through Sentry (no-op if SDK wasn't initialised).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
        Sentry.SentrySdk.CaptureException(ex);
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Sentry.SentrySdk.CaptureException(e.Exception);
    e.SetObserved();
};

await app.RunAsync();

record ConfigValidateRequest(string ConfigPath, string? Content, string? ServiceId);
record ServiceConfigWriteRequest(string Path, string? Content);
record InstallBinaryRequest(string App, string Version);
