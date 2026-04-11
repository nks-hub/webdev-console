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
builder.Services.AddSingleton<SseService>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddHostedService<HealthMonitor>();
builder.Services.AddSingleton<TemplateEngine>();
builder.Services.AddSingleton<ConfigValidator>();
builder.Services.AddSingleton<AtomicWriter>();
builder.Services.AddSingleton(sp => new SiteManager(
    sp.GetRequiredService<ILogger<SiteManager>>(),
    sp.GetRequiredService<TemplateEngine>(),
    sp.GetRequiredService<ConfigValidator>(),
    sp.GetRequiredService<AtomicWriter>(),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wdc", "sites"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wdc", "generated")
));
builder.Services.AddSingleton<SiteOrchestrator>();
builder.Services.AddSingleton<MampMigrator>();
builder.Services.AddSingleton<BackupManager>();

// Binary catalog / downloader / manager — own binaries under ~/.wdc/binaries/
builder.Services.AddHttpClient("binary-downloader");
builder.Services.AddHttpClient("catalog-client");
builder.Services.AddSingleton<CatalogClientOptions>(sp =>
{
    // FUTURE: read from settings table or environment
    var url = Environment.GetEnvironmentVariable("NKS_WDC_CATALOG_URL")
        ?? "http://127.0.0.1:8765";
    return new CatalogClientOptions { BaseUrl = url };
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
var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wdc", "data", "state.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var database = new Database(dbPath);
builder.Services.AddSingleton(database);

var migrationRunner = new MigrationRunner(earlyLoggerFactory.CreateLogger<MigrationRunner>());
migrationRunner.Run(database.ConnectionString);

var app = builder.Build();
app.UseCors();

// Expose OpenAPI spec at /openapi/v1.json — used by CI TS type generation
app.MapOpenApi();

// Refresh binary catalog from the (mock) catalog API before plugins start so they can use it
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
        return new
        {
            id = p.Instance.Id,
            name = p.Instance.DisplayName,
            version = p.Instance.Version,
            type = hasService ? "service" : "tool",
            enabled = pluginState.IsEnabled(p.Instance.Id),
            description = $"{p.Instance.DisplayName} plugin",
        };
    }));
});

// Plugin marketplace — Phase 5 plan item.
// Fetches a JSON manifest from a configurable URL (NKS_WDC_MARKETPLACE_URL env or default)
// and returns the list of available plugins. Cross-references installed plugin ids so the
// UI can mark entries as "installed" / "update available". Graceful fallback to an empty
// list if the remote manifest is unreachable — the feature is best-effort, not critical path.
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
            return Results.Ok(new
            {
                source = marketplaceUrl,
                reachable = false,
                plugins = Array.Empty<object>(),
                error = $"Marketplace returned HTTP {(int)response.StatusCode}"
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
        return Results.Ok(new
        {
            source = marketplaceUrl,
            reachable = false,
            plugins = Array.Empty<object>(),
            error = ex.Message
        });
    }
});

app.MapGet("/api/plugins/{id}/ui", (string id, IServiceProvider sp) =>
{
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null) return Results.NotFound();

    try
    {
        // Check if the plugin implements IFrontendPanelProvider via reflection (cross-ALC)
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
                return Results.Ok(new
                {
                    pluginId = pidProp?.GetValue(def)?.ToString() ?? id,
                    category = catProp?.GetValue(def)?.ToString() ?? "Services",
                    icon = iconProp?.GetValue(def)?.ToString() ?? "el-icon-setting",
                    panels = new[] { new { type = "service-status-card", props = (object)new { serviceId = id } } }
                });
            }
        }
    }
    catch { /* cross-ALC type mismatch — fallback */ }

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

    var cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "cache", "plugin-installs");
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

// Auto-start services if setting enabled (default: true)
var autoStartEnabled = true; // TODO: read from settings DB
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

    var generatedDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "generated", "history");
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

    var historyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "generated", "history");
    var historyDirFull = Path.GetFullPath(historyDir);
    var historyFile = Path.GetFullPath(Path.Combine(historyDir, $"{domain}.conf.{timestamp}"));
    if (!historyFile.StartsWith(historyDirFull, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Resolved history path escapes history root" });
    if (!File.Exists(historyFile))
        return Results.NotFound(new { error = $"History entry {timestamp} not found" });

    var generatedRoot = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "generated"));
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
    var flagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "data", "onboarding-complete.flag");

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
    var flagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "data", "onboarding-complete.flag");
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
        var backupRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wdc", "backups"));
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
            var backupRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".wdc", "backups"));
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
app.MapGet("/api/services/{id}/config", async (string id) =>
{
    var wdcHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wdc");
    var configs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["apache"] = new[] {
            Path.Combine(wdcHome, "binaries", "apache"),
        },
        ["mysql"] = new[] {
            Path.Combine(wdcHome, "data", "mysql", "my.ini"),
            Path.Combine(wdcHome, "binaries", "mysql"),
        },
        ["php"] = new[] {
            Path.Combine(wdcHome, "binaries", "php"),
        },
        ["redis"] = new[] {
            Path.Combine(wdcHome, "binaries", "redis"),
        },
    };

    var files = new List<object>();

    // Find actual config files
    if (id.Equals("apache", StringComparison.OrdinalIgnoreCase))
    {
        // httpd.conf + all vhost configs
        var apacheRoot = Directory.GetDirectories(Path.Combine(wdcHome, "binaries", "apache"))
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderByDescending(d => d)
            .FirstOrDefault();
        if (apacheRoot != null)
        {
            var httpdConf = Path.Combine(apacheRoot, "conf", "httpd.conf");
            if (File.Exists(httpdConf))
                files.Add(new { name = "httpd.conf", path = httpdConf, content = await File.ReadAllTextAsync(httpdConf) });

            var vhostsDir = Path.Combine(apacheRoot, "conf", "sites-enabled");
            if (Directory.Exists(vhostsDir))
                foreach (var f in Directory.GetFiles(vhostsDir, "*.conf"))
                    files.Add(new { name = Path.GetFileName(f), path = f, content = await File.ReadAllTextAsync(f) });
        }
    }
    else if (id.Equals("php", StringComparison.OrdinalIgnoreCase))
    {
        var phpRoot = Path.Combine(wdcHome, "binaries", "php");
        if (Directory.Exists(phpRoot))
            foreach (var vdir in Directory.GetDirectories(phpRoot).Where(d => !Path.GetFileName(d).StartsWith('.')))
            {
                var ini = Path.Combine(vdir, "php.ini");
                if (File.Exists(ini))
                    files.Add(new { name = $"php.ini ({Path.GetFileName(vdir)})", path = ini, content = await File.ReadAllTextAsync(ini) });
            }
    }
    else if (id.Equals("mysql", StringComparison.OrdinalIgnoreCase))
    {
        var myIni = Path.Combine(wdcHome, "data", "mysql", "my.ini");
        if (File.Exists(myIni))
            files.Add(new { name = "my.ini", path = myIni, content = await File.ReadAllTextAsync(myIni) });
    }

    return Results.Ok(new { serviceId = id, files });
});

// Config validation — dispatches to Apache / PHP / MySQL / Redis based on serviceId.
// Frontend sends serviceId from the ServiceConfig.vue editor; legacy callers that
// omit serviceId get Apache validation (backwards compat with the original endpoint).
app.MapPost("/api/config/validate", async (HttpContext ctx, ConfigValidator validator, BinaryManager bm) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ConfigValidateRequest>();
    if (body == null) return Results.BadRequest();

    var service = (body.ServiceId ?? "apache").ToLowerInvariant();

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
                return Results.BadRequest(new { error = "Apache httpd not found in managed binaries — install it first via POST /api/binaries/install" });
            var (isValid, output) = await validator.ValidateApacheConfig(httpdPath, body.ConfigPath);
            return Results.Ok(new { isValid, output });
        }
        case "php":
        {
            var phpPath = bm.ListInstalled("php").FirstOrDefault()?.Executable;
            if (string.IsNullOrEmpty(phpPath) || !File.Exists(phpPath))
                return Results.BadRequest(new { error = "PHP not found in managed binaries" });
            var (isValid, output) = await validator.ValidatePhpIni(phpPath, body.ConfigPath);
            return Results.Ok(new { isValid, output });
        }
        case "mysql":
        case "mariadb":
        {
            var mysqldPath = bm.ListInstalled("mysql").FirstOrDefault()?.Executable;
            if (string.IsNullOrEmpty(mysqldPath) || !File.Exists(mysqldPath))
                return Results.BadRequest(new { error = "mysqld not found in managed binaries" });
            var (isValid, output) = await validator.ValidateMyCnf(mysqldPath, body.ConfigPath);
            return Results.Ok(new { isValid, output });
        }
        case "redis":
        {
            var redisPath = bm.ListInstalled("redis").FirstOrDefault()?.Executable;
            if (string.IsNullOrEmpty(redisPath) || !File.Exists(redisPath))
                return Results.BadRequest(new { error = "redis-server not found in managed binaries" });
            var (isValid, output) = await validator.ValidateRedisConf(redisPath, body.ConfigPath);
            return Results.Ok(new { isValid, output });
        }
        default:
            return Results.BadRequest(new { error = $"Unknown serviceId '{body.ServiceId}'. Use apache | php | mysql | redis." });
    }
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
app.Lifetime.ApplicationStopping.Register(async () =>
{
    Console.WriteLine("[shutdown] Stopping all services...");
    var modules = app.Services.GetServices<IServiceModule>();
    foreach (var module in modules)
    {
        try
        {
            var status = await module.GetStatusAsync(CancellationToken.None);
            if (status.State == ServiceState.Running)
            {
                await module.StopAsync(CancellationToken.None);
                Console.WriteLine($"[shutdown] {module.ServiceId}: stopped");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[shutdown] {module.ServiceId}: {ex.Message}");
        }
    }

    // Stop plugins
    foreach (var p in pluginLoader.Plugins)
    {
        try { await p.Instance.StopAsync(CancellationToken.None); }
        catch { }
    }

    try { File.Delete(portFile); } catch { }
    Console.WriteLine("[shutdown] Complete");
});

await app.RunAsync();

record ConfigValidateRequest(string ConfigPath, string? Content, string? ServiceId);
record InstallBinaryRequest(string App, string Version);
