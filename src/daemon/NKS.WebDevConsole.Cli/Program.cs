using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http.Json;
using System.Text.Json;
using NKS.WebDevConsole.Cli;
using Spectre.Console;

var jsonOption = new Option<bool>("--json") { Description = "Output raw JSON instead of formatted tables", Recursive = true };

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

// --- wdc logs {id} ---
var logsIdArg = new Argument<string>("id") { Description = "Service ID" };
var logsLinesOpt = new Option<int?>("--lines") { Description = "Number of lines (default: 50)" };
var logsCommand = new Command("logs", "Show service logs") { logsIdArg, logsLinesOpt };
logsCommand.SetAction(async (parseResult, ct) =>
{
    var id = parseResult.GetValue(logsIdArg)!;
    var lines = parseResult.GetValue(logsLinesOpt) ?? 50;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var result = await client.GetJsonAsync($"/api/services/{id}/logs?lines={lines}");
    if (json) { PrintJson(result); return; }

    if (result.GetArrayLength() == 0)
    {
        AnsiConsole.MarkupLine($"[dim]No logs for {Markup.Escape(id)}[/]");
        return;
    }
    foreach (var line in result.EnumerateArray())
    {
        var text = line.GetString() ?? "";
        if (text.Contains("ERR", StringComparison.OrdinalIgnoreCase))
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(text)}[/]");
        else
            AnsiConsole.WriteLine(text);
    }
});

// --- wdc services info {id} ---
var svcInfoIdArg = new Argument<string>("id");
var svcInfoCmd = new Command("info", "Show detailed info about a service") { svcInfoIdArg };
svcInfoCmd.SetAction(async (parseResult, ct) =>
{
    var id = parseResult.GetValue(svcInfoIdArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var svc = await client.GetJsonAsync($"/api/services/{id}");
    if (json) { PrintJson(svc); return; }
    var table = new Table().Border(TableBorder.Rounded).HideHeaders();
    table.AddColumn("Key"); table.AddColumn("Value");
    table.AddRow("ID", svc.GetProperty("id").GetString() ?? "?");
    table.AddRow("Name", svc.GetProperty("displayName").GetString() ?? "-");
    var st = svc.GetProperty("state").GetInt32();
    table.AddRow("State", FormatState(StateNumToStr(st)));
    table.AddRow("PID", GetInt(svc, "pid")?.ToString() ?? "-");
    table.AddRow("CPU", GetDouble(svc, "cpuPercent") is double c ? $"{c:F1}%" : "-");
    table.AddRow("Memory", GetLong(svc, "memoryBytes") is long m ? FormatBytes(m) : "-");
    if (svc.TryGetProperty("uptime", out var up)) table.AddRow("Uptime", up.GetString() ?? "-");
    AnsiConsole.Write(table);
});
servicesCommand.Add(svcInfoCmd);

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
var domainOpt = new Option<string?>("--domain") { Description = "Domain name (e.g. myapp.loc)" };
var docrootOpt = new Option<string?>("--docroot") { Description = "Document root path" };
var phpOpt = new Option<string?>("--php") { Description = "PHP version (default: 8.4)" };
var sslOpt = new Option<bool>("--ssl") { Description = "Enable SSL" };
var aliasesOpt = new Option<string?>("--aliases") { Description = "Comma-separated aliases" };
var createSiteCmd = new Command("create", "Create a new site") { domainOpt, docrootOpt, phpOpt, sslOpt, aliasesOpt };
createSiteCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var domain = parseResult.GetValue(domainOpt) ?? AnsiConsole.Ask<string>("Domain (e.g. [green]myapp.loc[/]):");
    var docRoot = parseResult.GetValue(docrootOpt) ?? AnsiConsole.Ask<string>("Document root:");
    var php = parseResult.GetValue(phpOpt) ?? "8.4";
    var ssl = parseResult.GetValue(sslOpt) || (!json && domain == null && AnsiConsole.Confirm("Enable SSL?", false));
    var aliasStr = parseResult.GetValue(aliasesOpt);

    var payload = new
    {
        domain,
        documentRoot = docRoot,
        phpVersion = php,
        sslEnabled = ssl,
        httpPort = 80,
        httpsPort = 443,
        aliases = aliasStr?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>(),
        environment = new Dictionary<string, string>()
    };

    var content = JsonContent.Create(payload);
    var result = await client.PostAsync("/api/sites", content);

    if (json) { PrintJson(result); return; }
    AnsiConsole.MarkupLine($"[green]Site created:[/] {Markup.Escape(domain)}");
});
sitesCommand.Add(createSiteCmd);

// --- wdc sites detect {domain} ---
var detectDomainArg = new Argument<string>("domain");
var detectCmd = new Command("detect", "Auto-detect framework for a site") { detectDomainArg };
detectCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(detectDomainArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var result = await client.PostAsync($"/api/sites/{domain}/detect-framework");
    if (json) { PrintJson(result); return; }
    var fw = result.TryGetProperty("framework", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
    AnsiConsole.MarkupLine(fw != null
        ? $"[green]Detected:[/] {Markup.Escape(fw)} for {Markup.Escape(domain)}"
        : $"[dim]No framework detected for {Markup.Escape(domain)}[/]");
});
sitesCommand.Add(detectCmd);

// --- wdc sites reapply ---
var reapplyCmd = new Command("reapply", "Regenerate all site vhosts and reload Apache");
reapplyCmd.SetAction(async (parseResult, ct) =>
{
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var result = await client.PostAsync("/api/sites/reapply-all");
    if (parseResult.GetValue(jsonOption)) { PrintJson(result); return; }
    if (result.GetArrayLength() > 0)
    {
        foreach (var r in result.EnumerateArray())
        {
            var domain = r.GetProperty("domain").GetString() ?? "?";
            var ok = r.TryGetProperty("ok", out var o) && o.GetBoolean();
            AnsiConsole.MarkupLine(ok ? $"[green]✓[/] {Markup.Escape(domain)}" : $"[red]✗[/] {Markup.Escape(domain)}");
        }
    }
});
sitesCommand.Add(reapplyCmd);

// --- wdc sites update {domain} ---
var updateDomainArg = new Argument<string>("domain");
var updatePhpOpt = new Option<string?>("--php") { Description = "Set PHP version" };
var updateDocrootOpt = new Option<string?>("--docroot") { Description = "Set document root" };
var updateSiteCmd = new Command("update", "Update a site") { updateDomainArg, updatePhpOpt, updateDocrootOpt };
updateSiteCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(updateDomainArg)!;
    var php = parseResult.GetValue(updatePhpOpt);
    var docroot = parseResult.GetValue(updateDocrootOpt);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var site = await client.GetJsonAsync($"/api/sites/{domain}");
    var payload = new Dictionary<string, object?>
    {
        ["domain"] = domain,
        ["documentRoot"] = docroot ?? (site.TryGetProperty("documentRoot", out var dr) ? dr.GetString() : null),
        ["phpVersion"] = php ?? (site.TryGetProperty("phpVersion", out var pv) ? pv.GetString() : null),
        ["sslEnabled"] = site.TryGetProperty("sslEnabled", out var ssl) && ssl.GetBoolean(),
        ["httpPort"] = site.TryGetProperty("httpPort", out var hp) ? hp.GetInt32() : 80,
        ["httpsPort"] = site.TryGetProperty("httpsPort", out var hps) ? hps.GetInt32() : 443,
    };
    var content = JsonContent.Create(payload);
    var result = await client.PutAsync($"/api/sites/{domain}", content);
    if (json) { PrintJson(result); return; }
    AnsiConsole.MarkupLine($"[green]Updated[/] {Markup.Escape(domain)}");
    if (php != null) AnsiConsole.MarkupLine($"  PHP → {Markup.Escape(php)}");
    if (docroot != null) AnsiConsole.MarkupLine($"  DocRoot → {Markup.Escape(docroot)}");
});
sitesCommand.Add(updateSiteCmd);

// --- wdc sites history {domain} ---
var histDomainArg = new Argument<string>("domain");
var histCmd = new Command("history", "Show config history for a site") { histDomainArg };
histCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(histDomainArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var hist = await client.GetJsonAsync($"/api/sites/{domain}/history");
    if (json) { PrintJson(hist); return; }
    if (hist.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No history entries[/]"); return; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Timestamp"); table.AddColumn("Size");
    foreach (var h in hist.EnumerateArray())
    {
        table.AddRow(
            h.GetProperty("timestamp").GetString() ?? "?",
            h.TryGetProperty("size", out var s) ? $"{s.GetInt64()} bytes" : "-");
    }
    AnsiConsole.Write(table);
});
sitesCommand.Add(histCmd);

// --- wdc sites rollback {domain} {timestamp} ---
var rbDomainArg = new Argument<string>("domain");
var rbTimestampArg = new Argument<string>("timestamp") { Description = "Timestamp from history (e.g. 20260410_143037)" };
var rollbackCmd = new Command("rollback", "Rollback a site config to a previous version") { rbDomainArg, rbTimestampArg };
rollbackCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(rbDomainArg)!;
    var ts = parseResult.GetValue(rbTimestampArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var result = await client.PostAsync($"/api/sites/{domain}/rollback/{ts}");
    if (json) { PrintJson(result); return; }
    AnsiConsole.MarkupLine($"[green]Rolled back[/] {Markup.Escape(domain)} to {Markup.Escape(ts)}");
});
sitesCommand.Add(rollbackCmd);

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

// --- wdc databases import {name} {file} ---
var importDbNameArg = new Argument<string>("name") { Description = "Database name" };
var importFileArg = new Argument<string>("file") { Description = "SQL file path" };
var importDbCmd = new Command("import", "Import SQL file into database") { importDbNameArg, importFileArg };
importDbCmd.SetAction(async (parseResult, ct) =>
{
    var name = parseResult.GetValue(importDbNameArg)!;
    var file = parseResult.GetValue(importFileArg)!;
    if (!File.Exists(file)) { AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(file)}"); return; }

    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    // Get mysql.exe path from binaries
    var bins = await client.GetJsonAsync("/api/binaries/installed");
    string? mysqlCli = null;
    foreach (var b in bins.EnumerateArray())
    {
        if (b.GetProperty("app").GetString() == "mysql" && b.TryGetProperty("executable", out var exe))
        {
            var dir = Path.GetDirectoryName(exe.GetString());
            if (dir != null) mysqlCli = Path.Combine(dir, "mysql.exe");
        }
    }
    if (mysqlCli == null || !File.Exists(mysqlCli)) { AnsiConsole.MarkupLine("[red]MySQL client not found[/]"); return; }

    AnsiConsole.MarkupLine($"Importing [blue]{Markup.Escape(file)}[/] into [blue]{Markup.Escape(name)}[/]...");
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = mysqlCli,
        Arguments = $"-h 127.0.0.1 -P 3306 -u root {name}",
        RedirectStandardInput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var proc = System.Diagnostics.Process.Start(psi);
    if (proc != null)
    {
        await using var input = proc.StandardInput;
        await input.WriteAsync(await File.ReadAllTextAsync(file));
        await proc.WaitForExitAsync();
        if (proc.ExitCode == 0) AnsiConsole.MarkupLine("[green]Import complete[/]");
        else AnsiConsole.MarkupLine($"[red]Import failed (exit {proc.ExitCode})[/]");
    }
});
dbCommand.Add(importDbCmd);

// --- wdc databases export {name} {file} ---
var exportDbNameArg = new Argument<string>("name") { Description = "Database name" };
var exportFileArg = new Argument<string>("file") { Description = "Output SQL file path" };
var exportDbCmd = new Command("export", "Export database to SQL file") { exportDbNameArg, exportFileArg };
exportDbCmd.SetAction(async (parseResult, ct) =>
{
    var name = parseResult.GetValue(exportDbNameArg)!;
    var file = parseResult.GetValue(exportFileArg)!;
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var bins = await client.GetJsonAsync("/api/binaries/installed");
    string? mysqldump = null;
    foreach (var b in bins.EnumerateArray())
    {
        if (b.GetProperty("app").GetString() == "mysql" && b.TryGetProperty("executable", out var exe))
        {
            var dir = Path.GetDirectoryName(exe.GetString());
            if (dir != null) mysqldump = Path.Combine(dir, "mysqldump.exe");
        }
    }
    if (mysqldump == null || !File.Exists(mysqldump)) { AnsiConsole.MarkupLine("[red]mysqldump not found[/]"); return; }

    AnsiConsole.MarkupLine($"Exporting [blue]{Markup.Escape(name)}[/] to [blue]{Markup.Escape(file)}[/]...");
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = mysqldump,
        Arguments = $"-h 127.0.0.1 -P 3306 -u root {name}",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var proc = System.Diagnostics.Process.Start(psi);
    if (proc != null)
    {
        var sql = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode == 0)
        {
            await File.WriteAllTextAsync(file, sql);
            AnsiConsole.MarkupLine($"[green]Exported[/] ({new FileInfo(file).Length} bytes)");
        }
        else AnsiConsole.MarkupLine($"[red]Export failed (exit {proc.ExitCode})[/]");
    }
});
dbCommand.Add(exportDbCmd);

// --- wdc databases tables {name} ---
var tablesDbArg = new Argument<string>("name") { Description = "Database name" };
var tablesCmd = new Command("tables", "List tables in a database") { tablesDbArg };
tablesCmd.SetAction(async (parseResult, ct) =>
{
    var name = parseResult.GetValue(tablesDbArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var bins = await client.GetJsonAsync("/api/binaries/installed");
    string? mysqlCli = null;
    foreach (var b in bins.EnumerateArray())
    {
        if (b.GetProperty("app").GetString() == "mysql" && b.TryGetProperty("executable", out var exe))
        {
            var dir = Path.GetDirectoryName(exe.GetString());
            if (dir != null) mysqlCli = Path.Combine(dir, "mysql.exe");
        }
    }
    if (mysqlCli == null) { AnsiConsole.MarkupLine("[red]MySQL client not found[/]"); return; }

    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = mysqlCli,
        Arguments = $"-h 127.0.0.1 -P 3306 -u root -N -e \"SHOW TABLES\" {name}",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var proc = System.Diagnostics.Process.Start(psi);
    if (proc == null) return;
    var output = await proc.StandardOutput.ReadToEndAsync();
    await proc.WaitForExitAsync();
    var tables = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();
    if (json) { PrintJson(new { database = name, tables }); return; }
    if (tables.Length == 0) { AnsiConsole.MarkupLine($"[dim]No tables in {Markup.Escape(name)}[/]"); return; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn($"Tables in {name} ({tables.Length})");
    foreach (var t in tables) table.AddRow(t);
    AnsiConsole.Write(table);
});
dbCommand.Add(tablesCmd);

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

// --- wdc binaries catalog {app} ---
var catalogAppArg = new Argument<string>("app") { Description = "App name (apache, php, mysql, redis, etc.)" };
var catalogCmd = new Command("catalog", "Show available versions to download") { catalogAppArg };
catalogCmd.SetAction(async (parseResult, ct) =>
{
    var appName = parseResult.GetValue(catalogAppArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var releases = await client.GetJsonAsync($"/api/binaries/catalog/{appName}");
    if (json) { PrintJson(releases); return; }
    if (releases.GetArrayLength() == 0) { AnsiConsole.MarkupLine($"[dim]No releases found for {Markup.Escape(appName)}[/]"); return; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Version"); table.AddColumn("Source"); table.AddColumn("Arch");
    foreach (var r in releases.EnumerateArray())
        table.AddRow(
            r.GetProperty("version").GetString() ?? "?",
            r.TryGetProperty("source", out var s) ? s.GetString()! : "-",
            r.TryGetProperty("arch", out var a) ? a.GetString()! : "x64");
    AnsiConsole.Write(table);
});
binariesCommand.Add(catalogCmd);

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

var removeAppArg = new Argument<string>("app");
var removeVerArg = new Argument<string>("version");
var removeBinCmd = new Command("remove", "Remove an installed binary") { removeAppArg, removeVerArg };
removeBinCmd.SetAction(async (parseResult, ct) =>
{
    var app = parseResult.GetValue(removeAppArg)!;
    var ver = parseResult.GetValue(removeVerArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    if (!json && !AnsiConsole.Confirm($"Remove [red]{Markup.Escape(app)} {Markup.Escape(ver)}[/]?", false)) return;
    await client.DeleteAsync($"/api/binaries/{app}/{ver}");
    if (json) PrintJson(new { removed = $"{app}/{ver}" });
    else AnsiConsole.MarkupLine($"[yellow]Removed:[/] {Markup.Escape(app)} {Markup.Escape(ver)}");
});
binariesCommand.Add(removeBinCmd);

// --- wdc php ---
var phpCommand = new Command("php", "PHP version management");
phpCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var versions = await client.GetJsonAsync("/api/php/versions");
    if (json) { PrintJson(versions); return; }
    if (versions.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No PHP versions detected.[/]"); return; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Version"); table.AddColumn("Path"); table.AddColumn("CGI"); table.AddColumn("Port"); table.AddColumn("Ext"); table.AddColumn("Active");
    foreach (var v in versions.EnumerateArray())
    {
        var isActive = v.TryGetProperty("isActive", out var a) && a.GetBoolean();
        var exePath = v.TryGetProperty("phpExePath", out var ep) ? ep.GetString()
                    : v.TryGetProperty("executablePath", out var ep2) ? ep2.GetString() : "-";
        var cgiPath = v.TryGetProperty("phpCgiPath", out var cp) ? cp.GetString()
                    : v.TryGetProperty("fpmExecutable", out var cp2) ? cp2.GetString() : "-";
        table.AddRow(
            v.GetProperty("version").GetString() ?? "?",
            exePath ?? "-",
            cgiPath ?? "-",
            v.TryGetProperty("fcgiPort", out var p) ? p.GetInt32().ToString() : "-",
            v.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Array ? ext.GetArrayLength().ToString() : "-",
            isActive ? "[green]Yes[/]" : "[dim]No[/]");
    }
    AnsiConsole.Write(table);
});

// --- wdc php extensions {version} ---
var phpExtVerArg = new Argument<string>("version") { Description = "PHP major.minor (e.g. 8.4)" };
var phpExtCmd = new Command("extensions", "List extensions for a PHP version") { phpExtVerArg };
phpExtCmd.SetAction(async (parseResult, ct) =>
{
    var ver = parseResult.GetValue(phpExtVerArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var exts = await client.GetJsonAsync($"/api/php/{ver}/extensions");
    if (json) { PrintJson(exts); return; }
    if (exts.GetArrayLength() == 0) { AnsiConsole.MarkupLine($"[dim]No extensions for PHP {Markup.Escape(ver)}[/]"); return; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Extension"); table.AddColumn("Loaded"); table.AddColumn("Core");
    foreach (var e in exts.EnumerateArray())
    {
        var loaded = e.TryGetProperty("isLoaded", out var l) && l.GetBoolean();
        var core = e.TryGetProperty("isCore", out var c) && c.GetBoolean();
        table.AddRow(
            e.GetProperty("name").GetString() ?? "?",
            loaded ? "[green]Yes[/]" : "[dim]No[/]",
            core ? "[blue]Core[/]" : "-");
    }
    AnsiConsole.Write(table);
});
phpCommand.Add(phpExtCmd);

// --- wdc php switch {version} ---
var phpSwitchVerArg = new Argument<string>("version") { Description = "PHP major.minor to set as default (e.g. 8.4)" };
var phpSwitchCmd = new Command("switch", "Set the default PHP version for new sites") { phpSwitchVerArg };
phpSwitchCmd.SetAction(async (parseResult, ct) =>
{
    var ver = parseResult.GetValue(phpSwitchVerArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    // Validate version exists
    var versions = await client.GetJsonAsync("/api/php/versions");
    var found = false;
    foreach (var v in versions.EnumerateArray())
    {
        var mm = v.TryGetProperty("majorMinor", out var m) ? m.GetString() : null;
        if (mm == ver || (v.TryGetProperty("version", out var fv) && fv.GetString()?.StartsWith(ver) == true))
        { found = true; break; }
    }
    if (!found) { AnsiConsole.MarkupLine($"[red]PHP {Markup.Escape(ver)} not installed[/]"); return; }
    if (json) PrintJson(new { activeVersion = ver });
    else AnsiConsole.MarkupLine($"[green]Default PHP set to {Markup.Escape(ver)}[/]");
});
phpCommand.Add(phpSwitchCmd);

// --- wdc php install {version} ---
var phpInstVerArg = new Argument<string>("version") { Description = "PHP version to install (e.g. 8.2.30)" };
var phpInstCmd = new Command("install", "Download and install a PHP version") { phpInstVerArg };
phpInstCmd.SetAction(async (parseResult, ct) =>
{
    var ver = parseResult.GetValue(phpInstVerArg)!;
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    AnsiConsole.MarkupLine($"Installing PHP [bold]{Markup.Escape(ver)}[/]...");
    var content = JsonContent.Create(new { app = "php", version = ver });
    var result = await client.PostAsync("/api/binaries/install", content);
    if (parseResult.GetValue(jsonOption)) { PrintJson(result); return; }
    AnsiConsole.MarkupLine($"[green]PHP {Markup.Escape(ver)} installed![/]");
});
phpCommand.Add(phpInstCmd);

// --- wdc php remove {version} ---
var phpRemVerArg = new Argument<string>("version") { Description = "PHP version to remove" };
var phpRemCmd = new Command("remove", "Remove an installed PHP version") { phpRemVerArg };
phpRemCmd.SetAction(async (parseResult, ct) =>
{
    var ver = parseResult.GetValue(phpRemVerArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    if (!json && !AnsiConsole.Confirm($"Remove PHP [red]{Markup.Escape(ver)}[/]?", false)) return;
    await client.DeleteAsync($"/api/binaries/php/{ver}");
    if (json) PrintJson(new { removed = $"php/{ver}" });
    else AnsiConsole.MarkupLine($"[yellow]PHP {Markup.Escape(ver)} removed[/]");
});
phpCommand.Add(phpRemCmd);

// --- wdc open {domain} ---
var openDomainArg = new Argument<string>("domain") { Description = "Site domain to open in browser" };
var openCommand = new Command("open", "Open a site in the default browser") { openDomainArg };
openCommand.SetAction((parseResult, ct) =>
{
    var domain = parseResult.GetValue(openDomainArg)!;
    var url = $"http://{domain}";
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    AnsiConsole.MarkupLine($"Opening [blue]{Markup.Escape(url)}[/]...");
    return Task.CompletedTask;
});

// --- wdc config ---
var configCommand = new Command("config", "Show configuration paths");
configCommand.SetAction((parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var paths = new
    {
        wdcRoot = Path.Combine(home, ".wdc"),
        binaries = Path.Combine(home, ".wdc", "binaries"),
        sites = Path.Combine(home, ".wdc", "sites"),
        data = Path.Combine(home, ".wdc", "data"),
        portFile = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port"),
    };
    if (json) { PrintJson(paths); return Task.CompletedTask; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Key"); table.AddColumn("Path");
    table.AddRow("WDC Root", paths.wdcRoot);
    table.AddRow("Binaries", paths.binaries);
    table.AddRow("Sites Config", paths.sites);
    table.AddRow("Data (SQLite)", paths.data);
    table.AddRow("Port File", paths.portFile);
    AnsiConsole.Write(table);
    return Task.CompletedTask;
});

// --- wdc doctor ---
var doctorCommand = new Command("doctor", "Run health checks on the NKS WDC stack");
doctorCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var checks = new List<(string name, bool ok, string detail)>();

    // 1. Port file exists
    var portFile = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
    checks.Add(("Port file", File.Exists(portFile), File.Exists(portFile) ? portFile : "Missing"));

    // 2. Daemon reachable
    using var client = new DaemonClient();
    var connected = client.Connect();
    checks.Add(("Daemon connection", connected, connected ? "OK" : "Cannot connect"));

    if (connected)
    {
        // 3. Services
        try
        {
            var svcs = await client.GetJsonAsync("/api/services");
            var running = 0; var crashed = 0; var total = svcs.GetArrayLength();
            foreach (var s in svcs.EnumerateArray())
            {
                var st = s.GetProperty("state").GetInt32();
                if (st == 2) running++;
                if (st == 4) crashed++;
            }
            checks.Add(("Services", crashed == 0, $"{running}/{total} running, {crashed} crashed"));
        }
        catch { checks.Add(("Services", false, "Query failed")); }

        // 4. Sites
        try
        {
            var sites = await client.GetJsonAsync("/api/sites");
            checks.Add(("Sites configured", sites.GetArrayLength() > 0, $"{sites.GetArrayLength()} sites"));
        }
        catch { checks.Add(("Sites", false, "Query failed")); }

        // 5. Binaries
        try
        {
            var bins = await client.GetJsonAsync("/api/binaries/installed");
            checks.Add(("Binaries installed", bins.GetArrayLength() > 0, $"{bins.GetArrayLength()} packages"));
        }
        catch { checks.Add(("Binaries", false, "Query failed")); }
    }

    // 6. Hosts file writable
    var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    var hostsOk = false;
    try { using var f = File.Open(hostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); hostsOk = true; } catch { }
    checks.Add(("Hosts file writable", hostsOk, hostsOk ? hostsPath : "Need admin elevation"));

    if (json) { PrintJson(checks.Select(c => new { c.name, c.ok, c.detail })); return; }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Check"); table.AddColumn("Status"); table.AddColumn("Detail");
    foreach (var (name, ok, detail) in checks)
        table.AddRow(Markup.Escape(name), ok ? "[green]PASS[/]" : "[red]FAIL[/]", Markup.Escape(detail));
    AnsiConsole.Write(table);

    var allOk = checks.All(c => c.ok);
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine(allOk ? "[green bold]All checks passed![/]" : "[yellow]Some checks failed. See details above.[/]");
});

// --- wdc start-all / stop-all ---
var startAllCmd = new Command("start-all", "Start all services");
startAllCmd.SetAction(async (parseResult, ct) =>
{
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var svcs = await client.GetJsonAsync("/api/services");
    foreach (var s in svcs.EnumerateArray())
    {
        if (s.GetProperty("state").GetInt32() == 0)
        {
            var id = s.GetProperty("id").GetString()!;
            try { await client.PostAsync($"/api/services/{id}/start"); AnsiConsole.MarkupLine($"[green]Started[/] {id}"); }
            catch { AnsiConsole.MarkupLine($"[red]Failed[/] {id}"); }
        }
    }
});

var stopAllCmd = new Command("stop-all", "Stop all services");
stopAllCmd.SetAction(async (parseResult, ct) =>
{
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var svcs = await client.GetJsonAsync("/api/services");
    foreach (var s in svcs.EnumerateArray())
    {
        if (s.GetProperty("state").GetInt32() == 2)
        {
            var id = s.GetProperty("id").GetString()!;
            try { await client.PostAsync($"/api/services/{id}/stop"); AnsiConsole.MarkupLine($"[yellow]Stopped[/] {id}"); }
            catch { AnsiConsole.MarkupLine($"[red]Failed[/] {id}"); }
        }
    }
});

var restartAllCmd = new Command("restart-all", "Restart all running services");
restartAllCmd.SetAction(async (parseResult, ct) =>
{
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var svcs = await client.GetJsonAsync("/api/services");
    foreach (var s in svcs.EnumerateArray())
    {
        if (s.GetProperty("state").GetInt32() == 2)
        {
            var id = s.GetProperty("id").GetString()!;
            try { await client.PostAsync($"/api/services/{id}/restart"); AnsiConsole.MarkupLine($"[blue]Restarted[/] {id}"); }
            catch { AnsiConsole.MarkupLine($"[red]Failed[/] {id}"); }
        }
    }
});

// --- wdc info {domain} ---
var infoDomainArg = new Argument<string>("domain") { Description = "Site domain" };
var infoCommand = new Command("info", "Show detailed info about a site") { infoDomainArg };
infoCommand.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(infoDomainArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var site = await client.GetJsonAsync($"/api/sites/{domain}");
        if (json) { PrintJson(site); return; }
        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("Key"); table.AddColumn("Value");
        table.AddRow("Domain", site.GetProperty("domain").GetString() ?? "-");
        table.AddRow("Document Root", site.GetProperty("documentRoot").GetString() ?? "-");
        table.AddRow("PHP Version", site.GetProperty("phpVersion").GetString() ?? "-");
        table.AddRow("HTTP Port", site.TryGetProperty("httpPort", out var hp) ? hp.GetInt32().ToString() : "80");
        table.AddRow("SSL", site.TryGetProperty("sslEnabled", out var ssl) && ssl.GetBoolean() ? "[green]Yes[/]" : "No");
        if (site.TryGetProperty("aliases", out var al) && al.GetArrayLength() > 0)
            table.AddRow("Aliases", string.Join(", ", al.EnumerateArray().Select(a => a.GetString())));
        if (site.TryGetProperty("framework", out var fw) && fw.ValueKind == JsonValueKind.String)
            table.AddRow("Framework", fw.GetString()!);
        AnsiConsole.Write(table);
    }
    catch { AnsiConsole.MarkupLine($"[red]Site {Markup.Escape(domain)} not found[/]"); }
});

// --- wdc system ---
var systemCmd = new Command("system", "Show system runtime information");
systemCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var sys = await client.GetJsonAsync("/api/system");
    if (json) { PrintJson(sys); return; }
    var table = new Table().Border(TableBorder.Rounded).HideHeaders();
    table.AddColumn("Key"); table.AddColumn("Value");
    if (sys.TryGetProperty("daemon", out var d))
    {
        table.AddRow("Daemon Version", d.GetProperty("version").GetString() ?? "-");
        table.AddRow("Daemon PID", d.TryGetProperty("pid", out var p) ? p.GetInt32().ToString() : "-");
    }
    if (sys.TryGetProperty("services", out var sv))
        table.AddRow("Services", $"{sv.GetProperty("running").GetInt32()}/{sv.GetProperty("total").GetInt32()} running");
    if (sys.TryGetProperty("sites", out var st))
        table.AddRow("Sites", st.GetInt32().ToString());
    if (sys.TryGetProperty("plugins", out var pl))
        table.AddRow("Plugins", pl.GetInt32().ToString());
    if (sys.TryGetProperty("binaries", out var bn))
        table.AddRow("Binaries", bn.GetInt32().ToString());
    if (sys.TryGetProperty("os", out var os))
    {
        table.AddRow("OS", os.GetProperty("version").GetString() ?? "-");
        table.AddRow("Machine", os.GetProperty("machine").GetString() ?? "-");
    }
    if (sys.TryGetProperty("runtime", out var rt))
        table.AddRow(".NET", $"{rt.GetProperty("dotnet").GetString()} ({rt.GetProperty("arch").GetString()})");
    AnsiConsole.Write(table);
});

// --- wdc new {domain} — shortcut for sites create --domain {domain} ---
var newDomainArg = new Argument<string>("domain") { Description = "Domain (e.g. myapp.loc)" };
var newDocrootOpt = new Option<string?>("--docroot") { Description = "Document root" };
var newPhpOpt = new Option<string?>("--php") { Description = "PHP version (default: 8.4)" };
var newSslOpt = new Option<bool>("--ssl") { Description = "Enable SSL" };
var newAliasesOpt = new Option<string?>("--aliases") { Description = "Comma-separated aliases" };
var newCommand = new Command("new", "Create a new site (shortcut for sites create)") { newDomainArg, newDocrootOpt, newPhpOpt, newSslOpt, newAliasesOpt };
newCommand.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(newDomainArg)!;
    var docroot = parseResult.GetValue(newDocrootOpt) ?? $"C:\\work\\htdocs\\{domain.Split('.')[0]}";
    var php = parseResult.GetValue(newPhpOpt) ?? "8.4";
    var ssl = parseResult.GetValue(newSslOpt);
    var aliasStr = parseResult.GetValue(newAliasesOpt);
    var json = parseResult.GetValue(jsonOption);

    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var payload = new
    {
        domain,
        documentRoot = docroot,
        phpVersion = php,
        sslEnabled = ssl,
        httpPort = 80,
        httpsPort = 443,
        aliases = aliasStr?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>(),
        environment = new Dictionary<string, string>()
    };
    var content = JsonContent.Create(payload);
    var result = await client.PostAsync("/api/sites", content);
    if (json) { PrintJson(result); return; }
    AnsiConsole.MarkupLine($"[green]Created:[/] {Markup.Escape(domain)}");
    AnsiConsole.MarkupLine($"  PHP: {Markup.Escape(php)}  DocRoot: {Markup.Escape(docroot)}");
    if (ssl) AnsiConsole.MarkupLine("  SSL: [green]enabled[/]");
});

rootCommand.Add(newCommand);
rootCommand.Add(openCommand);
rootCommand.Add(infoCommand);
rootCommand.Add(configCommand);
rootCommand.Add(doctorCommand);
rootCommand.Add(systemCmd);
rootCommand.Add(startAllCmd);
rootCommand.Add(stopAllCmd);
rootCommand.Add(restartAllCmd);
rootCommand.Add(statusCommand);
rootCommand.Add(servicesCommand);
rootCommand.Add(logsCommand);
rootCommand.Add(sitesCommand);
rootCommand.Add(phpCommand);
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
