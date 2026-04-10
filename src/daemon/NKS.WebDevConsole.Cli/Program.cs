using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http.Json;
using System.Text.Json;
using NKS.WebDevConsole.Cli;
using Spectre.Console;

var jsonOption = new Option<bool>("--json") { Description = "Output raw JSON instead of formatted tables" };

var rootCommand = new RootCommand("NKS WebDev Console CLI") { jsonOption };

// --- wdc status ---
var statusCommand = new Command("status", "Show daemon and service status");
statusCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var status = await client.GetJsonAsync("/api/status");
    var services = await client.GetJsonAsync("/api/services");

    if (json) { PrintJson(new { status, services }); return; }

    AnsiConsole.MarkupLine($"[bold]Daemon[/]  [green]running[/]  v{status.GetProperty("version").GetString()}");
    AnsiConsole.MarkupLine($"Plugins: {status.GetProperty("plugins").GetInt32()}  Uptime: {FormatUptime(status.GetProperty("uptime").GetInt64())}");
    AnsiConsole.WriteLine();

    if (services.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No services registered.[/]"); return; }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Service"); table.AddColumn("State"); table.AddColumn("PID"); table.AddColumn("Memory");
    foreach (var svc in services.EnumerateArray())
    {
        var stateStr = svc.GetProperty("state").ValueKind == JsonValueKind.Number
            ? StateNumToStr(svc.GetProperty("state").GetInt32())
            : svc.GetProperty("state").GetString() ?? "unknown";
        table.AddRow(
            svc.GetProperty("id").GetString() ?? "?",
            FormatState(stateStr),
            GetInt(svc, "pid")?.ToString() ?? "-",
            GetLong(svc, "memoryBytes") is long m ? FormatBytes(m) : "-");
    }
    AnsiConsole.Write(table);
});

// --- wdc services ---
var servicesCommand = new Command("services", "List all services");
servicesCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var services = await client.GetJsonAsync("/api/services");
    if (json) { PrintJson(services); return; }
    if (services.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No services registered.[/]"); return; }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("ID"); table.AddColumn("Name"); table.AddColumn("State");
    table.AddColumn("PID"); table.AddColumn("CPU %"); table.AddColumn("Memory");
    foreach (var svc in services.EnumerateArray())
    {
        var sState = svc.GetProperty("state").ValueKind == JsonValueKind.Number
            ? StateNumToStr(svc.GetProperty("state").GetInt32())
            : svc.GetProperty("state").GetString() ?? "unknown";
        table.AddRow(
            svc.GetProperty("id").GetString() ?? "?",
            svc.GetProperty("displayName").GetString() ?? "-",
            FormatState(sState),
            GetInt(svc, "pid")?.ToString() ?? "-",
            GetDouble(svc, "cpuPercent") is double c ? $"{c:F1}%" : "-",
            GetLong(svc, "memoryBytes") is long m ? FormatBytes(m) : "-");
    }
    AnsiConsole.Write(table);
});

// --- wdc services start/stop/restart {id} ---
foreach (var (verb, label, color) in new[] { ("start", "Started", "green"), ("stop", "Stopped", "yellow"), ("restart", "Restarted", "blue") })
{
    var idArg = new Argument<string>("id");
    var cmd = new Command(verb, $"{verb} a service") { idArg };
    cmd.SetAction(async (parseResult, ct) =>
    {
        var id = parseResult.GetValue(idArg)!;
        var json = parseResult.GetValue(jsonOption);
        using var client = new DaemonClient();
        if (!EnsureConnected(client)) return;

        try
        {
            var result = await client.PostAsync($"/api/services/{id}/{verb}");
            if (json) { PrintJson(result); return; }
            AnsiConsole.MarkupLine($"[{color}]>>>[/] {Markup.Escape(label)}");
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/] {Markup.Escape(ex.Message)}");
        }
    });
    servicesCommand.Add(cmd);
}

// --- wdc sites ---
var sitesCommand = new Command("sites", "List all sites");
sitesCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var sites = await client.GetJsonAsync("/api/sites");
    if (json) { PrintJson(sites); return; }
    if (sites.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No sites configured.[/]"); return; }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Domain"); table.AddColumn("PHP"); table.AddColumn("SSL"); table.AddColumn("Framework");
    foreach (var site in sites.EnumerateArray())
    {
        var ssl = site.TryGetProperty("sslEnabled", out var s) && s.GetBoolean();
        var fw = site.TryGetProperty("framework", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        table.AddRow(
            site.GetProperty("domain").GetString() ?? "?",
            site.GetProperty("phpVersion").GetString() ?? "-",
            ssl ? "[green]Yes[/]" : "[dim]No[/]",
            fw != null ? Markup.Escape(fw) : "[dim]-[/]");
    }
    AnsiConsole.Write(table);
});

// --- wdc sites delete {domain} ---
var deleteDomainArg = new Argument<string>("domain");
var deleteSiteCmd = new Command("delete", "Delete a site") { deleteDomainArg };
deleteSiteCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(deleteDomainArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    if (!json && !AnsiConsole.Confirm($"Delete [red]{Markup.Escape(domain)}[/]?", false)) return;

    await client.DeleteAsync($"/api/sites/{domain}");
    if (json) PrintJson(new { deleted = domain });
    else AnsiConsole.MarkupLine($"[yellow]Deleted:[/] {Markup.Escape(domain)}");
});
sitesCommand.Add(deleteSiteCmd);

// --- wdc sites create ---
var createSiteCmd = new Command("create", "Create a new site (interactive)");
createSiteCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var domain = AnsiConsole.Ask<string>("Domain (e.g. [green]myapp.loc[/]):");
    var docRoot = AnsiConsole.Ask<string>("Document root:");
    var php = AnsiConsole.Ask("PHP version:", "8.4");
    var ssl = AnsiConsole.Confirm("Enable SSL?", false);

    var payload = new
    {
        domain,
        documentRoot = docRoot,
        phpVersion = php,
        sslEnabled = ssl,
        httpPort = 80,
        httpsPort = 443,
        aliases = Array.Empty<string>(),
        environment = new Dictionary<string, string>()
    };

    var content = JsonContent.Create(payload);
    var result = await client.PostAsync("/api/sites", content);

    if (json) { PrintJson(result); return; }
    AnsiConsole.MarkupLine($"[green]Site created:[/] {Markup.Escape(domain)}");
});
sitesCommand.Add(createSiteCmd);

// --- wdc plugins ---
var pluginsCommand = new Command("plugins", "List loaded plugins");
pluginsCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var plugins = await client.GetJsonAsync("/api/plugins");
    if (json) { PrintJson(plugins); return; }
    if (plugins.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No plugins loaded.[/]"); return; }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("ID"); table.AddColumn("Name"); table.AddColumn("Version");
    foreach (var p in plugins.EnumerateArray())
        table.AddRow(p.GetProperty("id").GetString() ?? "?", p.GetProperty("name").GetString() ?? "-", p.GetProperty("version").GetString() ?? "-");
    AnsiConsole.Write(table);
});

// --- wdc version ---
var versionCommand = new Command("version", "Show version info");
versionCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var cliVersion = typeof(DaemonClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    using var client = new DaemonClient();
    var connected = client.Connect();
    string? daemonVer = null;

    if (connected)
        try { daemonVer = (await client.GetJsonAsync("/api/status")).GetProperty("version").GetString(); } catch { }

    if (json) { PrintJson(new { cli = cliVersion, daemon = daemonVer }); return; }

    AnsiConsole.MarkupLine($"[bold]wdc CLI[/]    v{cliVersion}");
    AnsiConsole.MarkupLine(daemonVer != null
        ? $"[bold]Daemon[/]     v{daemonVer} [green](connected)[/]"
        : "[bold]Daemon[/]     [dim]not running[/]");
});

// --- wdc databases ---
var dbCommand = new Command("databases", "List and manage MySQL databases");
dbCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var result = await client.GetJsonAsync("/api/databases");
    if (json) { PrintJson(result); return; }

    if (result.TryGetProperty("databases", out var dbs) && dbs.GetArrayLength() > 0)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Database");
        foreach (var db in dbs.EnumerateArray())
            table.AddRow(db.GetString() ?? "?");
        AnsiConsole.Write(table);
    }
    else
    {
        AnsiConsole.MarkupLine("[dim]No user databases found.[/]");
    }
});

var createDbArg = new Argument<string>("name");
var createDbCmd = new Command("create", "Create a database") { createDbArg };
createDbCmd.SetAction(async (parseResult, ct) =>
{
    var name = parseResult.GetValue(createDbArg)!;
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var content = JsonContent.Create(new { name });
    await client.PostAsync("/api/databases", content);
    AnsiConsole.MarkupLine($"[green]Database created:[/] {Markup.Escape(name)}");
});
dbCommand.Add(createDbCmd);

var dropDbArg = new Argument<string>("name");
var dropDbCmd = new Command("drop", "Drop a database") { dropDbArg };
dropDbCmd.SetAction(async (parseResult, ct) =>
{
    var name = parseResult.GetValue(dropDbArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    if (!json && !AnsiConsole.Confirm($"Drop database [red]{Markup.Escape(name)}[/]?", false)) return;
    await client.DeleteAsync($"/api/databases/{name}");
    AnsiConsole.MarkupLine($"[yellow]Dropped:[/] {Markup.Escape(name)}");
});
dbCommand.Add(dropDbCmd);

// --- wdc binaries ---
var binariesCommand = new Command("binaries", "List installed binaries");
binariesCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var bins = await client.GetJsonAsync("/api/binaries/installed");
    if (json) { PrintJson(bins); return; }
    if (bins.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No binaries installed.[/]"); return; }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("App"); table.AddColumn("Version"); table.AddColumn("Path");
    foreach (var b in bins.EnumerateArray())
    {
        table.AddRow(
            b.GetProperty("app").GetString() ?? "?",
            b.GetProperty("version").GetString() ?? "?",
            b.GetProperty("installPath").GetString() ?? "-");
    }
    AnsiConsole.Write(table);
});

var installAppArg = new Argument<string>("app");
var installVerArg = new Argument<string>("version");
var installBinCmd = new Command("install", "Download and install a binary") { installAppArg, installVerArg };
installBinCmd.SetAction(async (parseResult, ct) =>
{
    var app = parseResult.GetValue(installAppArg)!;
    var ver = parseResult.GetValue(installVerArg)!;
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    AnsiConsole.MarkupLine($"Installing [bold]{Markup.Escape(app)}[/] {Markup.Escape(ver)}...");
    var content = JsonContent.Create(new { app, version = ver });
    var result = await client.PostAsync("/api/binaries/install", content);
    AnsiConsole.MarkupLine($"[green]Installed![/]");
    PrintJson(result);
});
binariesCommand.Add(installBinCmd);

rootCommand.Add(statusCommand);
rootCommand.Add(servicesCommand);
rootCommand.Add(sitesCommand);
rootCommand.Add(pluginsCommand);
rootCommand.Add(dbCommand);
rootCommand.Add(binariesCommand);
rootCommand.Add(versionCommand);

return await rootCommand.Parse(args).InvokeAsync();

// --- Helpers ---
static bool EnsureConnected(DaemonClient client)
{
    if (client.Connect()) return true;
    AnsiConsole.MarkupLine("[red]Daemon is not running.[/] Start it or check %TEMP%/nks-wdc-daemon.port");
    return false;
}

static void PrintJson(object obj) =>
    AnsiConsole.WriteLine(JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));

static string StateNumToStr(int state) => state switch
{
    0 => "stopped", 1 => "starting", 2 => "running",
    3 => "stopping", 4 => "crashed", 5 => "disabled", _ => "unknown"
};

static string FormatState(string state) => state.ToLowerInvariant() switch
{
    "running" => "[green]Running[/]", "stopped" => "[red]Stopped[/]",
    "starting" => "[yellow]Starting[/]", "stopping" => "[yellow]Stopping[/]",
    "crashed" => "[red bold]Crashed[/]", "disabled" => "[dim]Disabled[/]",
    _ => $"[dim]{Markup.Escape(state)}[/]"
};

static string FormatBytes(long b) => b switch
{
    < 1024 => $"{b} B", < 1024 * 1024 => $"{b / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{b / (1024.0 * 1024):F1} MB",
    _ => $"{b / (1024.0 * 1024 * 1024):F2} GB"
};

static string FormatUptime(long s) => TimeSpan.FromSeconds(s) switch
{
    var ts when ts.TotalDays >= 1 => $"{(int)ts.TotalDays}d {ts.Hours}h",
    var ts when ts.TotalHours >= 1 => $"{(int)ts.TotalHours}h {ts.Minutes}m",
    var ts => $"{(int)ts.TotalMinutes}m {ts.Seconds}s"
};

static int? GetInt(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

static long? GetLong(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

static double? GetDouble(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
