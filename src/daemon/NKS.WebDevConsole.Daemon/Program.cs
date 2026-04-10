using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Core.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// CORS for Electron renderer
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddSingleton<SseService>();
builder.Services.AddSingleton<PluginLoader>();

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
