using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:50051");

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();

var startedAt = DateTime.UtcNow;
int? managedPid = null;

// Write port file so Electron can discover us
var portFile = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
await File.WriteAllTextAsync(portFile, "50051");
Console.WriteLine($"[daemon] port file: {portFile}");

app.MapGet("/api/status", () =>
{
    var uptime = (DateTime.UtcNow - startedAt).TotalSeconds;
    return Results.Ok(new
    {
        version = "0.1.0-poc",
        uptime = Math.Round(uptime, 1),
        services = new[]
        {
            new
            {
                name = "notepad",
                status = managedPid.HasValue && !IsProcessDead(managedPid.Value)
                    ? "running"
                    : "stopped",
                pid = managedPid
            }
        }
    });
});

app.MapPost("/api/service/start", () =>
{
    if (managedPid.HasValue && !IsProcessDead(managedPid.Value))
        return Results.Ok(new { ok = false, message = "already running", pid = managedPid });

    var proc = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
    managedPid = proc?.Id;
    Console.WriteLine($"[daemon] started notepad pid={managedPid}");
    return Results.Ok(new { ok = true, message = "started", pid = managedPid });
});

app.MapPost("/api/service/stop", () =>
{
    if (!managedPid.HasValue)
        return Results.Ok(new { ok = false, message = "not running" });

    try
    {
        var proc = Process.GetProcessById(managedPid.Value);
        proc.Kill();
        Console.WriteLine($"[daemon] killed pid={managedPid}");
        managedPid = null;
        return Results.Ok(new { ok = true, message = "stopped" });
    }
    catch
    {
        managedPid = null;
        return Results.Ok(new { ok = true, message = "already dead" });
    }
});

Console.WriteLine("[daemon] listening on http://localhost:50051");
Console.CancelKeyPress += (_, _) => File.Delete(portFile);

await app.RunAsync();

static bool IsProcessDead(int pid)
{
    try { Process.GetProcessById(pid); return false; }
    catch { return true; }
}
