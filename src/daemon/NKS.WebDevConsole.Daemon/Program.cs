using Dapper;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using CliWrap.Buffered;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// CORS for Electron renderer
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000", "app://.")
              .AllowAnyMethod().AllowAnyHeader());
});

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

// Call Initialize on each plugin so they can register their services into the DI container
var initContext = PluginContext.ForInitPhase(earlyLoggerFactory);
foreach (var p in pluginLoader.Plugins)
{
    p.Instance.Initialize(builder.Services, initContext);
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

// Refresh binary catalog from the (mock) catalog API before plugins start so they can use it
var catalogClient = app.Services.GetRequiredService<CatalogClient>();
await catalogClient.RefreshAsync();

// Phase 2: Start plugins with the fully-built service provider
var pluginContext = new PluginContext(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>());

foreach (var p in pluginLoader.Plugins)
{
    await p.Instance.StartAsync(pluginContext, CancellationToken.None);
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

// Auth middleware for /api/* requests
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        var queryToken = ctx.Request.Query["token"].FirstOrDefault();
        var provided = auth?.Replace("Bearer ", "") ?? queryToken;
        if (provided != authToken)
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

app.MapGet("/api/plugins", (IServiceProvider sp) =>
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
            enabled = true,
            description = $"{p.Instance.DisplayName} plugin",
        };
    }));
});

app.MapGet("/api/plugins/{id}/ui", (string id, IServiceProvider sp) =>
{
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null) return Results.NotFound();

    // Check if the plugin implements IFrontendPanelProvider
    if (plugin.Instance is IFrontendPanelProvider uiProvider)
    {
        var def = uiProvider.GetUiDefinition();
        return Results.Ok(new
        {
            pluginId = def.PluginId,
            category = def.Category,
            icon = def.Icon,
            panels = def.Panels.Select(p => new { type = p.Type, props = p.Props })
        });
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

app.MapPost("/api/services/{id}/start", async (string id, IServiceProvider sp) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

    await module.StartAsync(CancellationToken.None);
    var status = await module.GetStatusAsync(CancellationToken.None);
    return Results.Ok(status);
});

app.MapPost("/api/services/{id}/stop", async (string id, IServiceProvider sp) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

    await module.StopAsync(CancellationToken.None);
    var status = await module.GetStatusAsync(CancellationToken.None);
    return Results.Ok(status);
});

app.MapPost("/api/services/{id}/restart", async (string id, IServiceProvider sp) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

    await module.StopAsync(CancellationToken.None);
    await module.StartAsync(CancellationToken.None);
    var status = await module.GetStatusAsync(CancellationToken.None);
    return Results.Ok(status);
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
    if (string.IsNullOrWhiteSpace(site.Domain))
        return Results.BadRequest(new { error = "Domain is required" });
    if (sm.Get(site.Domain) is not null)
        return Results.Conflict(new { error = $"Site {site.Domain} already exists" });
    var created = await sm.CreateAsync(site);
    await orchestrator.ApplyAsync(created);
    return Results.Created($"/api/sites/{created.Domain}", created);
});

app.MapPut("/api/sites/{domain}", async (string domain, SiteConfig site, SiteManager sm) =>
{
    if (sm.Get(domain) is null)
        return Results.NotFound();
    site.Domain = domain;
    var updated = await sm.UpdateAsync(site);
    return Results.Ok(updated);
});

app.MapDelete("/api/sites/{domain}", async (string domain, SiteManager sm, SiteOrchestrator orchestrator) =>
{
    if (!sm.Delete(domain)) return Results.NotFound();
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
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site {domain} not found" });

    var historyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "generated", "history");
    var historyFile = Path.Combine(historyDir, $"{domain}.conf.{timestamp}");
    if (!File.Exists(historyFile))
        return Results.NotFound(new { error = $"History entry {timestamp} not found" });

    var generatedFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "generated", $"{domain}.conf");

    File.Copy(historyFile, generatedFile, overwrite: true);
    // Re-apply through orchestrator so vhost is also written next to Apache binary
    await orchestrator.ApplyAsync(site);
    return Results.Ok(new { domain, restoredFrom = timestamp });
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

// Config validation (Apache)
app.MapPost("/api/config/validate", async (HttpContext ctx, ConfigValidator validator) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ConfigValidateRequest>();
    if (body == null) return Results.BadRequest();
    var apachePlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.apache");
    string httpdPath = @"C:\MAMP\bin\apache\bin\httpd.exe"; // fallback
    if (apachePlugin != null)
    {
        var prop = apachePlugin.Instance.GetType().GetProperty("HttpdPath");
        if (prop != null) httpdPath = (string?)prop.GetValue(apachePlugin.Instance) ?? httpdPath;
    }
    var (isValid, output) = await validator.ValidateApacheConfig(httpdPath, body.ConfigPath);
    return Results.Ok(new { isValid, output });
});

// Plugin enable/disable stubs
app.MapPost("/api/plugins/{id}/enable", (string id) =>
    Results.Ok(new { id, enabled = true }));

app.MapPost("/api/plugins/{id}/disable", (string id) =>
    Results.Ok(new { id, enabled = false }));

// SSL certificates
app.MapGet("/api/ssl/certs", () =>
{
    var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
    if (sslPlugin == null) return Results.Ok(Array.Empty<object>());
    var method = sslPlugin.Instance.GetType().GetMethod("GetCerts");
    if (method != null)
    {
        var certs = method.Invoke(sslPlugin.Instance, null);
        return Results.Ok(certs);
    }
    return Results.Ok(Array.Empty<object>());
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
    if (string.IsNullOrWhiteSpace(dbName))
        return Results.BadRequest(new { error = "Database name required" });

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
        var installed = await bm.EnsureInstalledAsync(req.App, req.Version, progress: null, ct: ctx.RequestAborted);
        return Results.Ok(installed);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Install failed: {ex.Message}");
    }
});

app.MapDelete("/api/binaries/{app}/{version}", (string app, string version, BinaryManager bm) =>
{
    bm.Uninstall(app, version);
    return Results.NoContent();
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

// Cleanup port file on shutdown
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { File.Delete(portFile); } catch { }
});

await app.RunAsync();

record ConfigValidateRequest(string ConfigPath, string? Content);
record InstallBinaryRequest(string App, string Version);
