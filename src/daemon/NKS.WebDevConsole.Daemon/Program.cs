using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
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

app.MapGet("/api/plugins", () => Results.Ok(
    pluginLoader.Plugins.Select(p => new
    {
        id = p.Instance.Id,
        name = p.Instance.DisplayName,
        version = p.Instance.Version
    })
));

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

// Service management endpoints
app.MapGet("/api/services", (ProcessManager pm) =>
    Results.Ok(pm.GetAllStatuses()));

app.MapGet("/api/services/{id}", (string id, ProcessManager pm) =>
    Results.Ok(pm.GetStatus(id)));

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

app.MapGet("/api/sites", (SiteManager sm) => Results.Ok(sm.Sites.Values));

app.MapGet("/api/sites/{domain}", (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    return site is not null ? Results.Ok(site) : Results.NotFound();
});

app.MapPost("/api/sites", async (SiteConfig site, SiteManager sm) =>
{
    if (string.IsNullOrWhiteSpace(site.Domain))
        return Results.BadRequest(new { error = "Domain is required" });
    if (sm.Get(site.Domain) is not null)
        return Results.Conflict(new { error = $"Site {site.Domain} already exists" });
    var created = await sm.CreateAsync(site);
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

app.MapDelete("/api/sites/{domain}", (string domain, SiteManager sm) =>
{
    return sm.Delete(domain) ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/sites/{domain}/detect-framework", (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();
    var framework = sm.DetectFramework(site.DocumentRoot);
    return Results.Ok(new { domain, framework });
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
