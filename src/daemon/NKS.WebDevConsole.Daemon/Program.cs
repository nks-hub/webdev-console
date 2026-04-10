using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// CORS for Electron renderer
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddSingleton<SseService>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddHostedService<HealthMonitor>();
builder.Services.AddSingleton<PluginLoader>();
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

var app = builder.Build();
app.UseCors();

// Load plugins
var pluginLoader = app.Services.GetRequiredService<PluginLoader>();
var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
// Also check relative path for development
if (!Directory.Exists(pluginDir))
    pluginDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "plugins"));
pluginLoader.LoadPlugins(pluginDir);

// Initialize plugins
foreach (var p in pluginLoader.Plugins)
{
    await p.Module.InitializeAsync(CancellationToken.None);
}

// Write port file so Electron can discover us
var url = app.Urls.FirstOrDefault() ?? "http://localhost:5173";
var port = new Uri(url.Replace("+", "localhost")).Port;
var portFile = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
await File.WriteAllTextAsync(portFile, port.ToString());
Console.WriteLine($"[daemon] port file: {portFile}");

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
        id = p.Module.Id,
        name = p.Module.Name,
        version = p.Module.Version,
        isService = p.Module is IServicePlugin
    })
));

app.MapGet("/api/plugins/{id}/ui", (string id) =>
{
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Module.Id == id);
    if (plugin == null) return Results.NotFound();
    // Stub UI schema - plugins will provide this via IFrontendPanelProvider later
    return Results.Ok(new
    {
        pluginId = id,
        category = "Services",
        icon = "el-icon-setting",
        panels = new[]
        {
            new { type = "service-status-card", props = new { serviceId = id } },
            new { type = "log-viewer", props = new { serviceId = id } }
        }
    });
});

// Service management endpoints
app.MapGet("/api/services", (ProcessManager pm) =>
    Results.Ok(pm.GetAllStatuses()));

app.MapGet("/api/services/{id}", (string id, ProcessManager pm) =>
    Results.Ok(pm.GetStatus(id)));

app.MapPost("/api/services/{id}/start", (string id, ProcessManager pm) =>
{
    // Plugin will provide the actual executable path
    return Results.Ok(new { message = $"Start {id} via plugin" });
});

app.MapPost("/api/services/{id}/stop", async (string id, ProcessManager pm) =>
{
    var result = await pm.StopAsync(id);
    return result ? Results.Ok(new { message = "stopped" }) : Results.NotFound();
});

app.MapPost("/api/services/{id}/restart", async (string id, ProcessManager pm) =>
{
    await pm.StopAsync(id);
    return Results.Ok(new { message = $"Restart {id} via plugin" });
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
