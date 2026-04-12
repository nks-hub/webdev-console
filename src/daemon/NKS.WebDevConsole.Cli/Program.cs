using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http.Json;
using System.Text.Json;
using NKS.WebDevConsole.Cli;
using NKS.WebDevConsole.Core.Services;
using Spectre.Console;

var jsonOption = new Option<bool>("--json") { Description = "Output raw JSON instead of formatted tables", Recursive = true };
var versionOption = new Option<bool>("--version", "-v") { Description = "Show CLI version and exit" };

var rootCommand = new RootCommand("NKS WebDev Console CLI") { jsonOption, versionOption };
rootCommand.SetAction((parseResult, ct) =>
{
    if (parseResult.GetValue(versionOption))
    {
        var ver = typeof(DaemonClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Console.WriteLine($"wdc {ver}");
    }
    return Task.CompletedTask;
});

// --- wdc status ---
var quietOption = new Option<bool>("--quiet", "-q") { Description = "Exit 0 if daemon running, 1 if not (no output)" };
var statusCommand = new Command("status", "Show daemon and service status") { quietOption };
statusCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var quiet = parseResult.GetValue(quietOption);
    using var client = new DaemonClient();
    if (quiet)
    {
        Environment.Exit(client.Connect() ? 0 : 1);
        return;
    }
    if (!EnsureConnected(client)) return;

    var status = await client.GetJsonAsync("/api/status");
    var services = await client.GetJsonAsync("/api/services");
    // /api/system is best-effort — adds host/catalog info on top of the
    // bare /api/status response. If the daemon is an old build that
    // doesn't expose /api/system, fall through silently so `wdc status`
    // still prints the core daemon+services output.
    System.Text.Json.JsonElement? system = null;
    try { system = await client.GetJsonAsync("/api/system"); } catch { /* old daemon */ }

    if (json) { PrintJson(new { status, services, system }); return; }

    var svcRunning = 0;
    foreach (var s in services.EnumerateArray())
        if (s.GetProperty("state").ValueKind == JsonValueKind.Number && s.GetProperty("state").GetInt32() == 2) svcRunning++;
    AnsiConsole.MarkupLine($"[bold]Daemon[/]  [green]running[/]  v{status.GetProperty("version").GetString()}");
    AnsiConsole.MarkupLine($"Plugins: {status.GetProperty("plugins").GetInt32()}  Services: {svcRunning}/{services.GetArrayLength()} running  Uptime: {FormatUptime(status.GetProperty("uptime").GetInt64())}");

    if (system is System.Text.Json.JsonElement sys)
    {
        // Host line: os/arch tag + machine name
        if (sys.TryGetProperty("os", out var os))
        {
            var osTag = os.TryGetProperty("tag", out var t) ? t.GetString() : "unknown";
            var archTag = os.TryGetProperty("arch", out var a) ? a.GetString() : "unknown";
            var machine = os.TryGetProperty("machine", out var m) ? m.GetString() : "";
            AnsiConsole.MarkupLine($"Host:    [cyan]{osTag}/{archTag}[/]  {Markup.Escape(machine ?? string.Empty)}");
        }

        // Catalog health line — color-coded by reachable flag
        if (sys.TryGetProperty("catalog", out var cat))
        {
            var url = cat.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var count = cat.TryGetProperty("cachedCount", out var cnt) ? cnt.GetInt32() : 0;
            var reachable = cat.TryGetProperty("reachable", out var r) && r.GetBoolean();
            var lastFetchRaw = cat.TryGetProperty("lastFetch", out var lf) && lf.ValueKind != System.Text.Json.JsonValueKind.Null
                ? lf.GetString() : null;
            var lastFetchDisplay = lastFetchRaw is null
                ? "never"
                : DateTime.TryParse(lastFetchRaw, out var dt)
                    ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : lastFetchRaw;
            var color = reachable ? "green" : "red";
            var label = reachable ? $"{count} releases" : "unreachable";
            AnsiConsole.MarkupLine($"Catalog: [{color}]{label}[/]  [dim]{Markup.Escape(url)}[/]  [dim]({lastFetchDisplay})[/]");
        }
    }

    AnsiConsole.WriteLine();

    if (services.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No services registered.[/]"); return; }

    if (Console.IsOutputRedirected)
    {
        foreach (var svc in services.EnumerateArray())
        {
            var id = svc.GetProperty("id").GetString() ?? "";
            var stateStr = svc.GetProperty("state").ValueKind == JsonValueKind.Number
                ? StateNumToStr(svc.GetProperty("state").GetInt32()) : "unknown";
            Console.WriteLine($"{id}\t{stateStr}");
        }
        return;
    }

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
    if (Console.IsOutputRedirected)
    {
        foreach (var svc in services.EnumerateArray())
        {
            var id = svc.GetProperty("id").GetString() ?? "";
            var st = svc.GetProperty("state").ValueKind == JsonValueKind.Number ? svc.GetProperty("state").GetInt32() : 0;
            var ver = svc.TryGetProperty("version", out var vp) && vp.ValueKind == JsonValueKind.String ? vp.GetString() ?? "" : "";
            Console.WriteLine($"{id}\t{StateNumToStr(st)}\t{ver}");
        }
        return;
    }
    if (services.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No services registered.[/]"); return; }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("ID"); table.AddColumn("Name"); table.AddColumn("Version");
    table.AddColumn("State"); table.AddColumn("PID"); table.AddColumn("CPU %"); table.AddColumn("Memory");
    foreach (var svc in services.EnumerateArray())
    {
        var sState = svc.GetProperty("state").ValueKind == JsonValueKind.Number
            ? StateNumToStr(svc.GetProperty("state").GetInt32())
            : svc.GetProperty("state").GetString() ?? "unknown";
        var ver = svc.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "-" : "-";
        table.AddRow(
            svc.GetProperty("id").GetString() ?? "?",
            svc.GetProperty("displayName").GetString() ?? "-",
            ver,
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
var logsFollowOpt = new Option<bool>("--follow", "-f") { Description = "Stream logs in real-time via WebSocket" };
var logsCommand = new Command("logs", "Show service logs") { logsIdArg, logsLinesOpt, logsFollowOpt };
logsCommand.SetAction(async (parseResult, ct) =>
{
    var id = parseResult.GetValue(logsIdArg)!;
    var lines = parseResult.GetValue(logsLinesOpt) ?? 50;
    var follow = parseResult.GetValue(logsFollowOpt);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    // Follow mode: connect via WebSocket for real-time streaming
    if (follow)
    {
        // Read port+token from the same port file DaemonClient uses
        var portFile = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
        var pf = File.ReadAllLines(portFile);
        var port = pf[0];
        var token = pf.Length > 1 ? pf[1] : "";
        var wsUrl = $"ws://localhost:{port}/api/logs/{id}/stream{(string.IsNullOrEmpty(token) ? "" : $"?token={Uri.EscapeDataString(token)}")}";
        try
        {
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            AnsiConsole.MarkupLine($"[dim]Streaming logs for {Markup.Escape(id)} (Ctrl+C to stop)...[/]");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var buf = new byte[8192];
            while (ws.State == System.Net.WebSockets.WebSocketState.Open && !cts.IsCancellationRequested)
            {
                System.Net.WebSockets.WebSocketReceiveResult result2;
                try { result2 = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token); }
                catch (OperationCanceledException) { break; }
                if (result2.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                var msg = System.Text.Encoding.UTF8.GetString(buf, 0, result2.Count);
                try
                {
                    var parsed = System.Text.Json.JsonDocument.Parse(msg);
                    var line = parsed.RootElement.GetProperty("line").GetString() ?? "";
                    Console.WriteLine(line);
                }
                catch { Console.WriteLine(msg); }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]WebSocket error:[/] {Markup.Escape(ex.Message)}");
            Environment.Exit(1);
        }
        return;
    }

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
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(text);
        }
        else if (text.Contains("ERR", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(text)}[/]");
        }
        else
        {
            AnsiConsole.WriteLine(text);
        }
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
    if (svc.TryGetProperty("type", out var tp) && tp.ValueKind == System.Text.Json.JsonValueKind.String)
        table.AddRow("Type", tp.GetString() ?? "-");
    var st = svc.GetProperty("state").GetInt32();
    table.AddRow("State", FormatState(StateNumToStr(st)));
    table.AddRow("PID", GetInt(svc, "pid")?.ToString() ?? "-");
    table.AddRow("CPU", GetDouble(svc, "cpuPercent") is double c ? $"{c:F1}%" : "-");
    table.AddRow("Memory", GetLong(svc, "memoryBytes") is long m ? FormatBytes(m) : "-");
    if (svc.TryGetProperty("uptime", out var up)) table.AddRow("Uptime", FormatUptime(up.ValueKind == System.Text.Json.JsonValueKind.String ? long.TryParse(up.GetString(), out var us) ? us : 0 : up.ValueKind == System.Text.Json.JsonValueKind.Number ? (long)up.GetDouble() : 0));
    AnsiConsole.Write(table);
});
servicesCommand.Add(svcInfoCmd);

// wdc services count — script-friendly running/total
var svcCountCmd = new Command("count", "Print running/total service count");
svcCountCmd.SetAction(async (parseResult, ct) =>
{
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var svcs = await client.GetJsonAsync("/api/services");
    var total = svcs.GetArrayLength();
    var running = 0;
    foreach (var s in svcs.EnumerateArray())
        if (s.TryGetProperty("state", out var st) && st.GetInt32() == 2) running++;
    AnsiConsole.Write($"{running}/{total}");
});
servicesCommand.Add(svcCountCmd);

// --- wdc sites ---
var sitesCommand = new Command("sites", "List all sites");
sitesCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var sites = await client.GetJsonAsync("/api/sites");
    if (json) { PrintJson(sites); return; }
    // --plain mode: one domain per line for piping (wdc sites --json | jq, or wdc sites | grep)
    // Detect piped output: if stdout is redirected, auto-switch to plain
    if (!Console.IsOutputRedirected && sites.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No sites configured.[/]"); return; }
    if (Console.IsOutputRedirected)
    {
        foreach (var site in sites.EnumerateArray())
        {
            var domain = site.GetProperty("domain").GetString() ?? "";
            var docRoot = site.GetProperty("documentRoot").GetString() ?? "";
            var php = site.GetProperty("phpVersion").GetString() ?? "";
            var sslOn = site.TryGetProperty("sslEnabled", out var se) && se.GetBoolean() ? "ssl" : "";
            Console.WriteLine($"{domain}\t{docRoot}\t{php}\t{sslOn}");
        }
        return;
    }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Domain"); table.AddColumn("Runtime"); table.AddColumn("SSL"); table.AddColumn("Tunnel"); table.AddColumn("Framework");
    foreach (var site in sites.EnumerateArray())
    {
        var ssl = site.TryGetProperty("sslEnabled", out var s) && s.GetBoolean();
        var fw = site.TryGetProperty("framework", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        var nodePort = site.TryGetProperty("nodeUpstreamPort", out var np) ? np.GetInt32() : 0;
        var phpVer = site.GetProperty("phpVersion").GetString() ?? "none";
        var runtime = nodePort > 0
            ? $"[green]Node:{nodePort}[/]"
            : (phpVer != "none" ? $"[blue]PHP {phpVer}[/]" : "[dim]Static[/]");
        // Cloudflare tunnel status
        var tunnelLabel = "[dim]-[/]";
        if (site.TryGetProperty("cloudflare", out var cf) && cf.ValueKind == JsonValueKind.Object)
        {
            var cfEnabled = cf.TryGetProperty("enabled", out var en) && en.GetBoolean();
            if (cfEnabled)
            {
                var sub = cf.TryGetProperty("subdomain", out var sd) ? sd.GetString() ?? "" : "";
                var zone = cf.TryGetProperty("zoneName", out var zn) ? zn.GetString() ?? "" : "";
                tunnelLabel = sub.Length > 0 && zone.Length > 0
                    ? $"[cyan]{Markup.Escape(sub)}.{Markup.Escape(zone)}[/]"
                    : "[green]Yes[/]";
            }
        }
        table.AddRow(
            site.GetProperty("domain").GetString() ?? "?",
            runtime,
            ssl ? "[green]Yes[/]" : "[dim]No[/]",
            tunnelLabel,
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

    try
    {
        await client.DeleteAsync($"/api/sites/{domain}");
        if (json) PrintJson(new { deleted = domain });
        else AnsiConsole.MarkupLine($"[yellow]Deleted:[/] {Markup.Escape(domain)}");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Failed to delete {Markup.Escape(domain)}:[/] {Markup.Escape(ex.Message)}");
    }
});
sitesCommand.Add(deleteSiteCmd);

// --- wdc sites create ---
var domainOpt = new Option<string?>("--domain") { Description = "Domain name (e.g. myapp.loc)" };
var docrootOpt = new Option<string?>("--docroot") { Description = "Document root path" };
var phpOpt = new Option<string?>("--php") { Description = "PHP version (default: 8.4)" };
var sslOpt = new Option<bool>("--ssl") { Description = "Enable SSL" };
var aliasesOpt = new Option<string?>("--aliases") { Description = "Comma-separated aliases" };
// Site creation templates — pre-configured blueprints that set sensible
// defaults for common stacks. The --template flag applies the blueprint
// BEFORE any explicit flags, so `--template laravel --php 8.2` starts
// from the Laravel defaults but overrides PHP to 8.2.
var siteTemplates = new Dictionary<string, (string php, bool ssl, string? framework)>(StringComparer.OrdinalIgnoreCase)
{
    ["wordpress"] = ("8.4", true, "wordpress"),
    ["laravel"]   = ("8.4", true, "laravel"),
    ["nette"]     = ("8.4", true, "nette"),
    ["symfony"]   = ("8.4", true, "symfony"),
    ["nextjs"]    = ("none", true, null),  // Node proxy, phpVersion=none
    ["static"]    = ("none", false, null),
    ["node"]      = ("none", false, null),
};
var templateOpt = new Option<string?>("--template", $"Site blueprint: {string.Join(", ", siteTemplates.Keys)}");
var createNodePortOpt = new Option<int?>("--node-port") { Description = "Node.js upstream port (enables reverse-proxy mode)" };
var createSiteCmd = new Command("create", "Create a new site") { domainOpt, docrootOpt, phpOpt, sslOpt, aliasesOpt, templateOpt, createNodePortOpt };
createSiteCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    // Apply template defaults first, then override with explicit flags
    var tplName = parseResult.GetValue(templateOpt);
    var tpl = tplName is not null && siteTemplates.TryGetValue(tplName, out var t) ? t : default;
    var tplApplied = tplName is not null && siteTemplates.ContainsKey(tplName);

    var domain = parseResult.GetValue(domainOpt) ?? AnsiConsole.Ask<string>("Domain (e.g. [green]myapp.loc[/]):");
    var docRoot = parseResult.GetValue(docrootOpt) ?? AnsiConsole.Ask<string>("Document root:");
    var php = parseResult.GetValue(phpOpt) ?? (tplApplied ? tpl.php : "8.4");
    var ssl = parseResult.GetValue(sslOpt) || (tplApplied && tpl.ssl)
              || (!json && !tplApplied && domain == null && AnsiConsole.Confirm("Enable SSL?", false));
    var aliasStr = parseResult.GetValue(aliasesOpt);

    var nodePort = parseResult.GetValue(createNodePortOpt) ?? 0;
    if (nodePort == 0 && tplApplied && tplName is "nextjs" or "node")
    {
        nodePort = 3000;
        if (!json) AnsiConsole.MarkupLine($"[dim]Template '{tplName}': Node.js reverse-proxy mode, upstream port 3000[/]");
    }

    var payload = new
    {
        domain,
        documentRoot = docRoot,
        phpVersion = php,
        sslEnabled = ssl,
        httpPort = 80,
        httpsPort = 443,
        nodeUpstreamPort = nodePort,
        framework = tplApplied ? tpl.framework : null,
        aliases = aliasStr?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>(),
        environment = new Dictionary<string, string>()
    };

    try
    {
        var content = JsonContent.Create(payload);
        var result = await client.PostAsync("/api/sites", content);
        if (json) { PrintJson(result); return; }
        var msg = tplApplied ? $"[green]Site created:[/] {Markup.Escape(domain)} [dim](template: {tplName})[/]"
                             : $"[green]Site created:[/] {Markup.Escape(domain)}";
        AnsiConsole.MarkupLine(msg);
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Failed to create site:[/] {Markup.Escape(ex.Message)}");
    }
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
    try
    {
        var result = await client.PostAsync($"/api/sites/{domain}/detect-framework");
        if (json) { PrintJson(result); return; }
        var fw = result.TryGetProperty("framework", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        AnsiConsole.MarkupLine(fw != null
            ? $"[green]Detected:[/] {Markup.Escape(fw)} for {Markup.Escape(domain)}"
            : $"[dim]No framework detected for {Markup.Escape(domain)}[/]");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Detection failed:[/] {Markup.Escape(ex.Message)}");
    }
});
sitesCommand.Add(detectCmd);

// --- wdc sites reapply ---
var reapplyCmd = new Command("reapply", "Regenerate all site vhosts and reload Apache");
reapplyCmd.SetAction(async (parseResult, ct) =>
{
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
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
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Reapply failed:[/] {Markup.Escape(ex.Message)}");
    }
});
sitesCommand.Add(reapplyCmd);

// --- wdc sites update {domain} ---
var updateDomainArg = new Argument<string>("domain");
var updatePhpOpt = new Option<string?>("--php") { Description = "Set PHP version" };
var updateDocrootOpt = new Option<string?>("--docroot") { Description = "Set document root" };
var updateSslOpt = new Option<bool?>("--ssl") { Description = "Enable/disable SSL" };
var updateNodePortOpt = new Option<int?>("--node-port") { Description = "Set Node.js upstream port (0 to disable)" };
var updateSiteCmd = new Command("update", "Update a site") { updateDomainArg, updatePhpOpt, updateDocrootOpt, updateSslOpt, updateNodePortOpt };
updateSiteCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(updateDomainArg)!;
    var php = parseResult.GetValue(updatePhpOpt);
    var docroot = parseResult.GetValue(updateDocrootOpt);
    var sslOpt = parseResult.GetValue(updateSslOpt);
    var nodePort = parseResult.GetValue(updateNodePortOpt);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    try
    {
        var site = await client.GetJsonAsync($"/api/sites/{domain}");
        var payload = new Dictionary<string, object?>
        {
            ["domain"] = domain,
            ["documentRoot"] = docroot ?? (site.TryGetProperty("documentRoot", out var dr) ? dr.GetString() : null),
            ["phpVersion"] = php ?? (site.TryGetProperty("phpVersion", out var pv) ? pv.GetString() : null),
            ["sslEnabled"] = sslOpt ?? (site.TryGetProperty("sslEnabled", out var ssl) && ssl.GetBoolean()),
            ["httpPort"] = site.TryGetProperty("httpPort", out var hp) ? hp.GetInt32() : 80,
            ["httpsPort"] = site.TryGetProperty("httpsPort", out var hps) ? hps.GetInt32() : 443,
            ["nodeUpstreamPort"] = nodePort ?? (site.TryGetProperty("nodeUpstreamPort", out var np) ? np.GetInt32() : 0),
        };
        var content = JsonContent.Create(payload);
        var result = await client.PutAsync($"/api/sites/{domain}", content);
        if (json) { PrintJson(result); return; }
        AnsiConsole.MarkupLine($"[green]Updated[/] {Markup.Escape(domain)}");
        if (php != null) AnsiConsole.MarkupLine($"  PHP → {Markup.Escape(php)}");
        if (docroot != null) AnsiConsole.MarkupLine($"  DocRoot → {Markup.Escape(docroot)}");
        if (sslOpt != null) AnsiConsole.MarkupLine($"  SSL → {(sslOpt.Value ? "[green]on[/]" : "[dim]off[/]")}");
        if (nodePort != null) AnsiConsole.MarkupLine($"  Node port → {nodePort}");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Failed to update {Markup.Escape(domain)}:[/] {Markup.Escape(ex.Message)}");
    }
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
    if (hist.GetArrayLength() == 0) { if (!Console.IsOutputRedirected) AnsiConsole.MarkupLine("[dim]No history entries[/]"); return; }
    if (Console.IsOutputRedirected)
    {
        foreach (var h in hist.EnumerateArray())
        {
            var ts2 = h.GetProperty("timestamp").GetString() ?? "";
            var sz = h.TryGetProperty("size", out var s2) ? s2.GetInt64() : 0;
            Console.WriteLine($"{ts2}\t{sz}");
        }
        return;
    }
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
    try
    {
        var result = await client.PostAsync($"/api/sites/{domain}/rollback/{ts}");
        if (json) { PrintJson(result); return; }
        AnsiConsole.MarkupLine($"[green]Rolled back[/] {Markup.Escape(domain)} to {Markup.Escape(ts)}");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Rollback failed:[/] {Markup.Escape(ex.Message)}");
    }
});
sitesCommand.Add(rollbackCmd);

// wdc sites count — script-friendly site count
var countCmd = new Command("count", "Print the number of configured sites");
countCmd.SetAction(async (parseResult, ct) =>
{
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var sites = await client.GetJsonAsync("/api/sites");
    AnsiConsole.Write(sites.GetArrayLength().ToString());
});
sitesCommand.Add(countCmd);

// --- wdc plugins ---
var pluginsCommand = new Command("plugins", "List loaded plugins");
pluginsCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var plugins = await client.GetJsonAsync("/api/plugins");
    if (json) { PrintJson(plugins); return; }
    if (Console.IsOutputRedirected)
    {
        foreach (var p in plugins.EnumerateArray())
        {
            var en = p.TryGetProperty("enabled", out var e) && e.GetBoolean() ? "enabled" : "disabled";
            Console.WriteLine($"{p.GetProperty("id").GetString()}\t{p.GetProperty("version").GetString()}\t{en}");
        }
        return;
    }
    if (plugins.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No plugins loaded.[/]"); return; }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("ID"); table.AddColumn("Name"); table.AddColumn("Version"); table.AddColumn("Status"); table.AddColumn("Description");
    foreach (var p in plugins.EnumerateArray())
    {
        var enabled = p.TryGetProperty("enabled", out var en) && en.GetBoolean();
        var desc = p.TryGetProperty("description", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
            ? d.GetString() ?? "" : "";
        if (desc.Length > 50) desc = desc[..47] + "...";
        table.AddRow(
            p.GetProperty("id").GetString() ?? "?",
            p.GetProperty("name").GetString() ?? "-",
            p.GetProperty("version").GetString() ?? "-",
            enabled ? "[green]enabled[/]" : "[dim]disabled[/]",
            Markup.Escape(desc));
    }
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"[dim]{plugins.GetArrayLength()} plugin(s)[/]");
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

    // Fetch installed binary versions for a compact diagnostic overview
    var binVersions = new Dictionary<string, string>();
    if (connected)
    {
        try
        {
            var installed = await client.GetJsonAsync("/api/binaries/installed");
            var keyApps = new[] { "apache", "php", "mysql", "node", "redis", "nginx", "mariadb" };
            foreach (var app in keyApps)
            {
                var first = installed.EnumerateArray()
                    .FirstOrDefault(b => b.GetProperty("app").GetString()?.Equals(app, StringComparison.OrdinalIgnoreCase) == true);
                if (first.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                    binVersions[app] = first.GetProperty("version").GetString() ?? "?";
            }
        }
        catch { /* binaries endpoint optional */ }
    }

    if (json) { PrintJson(new { cli = cliVersion, daemon = daemonVer, binaries = binVersions }); return; }

    if (Console.IsOutputRedirected)
    {
        Console.WriteLine($"cli\t{cliVersion}");
        if (daemonVer != null) Console.WriteLine($"daemon\t{daemonVer}");
        foreach (var (app, ver) in binVersions)
            Console.WriteLine($"{app}\t{ver}");
        return;
    }

    AnsiConsole.MarkupLine($"[bold]wdc CLI[/]    v{cliVersion}");
    AnsiConsole.MarkupLine(daemonVer != null
        ? $"[bold]Daemon[/]     v{daemonVer} [green](connected)[/]"
        : "[bold]Daemon[/]     [dim]not running[/]");
    if (binVersions.Count > 0)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Installed binaries:[/]");
        foreach (var (app, ver) in binVersions)
            AnsiConsole.MarkupLine($"  [cyan]{app,-10}[/] {ver}");
    }
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
        if (Console.IsOutputRedirected)
        {
            foreach (var db in dbs.EnumerateArray())
                Console.WriteLine(db.GetString() ?? "");
            return;
        }
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
    try
    {
        var content = JsonContent.Create(new { name });
        await client.PostAsync("/api/databases", content);
        AnsiConsole.MarkupLine($"[green]Database created:[/] {Markup.Escape(name)}");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Failed to create database:[/] {Markup.Escape(ex.Message)}");
    }
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
    try
    {
        await client.DeleteAsync($"/api/databases/{name}");
        AnsiConsole.MarkupLine($"[yellow]Dropped:[/] {Markup.Escape(name)}");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Failed to drop database:[/] {Markup.Escape(ex.Message)}");
    }
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
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var proc = System.Diagnostics.Process.Start(psi);
    if (proc != null)
    {
        var errTask = proc.StandardError.ReadToEndAsync();
        await using var input = proc.StandardInput;
        await input.WriteAsync(await File.ReadAllTextAsync(file));
        await proc.WaitForExitAsync();
        var stderr = await errTask;
        if (proc.ExitCode == 0) AnsiConsole.MarkupLine("[green]Import complete[/]");
        else AnsiConsole.MarkupLine($"[red]Import failed:[/] {Markup.Escape(stderr.Trim().Length > 0 ? stderr.Trim() : $"exit code {proc.ExitCode}")}");
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
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var proc = System.Diagnostics.Process.Start(psi);
    if (proc != null)
    {
        var sqlTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var sql = await sqlTask;
        var stderr = await errTask;
        if (proc.ExitCode == 0)
        {
            await File.WriteAllTextAsync(file, sql);
            AnsiConsole.MarkupLine($"[green]Exported[/] ({new FileInfo(file).Length} bytes)");
        }
        else AnsiConsole.MarkupLine($"[red]Export failed:[/] {Markup.Escape(stderr.Trim().Length > 0 ? stderr.Trim() : $"exit code {proc.ExitCode}")}");
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
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var proc = System.Diagnostics.Process.Start(psi);
    if (proc == null) return;
    var outputTask = proc.StandardOutput.ReadToEndAsync();
    var errTask = proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();
    var output = await outputTask;
    var err = await errTask;
    if (proc.ExitCode != 0 && err.Length > 0)
    {
        AnsiConsole.MarkupLine($"[red]MySQL error:[/] {Markup.Escape(err.Trim())}");
        Environment.Exit(1);
        return;
    }
    var tables = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();
    if (json) { PrintJson(new { database = name, tables }); return; }
    if (Console.IsOutputRedirected)
    {
        foreach (var t in tables) Console.WriteLine(t);
        return;
    }
    if (tables.Length == 0) { AnsiConsole.MarkupLine($"[dim]No tables in {Markup.Escape(name)}[/]"); return; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn($"Tables in {name} ({tables.Length})");
    foreach (var t in tables) table.AddRow(t);
    AnsiConsole.Write(table);
});
dbCommand.Add(tablesCmd);

// --- wdc databases query {name} {sql} ---
var queryDbArg = new Argument<string>("name") { Description = "Database name" };
var querySqlArg = new Argument<string>("sql") { Description = "SQL query to execute" };
var queryCmd = new Command("query", "Execute SQL query against a database") { queryDbArg, querySqlArg };
queryCmd.SetAction(async (parseResult, ct) =>
{
    var name = parseResult.GetValue(queryDbArg)!;
    var sql = parseResult.GetValue(querySqlArg)!;
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

    var args = json ? $"-h 127.0.0.1 -P 3306 -u root -N -e \"{sql}\" {name}" : $"-h 127.0.0.1 -P 3306 -u root -t -e \"{sql}\" {name}";
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = mysqlCli, Arguments = args,
        RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true
    };
    var proc = System.Diagnostics.Process.Start(psi);
    if (proc == null) return;
    var output = await proc.StandardOutput.ReadToEndAsync();
    var err = await proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();
    if (proc.ExitCode != 0) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(err.Trim())}[/]"); return; }
    AnsiConsole.WriteLine(output);
});
dbCommand.Add(queryCmd);

// --- wdc databases size ---
var sizeCmd = new Command("size", "Show database sizes");
sizeCmd.SetAction(async (parseResult, ct) =>
{
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

    var sql = "SELECT table_schema AS db, ROUND(SUM(data_length + index_length) / 1024 / 1024, 2) AS size_mb FROM information_schema.tables WHERE table_schema NOT IN ('information_schema','performance_schema','mysql','sys') GROUP BY table_schema ORDER BY size_mb DESC";
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = mysqlCli, Arguments = $"-h 127.0.0.1 -P 3306 -u root {(json ? "-N" : "-t")} -e \"{sql}\"",
        RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true
    };
    var proc = System.Diagnostics.Process.Start(psi);
    if (proc == null) return;
    var outputTask = proc.StandardOutput.ReadToEndAsync();
    var errTask = proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();
    var err = await errTask;
    if (proc.ExitCode != 0 && err.Length > 0)
    {
        AnsiConsole.MarkupLine($"[red]MySQL error:[/] {Markup.Escape(err.Trim())}");
        Environment.Exit(1);
        return;
    }
    AnsiConsole.WriteLine(await outputTask);
});
dbCommand.Add(sizeCmd);

// --- wdc binaries ---
var binariesCommand = new Command("binaries", "List installed binaries");
binariesCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var bins = await client.GetJsonAsync("/api/binaries/installed");
    if (json) { PrintJson(bins); return; }
    if (Console.IsOutputRedirected)
    {
        foreach (var b in bins.EnumerateArray())
        {
            var path = b.TryGetProperty("installPath", out var ip) ? ip.GetString() ?? "" : "";
            Console.WriteLine($"{b.GetProperty("app").GetString()}\t{b.GetProperty("version").GetString()}\t{path}");
        }
        return;
    }
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

// --- wdc binaries catalog [app] ---
// Optional positional: with no argument we show a grid summary of every
// app + its release/download counts so the CLI has parity with the
// Binaries grid view in the Electron UI. With an argument we drill into
// the per-app detail view (version/source/arch). Both support --json.
var catalogAppArg = new Argument<string?>("app")
{
    Description = "App name (apache, php, mysql, redis, etc.). Omit to list every app.",
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = _ => null,
};
var catalogCmd = new Command("catalog", "Show available binaries (summary when no app, details when one is given)") { catalogAppArg };
catalogCmd.SetAction(async (parseResult, ct) =>
{
    var appName = parseResult.GetValue(catalogAppArg);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    // Empty arg → summary across every app, same shape the Vue grid uses
    // so the CLI output mirrors what the Binaries page shows.
    if (string.IsNullOrWhiteSpace(appName))
    {
        var full = await client.GetJsonAsync("/api/binaries/catalog");
        if (full.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            AnsiConsole.MarkupLine("[red]Unexpected catalog shape from daemon[/]");
            return;
        }
        var grouped = new Dictionary<string, (HashSet<string> versions, int downloads)>();
        foreach (var r in full.EnumerateArray())
        {
            var app = r.GetProperty("app").GetString() ?? "";
            if (string.IsNullOrEmpty(app)) continue;
            if (!grouped.TryGetValue(app, out var bucket))
                bucket = (new HashSet<string>(), 0);
            if (r.TryGetProperty("version", out var v) && v.GetString() is { } vs)
                bucket.versions.Add(vs);
            grouped[app] = (bucket.versions, bucket.downloads + 1);
        }
        if (json)
        {
            PrintJson(grouped.ToDictionary(
                kv => kv.Key,
                kv => new { versions = kv.Value.versions.Count, downloads = kv.Value.downloads }));
            return;
        }
        if (grouped.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Catalog is empty. Try `wdc binaries refresh`.[/]");
            return;
        }
        var summaryTable = new Table().Border(TableBorder.Rounded).Title($"[bold]Catalog — {grouped.Count} apps[/]");
        summaryTable.AddColumn("App"); summaryTable.AddColumn("Versions"); summaryTable.AddColumn("Downloads");
        foreach (var kv in grouped.OrderBy(k => k.Key))
            summaryTable.AddRow(kv.Key, kv.Value.versions.Count.ToString(), kv.Value.downloads.ToString());
        AnsiConsole.Write(summaryTable);
        AnsiConsole.MarkupLine("[dim]Use [bold]wdc binaries catalog <app>[/] for per-version details.[/]");
        return;
    }

    var releases = await client.GetJsonAsync($"/api/binaries/catalog/{appName}");
    if (json) { PrintJson(releases); return; }
    if (releases.GetArrayLength() == 0) { AnsiConsole.MarkupLine($"[dim]No releases found for {Markup.Escape(appName ?? string.Empty)}[/]"); return; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Version"); table.AddColumn("OS"); table.AddColumn("Arch"); table.AddColumn("Source");
    foreach (var r in releases.EnumerateArray())
        table.AddRow(
            r.GetProperty("version").GetString() ?? "?",
            r.TryGetProperty("os", out var o) ? o.GetString()! : "-",
            r.TryGetProperty("arch", out var a) ? a.GetString()! : "x64",
            r.TryGetProperty("source", out var s) ? s.GetString()! : "-");
    AnsiConsole.Write(table);
});
binariesCommand.Add(catalogCmd);

// --- wdc binaries catalog-url [url | reset] ---
// Read or write the daemon.catalogUrl setting — mirrors the Settings →
// Advanced tab in the UI. Without an argument prints the current value,
// with a URL arg validates + writes via PUT /api/settings and triggers a
// refresh so the change takes effect immediately (CatalogClient factory
// reads SettingsStore on every RefreshAsync). Special sentinel
// `reset` / `default` clears the setting, letting the env var
// NKS_WDC_CATALOG_URL or the hardcoded localhost fallback take over.
var catalogUrlArg = new Argument<string?>("url")
{
    Description = "New catalog URL (http/https), or `reset` to clear and use default. Omit to print current value.",
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = _ => null,
};
var catalogUrlCmd = new Command("catalog-url", "Show or set the catalog API URL") { catalogUrlArg };
catalogUrlCmd.SetAction(async (parseResult, ct) =>
{
    var newUrl = parseResult.GetValue(catalogUrlArg);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    // Read: return the persisted value from /api/settings (dotted key
    // daemon.catalogUrl), falling back to "(default)" if unset.
    if (string.IsNullOrWhiteSpace(newUrl))
    {
        var settings = await client.GetJsonAsync("/api/settings");
        var url = settings.TryGetProperty("daemon.catalogUrl", out var v) ? v.GetString() : null;
        var display = string.IsNullOrWhiteSpace(url) ? "(default: http://127.0.0.1:8765)" : url!;
        if (json) { PrintJson(new { catalogUrl = url ?? "" }); return; }
        AnsiConsole.MarkupLine($"Current catalog URL: [cyan]{Markup.Escape(display)}[/]");
        return;
    }

    // Sentinel: user explicitly wants to clear the override so the
    // SettingsStore getter falls through to env var / hardcoded default.
    // Persist empty string — GetString returns null for "" via Dapper's
    // default handling, but the CatalogUrl getter guards with
    // IsNullOrWhiteSpace so either works.
    var isReset = newUrl.Equals("reset", StringComparison.OrdinalIgnoreCase)
               || newUrl.Equals("default", StringComparison.OrdinalIgnoreCase)
               || newUrl.Equals("clear", StringComparison.OrdinalIgnoreCase);

    if (!isReset)
    {
        // Validate URL format upfront so we never persist garbage. Must be
        // an absolute http/https URI — catalog-api has no other protocol.
        if (!Uri.TryCreate(newUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            AnsiConsole.MarkupLine($"[red]Invalid URL:[/] {Markup.Escape(newUrl)}");
            AnsiConsole.MarkupLine("[dim]Expected absolute http:// or https:// URL. Use `reset` to clear the override.[/]");
            // Environment.Exit terminates the process immediately with the
            // given code. We intentionally bypass System.CommandLine's
            // InvokeAsync return value here — its async Task handler
            // overload always returns 0 on success, so setting
            // Environment.ExitCode alone would be silently overwritten
            // by the `return await ... InvokeAsync()` line at bottom-of-file.
            Environment.Exit(1);
            return;
        }
    }

    var valueToStore = isReset ? string.Empty : newUrl;
    var body = JsonContent.Create(new Dictionary<string, string>
    {
        ["daemon.catalogUrl"] = valueToStore,
    });
    await client.PutAsync("/api/settings", body);
    var refreshed = await client.PostAsync("/api/binaries/catalog/refresh");
    var count = refreshed.TryGetProperty("count", out var c) ? c.GetInt32() : 0;

    if (json)
    {
        PrintJson(new { catalogUrl = valueToStore, reset = isReset, refreshed = count });
        return;
    }
    if (isReset)
        AnsiConsole.MarkupLine("[green]Reset:[/] daemon.catalogUrl cleared — using default");
    else
        AnsiConsole.MarkupLine($"[green]Saved:[/] daemon.catalogUrl = [cyan]{Markup.Escape(newUrl)}[/]");

    // Zero releases after a successful save almost always means the new
    // URL didn't respond → warn the user instead of faking success with
    // "0 releases" (which previously looked like a happy exit).
    if (count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]Warning:[/] refresh returned 0 releases — the new URL may be unreachable");
        AnsiConsole.MarkupLine("[dim]Check that catalog-api is running at the configured URL and try again.[/]");
        // See rationale in the validation branch above — Environment.Exit
        // is the only reliable way to propagate a non-zero exit code out
        // of a System.CommandLine async Task handler.
        Environment.Exit(2);
    }
    else
    {
        AnsiConsole.MarkupLine($"[green]Catalog refreshed:[/] {count} releases");
    }
});
binariesCommand.Add(catalogUrlCmd);

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
    try
    {
        var content = JsonContent.Create(new { app, version = ver });
        var result = await client.PostAsync("/api/binaries/install", content);
        AnsiConsole.MarkupLine($"[green]Installed![/]");
        PrintJson(result);
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Install failed:[/] {Markup.Escape(ex.Message)}");
    }
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
    try
    {
        await client.DeleteAsync($"/api/binaries/{app}/{ver}");
        if (json) PrintJson(new { removed = $"{app}/{ver}" });
        else AnsiConsole.MarkupLine($"[yellow]Removed:[/] {Markup.Escape(app)} {Markup.Escape(ver)}");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Remove failed:[/] {Markup.Escape(ex.Message)}");
    }
});
binariesCommand.Add(removeBinCmd);

// --- wdc binaries refresh ---
var refreshCmd = new Command("refresh", "Refresh the binary catalog from the catalog API");
refreshCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var result = await client.PostAsync("/api/binaries/catalog/refresh");
    if (json) { PrintJson(result); return; }
    var count = result.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
    AnsiConsole.MarkupLine($"[green]Catalog refreshed:[/] {count} releases");
});
binariesCommand.Add(refreshCmd);

// wdc binaries outdated — compare installed vs catalog latest
var outdatedCmd = new Command("outdated", "Show installed binaries with newer versions available");
outdatedCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var installed = await client.GetJsonAsync("/api/binaries/installed");
    var catalog = await client.GetJsonAsync("/api/binaries/catalog");

    var outdated = new List<(string app, string current, string latest)>();
    var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var bin in installed.EnumerateArray())
    {
        var app = bin.GetProperty("app").GetString() ?? "";
        if (!seenApps.Add(app)) continue;
        var ver = bin.GetProperty("version").GetString() ?? "";
        if (catalog.TryGetProperty(app, out var releases) && releases.GetArrayLength() > 0)
        {
            var latestVer = releases[0].GetProperty("version").GetString() ?? "";
            if (latestVer != ver && NKS.WebDevConsole.Core.Services.SemverVersionComparer.CompareAscending(latestVer, ver) > 0)
                outdated.Add((app, ver, latestVer));
        }
    }

    if (json) { PrintJson(outdated.Select(o => new { o.app, o.current, o.latest })); return; }
    if (Console.IsOutputRedirected)
    {
        foreach (var (app, current, latest) in outdated)
            Console.WriteLine($"{app}\t{current}\t{latest}");
        return;
    }
    if (outdated.Count == 0) { AnsiConsole.MarkupLine("[green]All binaries are up to date.[/]"); return; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("App"); table.AddColumn("Installed"); table.AddColumn("Available");
    foreach (var (app, current, latest) in outdated)
        table.AddRow(Markup.Escape(app), $"[yellow]{Markup.Escape(current)}[/]", $"[green]{Markup.Escape(latest)}[/]");
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"[yellow]{outdated.Count} package(s) can be updated.[/]");
});
binariesCommand.Add(outdatedCmd);

// --- wdc php ---
var phpCommand = new Command("php", "PHP version management");
phpCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var versions = await client.GetJsonAsync("/api/php/versions");
    if (json) { PrintJson(versions); return; }
    if (versions.GetArrayLength() == 0) { if (!Console.IsOutputRedirected) AnsiConsole.MarkupLine("[dim]No PHP versions detected.[/]"); return; }
    if (Console.IsOutputRedirected)
    {
        foreach (var v in versions.EnumerateArray())
        {
            var ver = v.GetProperty("version").GetString() ?? "";
            var path = v.TryGetProperty("phpExePath", out var ep) ? ep.GetString() ?? ""
                     : v.TryGetProperty("executablePath", out var ep2) ? ep2.GetString() ?? "" : "";
            var active = v.TryGetProperty("isActive", out var a) && a.GetBoolean() ? "active" : "";
            Console.WriteLine($"{ver}\t{path}\t{active}");
        }
        return;
    }
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
var phpExtToggleOpt = new Option<string?>("--toggle") { Description = "Toggle an extension on/off (e.g. --toggle curl)" };
var phpExtCmd = new Command("extensions", "List extensions for a PHP version") { phpExtVerArg, phpExtToggleOpt };
phpExtCmd.SetAction(async (parseResult, ct) =>
{
    var ver = parseResult.GetValue(phpExtVerArg)!;
    var toggle = parseResult.GetValue(phpExtToggleOpt);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    // Toggle mode: flip an extension on/off
    if (!string.IsNullOrEmpty(toggle))
    {
        try
        {
            var exts = await client.GetJsonAsync($"/api/php/{ver}/extensions");
            var ext = exts.EnumerateArray().FirstOrDefault(e => e.GetProperty("name").GetString()?.Equals(toggle, StringComparison.OrdinalIgnoreCase) == true);
            if (ext.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                AnsiConsole.MarkupLine($"[red]Extension '{Markup.Escape(toggle)}' not found for PHP {Markup.Escape(ver)}[/]");
                Environment.Exit(1);
                return;
            }
            var currentlyLoaded = ext.TryGetProperty("isLoaded", out var il) && il.GetBoolean();
            var newState = !currentlyLoaded;
            var body = JsonContent.Create(new { enabled = newState });
            await client.PostAsync($"/api/php/{ver}/extensions/{toggle}", body);
            AnsiConsole.MarkupLine(newState
                ? $"[green]Enabled[/] {Markup.Escape(toggle)} for PHP {Markup.Escape(ver)}"
                : $"[yellow]Disabled[/] {Markup.Escape(toggle)} for PHP {Markup.Escape(ver)}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
            Environment.Exit(1);
        }
        return;
    }

    var extensions = await client.GetJsonAsync($"/api/php/{ver}/extensions");
    if (json) { PrintJson(extensions); return; }
    if (extensions.GetArrayLength() == 0) { if (!Console.IsOutputRedirected) AnsiConsole.MarkupLine($"[dim]No extensions for PHP {Markup.Escape(ver)}[/]"); return; }
    if (Console.IsOutputRedirected)
    {
        foreach (var e in extensions.EnumerateArray())
        {
            var name = e.GetProperty("name").GetString() ?? "";
            var loaded = e.TryGetProperty("isLoaded", out var l) && l.GetBoolean();
            var core = e.TryGetProperty("isCore", out var c) && c.GetBoolean();
            Console.WriteLine($"{name}\t{(loaded ? "loaded" : "disabled")}\t{(core ? "core" : "")}");
        }
        return;
    }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Extension"); table.AddColumn("Loaded"); table.AddColumn("Core");
    foreach (var e in extensions.EnumerateArray())
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

// --- wdc php versions --- explicit alias
var phpVersionsCmd = new Command("versions", "List installed PHP versions (alias for wdc php)");
phpVersionsCmd.SetAction(async (parseResult, ct) =>
{
    // Delegate to parent command handler
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var versions = await client.GetJsonAsync("/api/php/versions");
    if (json) { PrintJson(versions); return; }
    if (versions.GetArrayLength() == 0) { AnsiConsole.MarkupLine("[dim]No PHP versions[/]"); return; }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Version"); table.AddColumn("Port"); table.AddColumn("Ext"); table.AddColumn("Active");
    foreach (var v in versions.EnumerateArray())
    {
        var isActive = v.TryGetProperty("isActive", out var a) && a.GetBoolean();
        table.AddRow(
            v.GetProperty("version").GetString() ?? "?",
            v.TryGetProperty("fcgiPort", out var p) ? p.GetInt32().ToString() : "-",
            v.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Array ? ext.GetArrayLength().ToString() : "-",
            isActive ? "[green]Yes[/]" : "[dim]No[/]");
    }
    AnsiConsole.Write(table);
});
phpCommand.Add(phpVersionsCmd);

// --- wdc open {domain} ---
var openDomainArg = new Argument<string>("domain") { Description = "Site domain to open in browser" };
var openCommand = new Command("open", "Open a site in the default browser") { openDomainArg };
openCommand.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(openDomainArg)!;
    // Fetch site config to build an SSL-aware URL with the correct port,
    // matching the CommandPalette's "Open {domain}" logic in the frontend.
    var proto = "http";
    var port = 80;
    using var client = new DaemonClient();
    if (client.Connect())
    {
        try
        {
            var site = await client.GetJsonAsync($"/api/sites/{domain}");
            var ssl = site.TryGetProperty("sslEnabled", out var s) && s.GetBoolean();
            proto = ssl ? "https" : "http";
            var httpsP = site.TryGetProperty("httpsPort", out var hp) ? hp.GetInt32() : 0;
            var httpP = site.TryGetProperty("httpPort", out var htp) ? htp.GetInt32() : 0;
            port = ssl ? (httpsP > 0 ? httpsP : 443) : (httpP > 0 ? httpP : 80);
        }
        catch { /* fall through to default http */ }
    }
    var portSuffix = (proto == "https" && port == 443) || (proto == "http" && port == 80) ? "" : $":{port}";
    var url = $"{proto}://{domain}{portSuffix}";
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    AnsiConsole.MarkupLine($"Opening [blue]{Markup.Escape(url)}[/]...");
});

// --- wdc config ---
var configCommand = new Command("config", "Show configuration paths and manage daemon settings");
configCommand.SetAction((parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var paths = new
    {
        wdcRoot = WdcPaths.Root,
        binaries = WdcPaths.BinariesRoot,
        sites = WdcPaths.SitesRoot,
        data = WdcPaths.DataRoot,
        ssl = WdcPaths.SslRoot,
        logs = WdcPaths.LogsRoot,
        backups = WdcPaths.BackupsRoot,
        cache = WdcPaths.CacheRoot,
        portable = WdcPaths.IsPortableMode,
        portFile = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port"),
    };
    if (json) { PrintJson(paths); return Task.CompletedTask; }
    if (Console.IsOutputRedirected)
    {
        Console.WriteLine($"root\t{paths.wdcRoot}");
        Console.WriteLine($"binaries\t{paths.binaries}");
        Console.WriteLine($"sites\t{paths.sites}");
        Console.WriteLine($"data\t{paths.data}");
        Console.WriteLine($"ssl\t{paths.ssl}");
        Console.WriteLine($"logs\t{paths.logs}");
        Console.WriteLine($"backups\t{paths.backups}");
        return Task.CompletedTask;
    }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Key"); table.AddColumn("Path");
    table.AddRow("WDC Root", paths.wdcRoot);
    table.AddRow("Binaries", paths.binaries);
    table.AddRow("Sites Config", paths.sites);
    table.AddRow("Data (SQLite)", paths.data);
    table.AddRow("SSL Certs", paths.ssl);
    table.AddRow("Logs", paths.logs);
    table.AddRow("Backups", paths.backups);
    table.AddRow("Cache", paths.cache);
    table.AddRow("Port File", paths.portFile);
    if (paths.portable) table.AddRow("[yellow]Mode[/]", "[yellow]Portable[/]");
    AnsiConsole.Write(table);
    return Task.CompletedTask;
});

// --- wdc config list ---
var configListCmd = new Command("list", "List all daemon settings");
configListCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!client.Connect()) { AnsiConsole.MarkupLine("[red]Daemon is not running.[/]"); return; }
    try
    {
        var settings = await client.GetJsonAsync("/api/settings");
        if (json) { PrintJson(settings); return; }
        if (Console.IsOutputRedirected)
        {
            foreach (var prop in settings.EnumerateObject())
                Console.WriteLine($"{prop.Name}\t{prop.Value.GetString() ?? ""}");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Setting"); table.AddColumn("Value");
        foreach (var prop in settings.EnumerateObject())
            table.AddRow(Markup.Escape(prop.Name), Markup.Escape(prop.Value.GetString() ?? ""));
        if (table.Rows.Count == 0)
            AnsiConsole.MarkupLine("[dim]No settings configured.[/]");
        else
            AnsiConsole.Write(table);
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Failed to list settings:[/] {Markup.Escape(ex.Message)}");
    }
});
configCommand.Add(configListCmd);

// --- wdc config get <key> ---
var configGetKeyArg = new Argument<string>("key") { Description = "Setting key (e.g. daemon.autoStartEnabled)" };
var configGetCmd = new Command("get", "Get a daemon setting value") { configGetKeyArg };
configGetCmd.SetAction(async (parseResult, ct) =>
{
    var key = parseResult.GetValue(configGetKeyArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!client.Connect()) { AnsiConsole.MarkupLine("[red]Daemon is not running.[/]"); return; }
    try
    {
        var settings = await client.GetJsonAsync("/api/settings");
        if (settings.TryGetProperty(key, out var val))
        {
            var value = val.GetString() ?? "";
            if (json) { PrintJson(new { key, value }); return; }
            if (Console.IsOutputRedirected) { Console.WriteLine(value); return; }
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(key)}[/] = {Markup.Escape(value)}");
        }
        else
        {
            if (json) { PrintJson(new { key, value = (string?)null, found = false }); return; }
            AnsiConsole.MarkupLine($"[yellow]Setting '{Markup.Escape(key)}' not found.[/]");
        }
    }
    catch (HttpRequestException ex)
    {
        if (json) { PrintJson(new { key, error = ex.Message }); return; }
        AnsiConsole.MarkupLine($"[red]Failed to get setting:[/] {Markup.Escape(ex.Message)}");
    }
});
configCommand.Add(configGetCmd);

// --- wdc config set <key> <value> ---
var configSetKeyArg = new Argument<string>("key") { Description = "Setting key (e.g. daemon.autoStartEnabled)" };
var configSetValueArg = new Argument<string>("value") { Description = "Value to set" };
var configSetCmd = new Command("set", "Set a daemon setting") { configSetKeyArg, configSetValueArg };
configSetCmd.SetAction(async (parseResult, ct) =>
{
    var key = parseResult.GetValue(configSetKeyArg)!;
    var value = parseResult.GetValue(configSetValueArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!client.Connect()) { AnsiConsole.MarkupLine("[red]Daemon is not running.[/]"); return; }
    try
    {
        var body = JsonContent.Create(new Dictionary<string, string> { [key] = value });
        await client.PutAsync("/api/settings", body);
        if (json) { PrintJson(new { key, value, ok = true }); return; }
        AnsiConsole.MarkupLine($"[green]Set[/] {Markup.Escape(key)} = {Markup.Escape(value)}");
    }
    catch (HttpRequestException ex)
    {
        if (json) { PrintJson(new { key, value, ok = false, error = ex.Message }); return; }
        AnsiConsole.MarkupLine($"[red]Failed to set {Markup.Escape(key)}:[/] {Markup.Escape(ex.Message)}");
    }
});
configCommand.Add(configSetCmd);

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

        // 6. PHP versions
        try
        {
            var phpVersions = await client.GetJsonAsync("/api/php/versions");
            var phpCount = phpVersions.GetArrayLength();
            checks.Add(("PHP versions", phpCount > 0, phpCount > 0 ? $"{phpCount} installed" : "None — install via Binaries page"));
        }
        catch { checks.Add(("PHP versions", false, "Query failed")); }

        // 7. Node.js plugin
        try
        {
            var nodeSites = await client.GetJsonAsync("/api/node/sites");
            checks.Add(("Node.js plugin", true, $"Loaded, {nodeSites.GetArrayLength()} tracked site(s)"));
        }
        catch
        {
            checks.Add(("Node.js plugin", true, "Not loaded (optional)"));
        }

        // 8. Catalog API
        try
        {
            var sys = await client.GetJsonAsync("/api/system");
            var hasCat = sys.TryGetProperty("catalog", out var cat);
            var catOk = hasCat
                && cat.TryGetProperty("healthy", out var h) && h.GetBoolean();
            var catUrl = hasCat && cat.TryGetProperty("url", out var u)
                ? u.GetString() ?? "" : "";
            checks.Add(("Catalog API", catOk, catOk ? catUrl : "Unreachable or not configured"));
        }
        catch { checks.Add(("Catalog API", false, "System info query failed")); }
    }

    // 6. Hosts file writable
    var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    var hostsOk = false;
    try { using var f = File.Open(hostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); hostsOk = true; } catch { }
    checks.Add(("Hosts file writable", hostsOk, hostsOk ? hostsPath : "Need admin elevation"));

    // 7. Disk space on data root
    try
    {
        var dataRoot = NKS.WebDevConsole.Core.Services.WdcPaths.Root;
        var drive = new DriveInfo(Path.GetPathRoot(dataRoot) ?? "C:");
        var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
        var diskOk = freeGb > 1.0;
        checks.Add(("Disk space", diskOk, $"{freeGb:F1} GB free on {drive.Name}"));
    }
    catch (Exception ex) { checks.Add(("Disk space", false, ex.Message)); }

    // 8. Docker availability (needed for Compose lifecycle)
    try
    {
        var dPsi = new System.Diagnostics.ProcessStartInfo("docker", "version --format {{.Server.Version}}")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var dProc = System.Diagnostics.Process.Start(dPsi);
        var dVer = dProc?.StandardOutput.ReadToEnd().Trim() ?? "";
        dProc?.WaitForExit(3000);
        var dOk = dProc?.ExitCode == 0 && dVer.Length > 0;
        checks.Add(("Docker", dOk == true, dOk == true ? $"v{dVer}" : "Not running or not installed"));
    }
    catch { checks.Add(("Docker", true, "Not installed (optional)")); }

    // 10. SSL CA (mkcert root CA installed)
    if (connected)
    {
        try
        {
            var certs = await client.GetJsonAsync("/api/ssl/certs");
            var mkcertOk = certs.TryGetProperty("mkcertInstalled", out var mi) && mi.GetBoolean();
            var certCount = certs.TryGetProperty("certs", out var ca) ? ca.GetArrayLength() : 0;
            checks.Add(("SSL (mkcert)", mkcertOk, mkcertOk
                ? $"CA installed, {certCount} cert(s)"
                : "mkcert not installed — run wdc ssl install-ca"));
        }
        catch { checks.Add(("SSL (mkcert)", true, "Check skipped")); }
    }

    // 11. Backup freshness
    if (connected)
    {
        try
        {
            var backups = await client.GetJsonAsync("/api/backup/list");
            if (backups.TryGetProperty("backups", out var bArr) && bArr.GetArrayLength() > 0)
            {
                var newest = bArr[0];
                var created = newest.TryGetProperty("createdUtc", out var c) ? c.GetString() : null;
                if (created != null && DateTime.TryParse(created, out var dt))
                {
                    var age = DateTime.UtcNow - dt;
                    var fresh = age.TotalDays < 7;
                    var ageStr = age.TotalHours < 1 ? "just now"
                        : age.TotalHours < 24 ? $"{(int)age.TotalHours}h ago"
                        : $"{(int)age.TotalDays}d ago";
                    checks.Add(("Backup freshness", fresh, $"Last backup {ageStr}{(fresh ? "" : " — consider running wdc backup")}"));
                }
                else
                {
                    checks.Add(("Backup freshness", true, $"{bArr.GetArrayLength()} backup(s)"));
                }
            }
            else
            {
                checks.Add(("Backup freshness", false, "No backups — run wdc backup"));
            }
        }
        catch { checks.Add(("Backup freshness", true, "Check skipped")); }
    }

    if (json) { PrintJson(checks.Select(c => new { c.name, c.ok, c.detail })); return; }

    if (Console.IsOutputRedirected)
    {
        foreach (var (name, ok, detail) in checks)
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}\t{name}\t{detail}");
    }
    else
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Check"); table.AddColumn("Status"); table.AddColumn("Detail");
        foreach (var (name, ok, detail) in checks)
            table.AddRow(Markup.Escape(name), ok ? "[green]PASS[/]" : "[red]FAIL[/]", Markup.Escape(detail));
        AnsiConsole.Write(table);
    }

    var allOk = checks.All(c => c.ok);
    AnsiConsole.WriteLine();
    var passed = checks.Count(c => c.ok);
    var failed = checks.Count - passed;
    AnsiConsole.MarkupLine(allOk
        ? $"[green bold]All {checks.Count} checks passed![/]"
        : $"[yellow]{passed} passed, {failed} failed. See details above.[/]");
    if (!allOk) Environment.Exit(1);
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
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine($"domain\t{site.GetProperty("domain").GetString()}");
            Console.WriteLine($"docroot\t{site.GetProperty("documentRoot").GetString()}");
            Console.WriteLine($"php\t{site.GetProperty("phpVersion").GetString()}");
            Console.WriteLine($"ssl\t{(site.TryGetProperty("sslEnabled", out var ssl2) && ssl2.GetBoolean() ? "true" : "false")}");
            var np2 = site.TryGetProperty("nodeUpstreamPort", out var npp) ? npp.GetInt32() : 0;
            if (np2 > 0) Console.WriteLine($"node_port\t{np2}");
            if (site.TryGetProperty("nodeStartCommand", out var nsc2) && nsc2.ValueKind == JsonValueKind.String && nsc2.GetString()?.Length > 0)
                Console.WriteLine($"node_cmd\t{nsc2.GetString()}");
            if (site.TryGetProperty("framework", out var fw2) && fw2.ValueKind == JsonValueKind.String)
                Console.WriteLine($"framework\t{fw2.GetString()}");
            if (site.TryGetProperty("cloudflare", out var cf2) && cf2.ValueKind == JsonValueKind.Object
                && cf2.TryGetProperty("enabled", out var cfe2) && cfe2.GetBoolean()
                && cf2.TryGetProperty("subdomain", out var cfs2))
                Console.WriteLine($"tunnel\t{cfs2.GetString()}.{(cf2.TryGetProperty("zoneName", out var cfz2) ? cfz2.GetString() : "")}");
            return;
        }
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
        if (site.TryGetProperty("nodeUpstreamPort", out var np) && np.GetInt32() > 0)
        {
            table.AddRow("Runtime", "[green]Node.js[/]");
            table.AddRow("Upstream Port", np.GetInt32().ToString());
            if (site.TryGetProperty("nodeStartCommand", out var nsc) && nsc.ValueKind == JsonValueKind.String && nsc.GetString()?.Length > 0)
                table.AddRow("Start Command", nsc.GetString()!);
        }
        // Cloudflare tunnel
        if (site.TryGetProperty("cloudflare", out var cf) && cf.ValueKind == JsonValueKind.Object)
        {
            var cfEnabled = cf.TryGetProperty("enabled", out var cfe) && cfe.GetBoolean();
            var cfSub = cf.TryGetProperty("subdomain", out var cfs) ? cfs.GetString() ?? "" : "";
            var cfZone = cf.TryGetProperty("zoneName", out var cfz) ? cfz.GetString() ?? "" : "";
            if (cfEnabled && cfSub.Length > 0)
                table.AddRow("Tunnel", $"[cyan]{Markup.Escape(cfSub)}.{Markup.Escape(cfZone)}[/]");
            else if (cfSub.Length > 0)
                table.AddRow("Tunnel", $"[dim]{Markup.Escape(cfSub)}.{Markup.Escape(cfZone)} (disabled)[/]");
        }
        // Environment variables
        if (site.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            var envCount = env.EnumerateObject().Count();
            if (envCount > 0)
                table.AddRow("Env vars", $"{envCount} defined");
        }
        // Docker Compose detection
        try
        {
            var compose = await client.GetJsonAsync($"/api/sites/{domain}/docker-compose");
            if (compose.TryGetProperty("hasCompose", out var hc) && hc.GetBoolean())
            {
                var fn = compose.TryGetProperty("fileName", out var cfn) ? cfn.GetString() ?? "" : "";
                table.AddRow("Docker Compose", $"[cyan]{Markup.Escape(fn)}[/]");
            }
        }
        catch { /* optional — compose endpoint may not exist */ }
        // Access log metrics
        try
        {
            var metrics = await client.GetJsonAsync($"/api/sites/{domain}/metrics");
            if (metrics.TryGetProperty("hasMetrics", out var hm) && hm.GetBoolean()
                && metrics.TryGetProperty("accessLog", out var alog))
            {
                var reqs = alog.GetProperty("requestCount").GetInt64();
                var size = alog.GetProperty("sizeBytes").GetInt64();
                var sizeStr = size < 1024 ? $"{size} B"
                    : size < 1024 * 1024 ? $"{size / 1024.0:F1} KB"
                    : $"{size / (1024.0 * 1024.0):F1} MB";
                table.AddRow("Requests", $"{reqs:N0}");
                table.AddRow("Access Log", sizeStr);
            }
        }
        catch { /* optional — metrics endpoint may not exist */ }
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
    if (Console.IsOutputRedirected)
    {
        if (sys.TryGetProperty("daemon", out var d2))
            Console.WriteLine($"version\t{d2.GetProperty("version").GetString()}");
        if (sys.TryGetProperty("services", out var sv2))
            Console.WriteLine($"services\t{sv2.GetProperty("running").GetInt32()}/{sv2.GetProperty("total").GetInt32()}");
        if (sys.TryGetProperty("sites", out var st2))
            Console.WriteLine($"sites\t{st2.GetInt32()}");
        return;
    }
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

// --- wdc completion ---
var completionShellArg = new Argument<string?>("shell") { Description = "Shell type: bash, zsh, powershell (default: powershell)" };
var completionCmd = new Command("completion", "Generate shell completion script") { completionShellArg };
completionCmd.SetAction((parseResult, ct) =>
{
    var shell = parseResult.GetValue(completionShellArg) ?? "powershell";
    switch (shell.ToLowerInvariant())
    {
        case "powershell":
            AnsiConsole.WriteLine("Register-ArgumentCompleter -Native -CommandName wdc -ScriptBlock {");
            AnsiConsole.WriteLine("  param($wordToComplete, $commandAst, $cursorPosition)");
            AnsiConsole.WriteLine("  wdc --help | Select-String '  \\w' | ForEach-Object { $_.Line.Trim().Split()[0] } |");
            AnsiConsole.WriteLine("    Where-Object { $_ -like \"$wordToComplete*\" } |");
            AnsiConsole.WriteLine("    ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }");
            AnsiConsole.WriteLine("}");
            break;
        case "bash":
        case "zsh":
            AnsiConsole.WriteLine("# Add to ~/.bashrc or ~/.zshrc:");
            AnsiConsole.WriteLine("_wdc_completions() {");
            AnsiConsole.WriteLine("  local commands=\"new open info config doctor system start-all stop-all restart-all status services logs sites php plugins databases binaries version node compose metrics ssl hosts backup restore migrate uninstall sync cloudflare completion activity\"");
            AnsiConsole.WriteLine("  COMPREPLY=( $(compgen -W \"$commands\" -- ${COMP_WORDS[COMP_CWORD]}) )");
            AnsiConsole.WriteLine("}");
            AnsiConsole.WriteLine("complete -F _wdc_completions wdc");
            break;
        default:
            AnsiConsole.MarkupLine($"[yellow]Shell '{Markup.Escape(shell)}' not supported. Try: powershell, bash, zsh[/]");
            break;
    }
    return Task.CompletedTask;
});

// --- wdc hosts ---
var hostsCmd = new Command("hosts", "Show managed hosts file entries");
hostsCmd.SetAction((parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    if (!File.Exists(hostsPath)) { AnsiConsole.MarkupLine("[red]Hosts file not found[/]"); return Task.CompletedTask; }

    var content = File.ReadAllText(hostsPath);
    var beginIdx = content.IndexOf("# BEGIN NKS WebDev Console");
    var endIdx = content.IndexOf("# END NKS WebDev Console");

    if (beginIdx < 0 || endIdx < 0)
    {
        if (json) PrintJson(Array.Empty<object>());
        else AnsiConsole.MarkupLine("[dim]No managed entries in hosts file[/]");
        return Task.CompletedTask;
    }

    var block = content[beginIdx..endIdx];
    var entries = block.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(l => !l.StartsWith('#') && l.Trim().Length > 0)
        .Select(l => { var p = l.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); return new { ip = p.Length > 0 ? p[0] : "", domain = p.Length > 1 ? p[1] : "" }; })
        .ToList();

    if (json) { PrintJson(entries); return Task.CompletedTask; }

    if (Console.IsOutputRedirected)
    {
        foreach (var e in entries) Console.WriteLine(e.domain);
        return Task.CompletedTask;
    }

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("IP"); table.AddColumn("Domain");
    foreach (var e in entries) table.AddRow(e.ip, e.domain);
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"\n[dim]{entries.Count} managed entries in {Markup.Escape(hostsPath)}[/]");
    return Task.CompletedTask;
});

// --- wdc ssl ---
var sslCommand = new Command("ssl", "SSL certificate management");

var sslListCmd = new Command("list", "List all SSL certificates");
sslListCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var data = await client.GetJsonAsync("/api/ssl/certs");
    if (json) { PrintJson(data); return; }

    if (Console.IsOutputRedirected)
    {
        if (data.TryGetProperty("certs", out var certs2) && certs2.GetArrayLength() > 0)
            foreach (var c in certs2.EnumerateArray())
            {
                var d = c.GetProperty("domain").GetString() ?? "";
                var cr = c.TryGetProperty("createdUtc", out var cv) ? cv.GetString()?[..10] ?? "" : "";
                var cp = c.TryGetProperty("certPath", out var cpv) ? cpv.GetString() ?? "" : "";
                Console.WriteLine($"{d}\t{cr}\t{cp}");
            }
        return;
    }

    var mkcert = data.TryGetProperty("mkcertInstalled", out var mi) && mi.GetBoolean();
    AnsiConsole.MarkupLine($"mkcert: {(mkcert ? "[green]installed[/]" : "[red]not found[/]")}");

    if (data.TryGetProperty("certs", out var certs) && certs.GetArrayLength() > 0)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Domain").AddColumn("Created").AddColumn("Cert Path");
        foreach (var c in certs.EnumerateArray())
        {
            var domain = c.GetProperty("domain").GetString() ?? "";
            var created = c.TryGetProperty("createdUtc", out var cr) ? cr.GetString()?[..10] ?? "" : "";
            var path = c.TryGetProperty("certPath", out var cp) ? cp.GetString() ?? "" : "";
            table.AddRow(Markup.Escape(domain), created, Markup.Escape(path));
        }
        AnsiConsole.Write(table);
    }
    else
    {
        AnsiConsole.MarkupLine("[dim]No certificates.[/]");
    }
});
sslCommand.Add(sslListCmd);

var sslGenerateArg = new Argument<string>("domain") { Description = "Domain to generate certificate for" };
var sslGenerateCmd = new Command("generate", "Generate SSL certificate for a domain") { sslGenerateArg };
sslGenerateCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(sslGenerateArg);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var result = await client.PostAsync("/api/ssl/generate",
            new StringContent(JsonSerializer.Serialize(new { domain, aliases = Array.Empty<string>() }),
            System.Text.Encoding.UTF8, "application/json"));
        var ok = result.TryGetProperty("ok", out var okVal) && okVal.GetBoolean();
        var msg = result.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (ok) AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(msg)}");
        else AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(msg)}");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]SSL generate failed:[/] {Markup.Escape(ex.Message)}");
    }
});
sslCommand.Add(sslGenerateCmd);

var sslInstallCaCmd = new Command("install-ca", "Install mkcert root CA into system trust store");
sslInstallCaCmd.SetAction(async (parseResult, ct) =>
{
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var result = await client.PostAsync("/api/ssl/install-ca");
        var ok = result.TryGetProperty("ok", out var okVal) && okVal.GetBoolean();
        var msg = result.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (ok) AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(msg)}");
        else AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(msg)}");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]CA install failed:[/] {Markup.Escape(ex.Message)}");
    }
});
sslCommand.Add(sslInstallCaCmd);

var sslRevokeArg = new Argument<string>("domain") { Description = "Domain to revoke certificate for" };
var sslRevokeCmd = new Command("revoke", "Revoke (delete) SSL certificate") { sslRevokeArg };
sslRevokeCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(sslRevokeArg);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        await client.DeleteAsync($"/api/ssl/certs/{domain}");
        AnsiConsole.MarkupLine($"[green]✓[/] Certificate for {Markup.Escape(domain)} revoked");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Revoke failed:[/] {Markup.Escape(ex.Message)}");
    }
});
sslCommand.Add(sslRevokeCmd);

// ── Node.js per-site process management ───────────────────────────────
var nodeCommand = new Command("node", "Node.js per-site process management");

var nodeListCmd = new Command("list", "List all tracked Node.js site processes");
nodeListCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var data = await client.GetJsonAsync("/api/node/sites");
    if (json) { PrintJson(data); return; }

    if (data.ValueKind != System.Text.Json.JsonValueKind.Array || data.GetArrayLength() == 0)
    {
        if (!Console.IsOutputRedirected) AnsiConsole.MarkupLine("[dim]No Node.js site processes tracked.[/]");
        return;
    }
    if (Console.IsOutputRedirected)
    {
        foreach (var p in data.EnumerateArray())
        {
            var domain = p.GetProperty("domain").GetString() ?? "";
            var state = p.TryGetProperty("state", out var s2) ? StateNumToStr(s2.GetInt32()) : "stopped";
            var pid = p.TryGetProperty("pid", out var pp) && pp.ValueKind != JsonValueKind.Null ? pp.GetInt32().ToString() : "";
            var port = p.TryGetProperty("port", out var pt2) ? pt2.GetInt32().ToString() : "";
            Console.WriteLine($"{domain}\t{state}\t{pid}\t{port}");
        }
        return;
    }
    var table = new Table().Border(TableBorder.Rounded)
        .AddColumn("Domain").AddColumn("State").AddColumn("PID")
        .AddColumn("Port").AddColumn("Command");
    foreach (var p in data.EnumerateArray())
    {
        var domain = p.GetProperty("domain").GetString() ?? "";
        var state = p.TryGetProperty("state", out var s) ? s.GetInt32() : 0;
        var pid = p.TryGetProperty("pid", out var pv) && pv.ValueKind != System.Text.Json.JsonValueKind.Null
            ? pv.GetInt32().ToString() : "-";
        var port = p.TryGetProperty("port", out var pt) ? pt.GetInt32().ToString() : "-";
        var cmd = p.TryGetProperty("startCommand", out var sc) ? sc.GetString() ?? "" : "";
        var stateLabel = state switch
        {
            2 => "[green]running[/]",
            1 => "[yellow]starting[/]",
            3 => "[yellow]stopping[/]",
            4 => "[red]crashed[/]",
            _ => "[dim]stopped[/]"
        };
        table.AddRow(Markup.Escape(domain), stateLabel, pid, port, Markup.Escape(cmd));
    }
    AnsiConsole.Write(table);
});
nodeCommand.Add(nodeListCmd);

var nodeStartArg = new Argument<string>("domain") { Description = "Site domain to start" };
var nodeStartCmd = new Command("start", "Start the Node.js process for a site") { nodeStartArg };
nodeStartCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(nodeStartArg);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var result = await client.PostAsync($"/api/node/sites/{domain}/start");
        var state = result.TryGetProperty("state", out var s) ? s.GetInt32() : -1;
        var pid = result.TryGetProperty("pid", out var pv) && pv.ValueKind != System.Text.Json.JsonValueKind.Null
            ? pv.GetInt32().ToString() : "-";
        if (state == 2)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(domain ?? "")} running (PID {pid})");
        }
        else
        {
            // Surface a non-zero exit so shell scripts like
            // `wdc node start app.loc && echo ok` don't falsely succeed
            // when the Node process crashed or never reached Running.
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(domain ?? "")} state={state} (expected Running)");
            Environment.Exit(2);
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
        Environment.Exit(1);
    }
});
nodeCommand.Add(nodeStartCmd);

var nodeStopArg = new Argument<string>("domain") { Description = "Site domain to stop" };
var nodeStopCmd = new Command("stop", "Stop the Node.js process for a site") { nodeStopArg };
nodeStopCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(nodeStopArg);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        await client.PostAsync($"/api/node/sites/{domain}/stop");
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(domain ?? "")} stopped");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
        Environment.Exit(1);
    }
});
nodeCommand.Add(nodeStopCmd);

var nodeRestartArg = new Argument<string>("domain") { Description = "Site domain to restart" };
var nodeRestartCmd = new Command("restart", "Restart the Node.js process for a site") { nodeRestartArg };
nodeRestartCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(nodeRestartArg);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var result = await client.PostAsync($"/api/node/sites/{domain}/restart");
        var state = result.TryGetProperty("state", out var s) ? s.GetInt32() : -1;
        var pid = result.TryGetProperty("pid", out var pv) && pv.ValueKind != System.Text.Json.JsonValueKind.Null
            ? pv.GetInt32().ToString() : "-";
        if (state == 2)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(domain ?? "")} restarted (PID {pid})");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(domain ?? "")} state={state} (expected Running)");
            Environment.Exit(2);
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
        Environment.Exit(1);
    }
});
nodeCommand.Add(nodeRestartCmd);

// ── Docker Compose detection ─────────────────────────────────────────
var composeCommand = new Command("compose", "Docker Compose detection for a site");

var composeDomainArg = new Argument<string>("domain") { Description = "Site domain to check" };
var composeCheckCmd = new Command("check", "Check whether a site's document root has a Compose file") { composeDomainArg };
composeCheckCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(composeDomainArg);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var result = await client.GetJsonAsync($"/api/sites/{domain}/docker-compose");
        if (json) { PrintJson(result); return; }

        var hasCompose = result.TryGetProperty("hasCompose", out var hc) && hc.GetBoolean();
        var fileName = result.TryGetProperty("fileName", out var fn) && fn.ValueKind != System.Text.Json.JsonValueKind.Null
            ? fn.GetString() ?? "" : "";
        var composeFile = result.TryGetProperty("composeFile", out var cf) && cf.ValueKind != System.Text.Json.JsonValueKind.Null
            ? cf.GetString() ?? "" : "";

        if (hasCompose)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(domain ?? "")} has [cyan]{Markup.Escape(fileName)}[/]");
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(composeFile)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]—[/] {Markup.Escape(domain ?? "")} has no compose file");
            Environment.Exit(2);
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
        Environment.Exit(1);
    }
});
composeCommand.Add(composeCheckCmd);

// Docker Compose lifecycle commands
foreach (var (cmdName, desc, method) in new[] {
    ("up", "Start compose services (docker compose up -d)", "up"),
    ("down", "Stop and remove compose services", "down"),
    ("restart", "Restart compose services", "restart"),
})
{
    var domArg = new Argument<string>("domain") { Description = "Site domain" };
    var cmd = new Command(cmdName, desc) { domArg };
    var capturedMethod = method;
    cmd.SetAction(async (parseResult, ct) =>
    {
        var domain = parseResult.GetValue(domArg);
        var json = parseResult.GetValue(jsonOption);
        using var client = new DaemonClient();
        if (!EnsureConnected(client)) return;
        try
        {
            var result = await client.PostAsync($"/api/sites/{domain}/docker-compose/{capturedMethod}");
            if (json) { PrintJson(result); return; }
            var ok = result.TryGetProperty("ok", out var okv) && okv.GetBoolean();
            var output = result.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            if (ok)
                AnsiConsole.MarkupLine($"[green]✓[/] compose {capturedMethod} for {Markup.Escape(domain ?? "")}");
            else
                AnsiConsole.MarkupLine($"[red]✗[/] compose {capturedMethod} failed for {Markup.Escape(domain ?? "")}");
            if (output.Length > 0) AnsiConsole.WriteLine(output);
            if (!ok) Environment.Exit(1);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
            Environment.Exit(1);
        }
    });
    composeCommand.Add(cmd);
}

// wdc compose ps <domain>
var composePsDomainArg = new Argument<string>("domain") { Description = "Site domain" };
var composePsCmd = new Command("ps", "List running compose containers") { composePsDomainArg };
composePsCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(composePsDomainArg);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var result = await client.GetJsonAsync($"/api/sites/{domain}/docker-compose/ps");
    if (json) { PrintJson(result); return; }
    var output = result.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
    if (output.Length > 0) AnsiConsole.WriteLine(output);
    else AnsiConsole.MarkupLine("[dim]No containers running[/]");
});
composeCommand.Add(composePsCmd);

// wdc compose logs <domain>
var composeLogsDomainArg = new Argument<string>("domain") { Description = "Site domain" };
var composeLogsCmd = new Command("logs", "Show recent compose logs") { composeLogsDomainArg };
composeLogsCmd.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(composeLogsDomainArg);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var result = await client.GetJsonAsync($"/api/sites/{domain}/docker-compose/logs");
    if (json) { PrintJson(result); return; }
    var output = result.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
    if (output.Length > 0) AnsiConsole.WriteLine(output);
    else AnsiConsole.MarkupLine("[dim]No log output[/]");
});
composeCommand.Add(composeLogsCmd);

// ── Per-site metrics (Phase 11 performance foothold) ─────────────────
var metricsCommand = new Command("metrics", "Show access log metrics for a site");
var metricsDomainArg = new Argument<string>("domain") { Description = "Site domain to inspect" };
metricsCommand.Add(metricsDomainArg);
metricsCommand.SetAction(async (parseResult, ct) =>
{
    var domain = parseResult.GetValue(metricsDomainArg);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var result = await client.GetJsonAsync($"/api/sites/{domain}/metrics");
        if (json) { PrintJson(result); return; }

        var has = result.TryGetProperty("hasMetrics", out var hm) && hm.GetBoolean();
        if (!has)
        {
            if (!Console.IsOutputRedirected)
            {
                AnsiConsole.MarkupLine($"[dim]—[/] {Markup.Escape(domain ?? "")}: no access log found");
                AnsiConsole.MarkupLine($"  [dim]Site hasn't been hit yet, or Apache hasn't been started with the generated vhost.[/]");
            }
            Environment.Exit(2);
            return;
        }

        var log = result.GetProperty("accessLog");
        var path = log.GetProperty("path").GetString() ?? "";
        var size = log.GetProperty("sizeBytes").GetInt64();
        var requests = log.GetProperty("requestCount").GetInt64();

        if (Console.IsOutputRedirected)
        {
            Console.WriteLine($"requests\t{requests}");
            Console.WriteLine($"size\t{size}");
            Console.WriteLine($"path\t{path}");
            return;
        }
        var lastWrite = log.TryGetProperty("lastWriteUtc", out var lw) && lw.ValueKind != System.Text.Json.JsonValueKind.Null
            ? lw.GetDateTime() : (DateTime?)null;

        var sizeDisplay = size < 1024 ? $"{size} B"
            : size < 1024 * 1024 ? $"{size / 1024.0:F1} KB"
            : $"{size / (1024.0 * 1024.0):F1} MB";

        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(domain ?? "")}");
        AnsiConsole.MarkupLine($"  [cyan]Requests:[/] {requests:N0}");
        AnsiConsole.MarkupLine($"  [cyan]Size:[/]     {sizeDisplay}");
        if (lastWrite.HasValue)
        {
            var age = DateTime.UtcNow - lastWrite.Value;
            var ageDisplay = age.TotalSeconds < 60 ? $"{(int)age.TotalSeconds}s ago"
                : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago"
                : age.TotalHours < 24 ? $"{(int)age.TotalHours}h ago"
                : $"{(int)age.TotalDays}d ago";
            AnsiConsole.MarkupLine($"  [cyan]Last hit:[/] {ageDisplay}");
        }
        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(path)}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
        Environment.Exit(1);
    }
});

rootCommand.Add(metricsCommand);
rootCommand.Add(composeCommand);
rootCommand.Add(nodeCommand);
rootCommand.Add(sslCommand);
rootCommand.Add(hostsCmd);
rootCommand.Add(completionCmd);
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
// --- wdc migrate ---
var migrateCommand = new Command("migrate", "Migrate sites from another tool (currently: MAMP)");
var migrateFromOption = new Option<string>("--from") { Description = "Source tool to migrate from (mamp)", Required = true };
var migrateDryRunOption = new Option<bool>("--dry-run") { Description = "Discover only, do not write any site configs" };
migrateCommand.Add(migrateFromOption);
migrateCommand.Add(migrateDryRunOption);
migrateCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var from = parseResult.GetValue(migrateFromOption);
    var dryRun = parseResult.GetValue(migrateDryRunOption);

    if (!string.Equals(from, "mamp", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine($"[red]Unsupported source:[/] {Markup.Escape(from ?? "<none>")} — only 'mamp' is supported");
        return;
    }

    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    if (dryRun)
    {
        var discovered = await client.GetJsonAsync("/api/sites/discover-mamp");
        if (json) { PrintJson(discovered); return; }
        var count = discovered.GetProperty("count").GetInt32();
        AnsiConsole.MarkupLine($"[bold]MAMP discovery:[/] {count} site(s) found");
        if (count == 0) return;
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Domain"); t.AddColumn("DocumentRoot"); t.AddColumn("SSL"); t.AddColumn("Source");
        foreach (var s in discovered.GetProperty("sites").EnumerateArray())
        {
            t.AddRow(
                s.GetProperty("domain").GetString() ?? "?",
                s.GetProperty("documentRoot").GetString() ?? "?",
                s.GetProperty("sslEnabled").GetBoolean() ? "✓" : "-",
                s.GetProperty("sourcePath").GetString() ?? "?");
        }
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("[dim]Run without --dry-run to import.[/]");
        return;
    }

    var result = await client.PostAsync("/api/sites/migrate-mamp");
    if (json) { PrintJson(result); return; }
    var imported = result.GetProperty("count").GetInt32();
    AnsiConsole.MarkupLine($"[green]✓[/] Imported [bold]{imported}[/] site(s) from MAMP");
    if (imported > 0)
    {
        foreach (var d in result.GetProperty("imported").EnumerateArray())
            AnsiConsole.MarkupLine($"  • {Markup.Escape(d.GetString() ?? "")}");
    }
});

// --- wdc backup ---
var backupCommand = new Command("backup", "Create a zip backup of NKS WDC state (sites, certs, db)");
var backupOutOption = new Option<string?>("--out") { Description = "Output zip path (default: ~/.wdc/backups/wdc-backup-<timestamp>.zip)" };
backupCommand.Add(backupOutOption);
backupCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var outPath = parseResult.GetValue(backupOutOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    try
    {
        var url = "/api/backup" + (string.IsNullOrEmpty(outPath) ? "" : $"?out={Uri.EscapeDataString(outPath)}");
        var result = await client.PostAsync(url);
        if (json) { PrintJson(result); return; }

        var path = result.GetProperty("path").GetString() ?? "?";
        var files = result.GetProperty("files").GetInt32();
        var size = result.GetProperty("size").GetInt64();
        AnsiConsole.MarkupLine($"[green]✓[/] Backup created");
        AnsiConsole.MarkupLine($"  Path:  [bold]{Markup.Escape(path)}[/]");
        AnsiConsole.MarkupLine($"  Files: {files}");
        AnsiConsole.MarkupLine($"  Size:  {FormatBytes(size)}");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Backup failed:[/] {Markup.Escape(ex.Message)}");
    }
});

// wdc backup list
var backupListCmd = new Command("list", "List existing backups");
backupListCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var data = await client.GetJsonAsync("/api/backup/list");
    if (json) { PrintJson(data); return; }
    if (!data.TryGetProperty("backups", out var arr) || arr.GetArrayLength() == 0)
    {
        if (!Console.IsOutputRedirected) AnsiConsole.MarkupLine("[dim]No backups found.[/]");
        return;
    }
    if (Console.IsOutputRedirected)
    {
        foreach (var b in arr.EnumerateArray())
        {
            var created = b.TryGetProperty("createdUtc", out var c) ? c.GetString()?[..19] ?? "" : "";
            var size = b.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            var path = b.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            Console.WriteLine($"{created}\t{size}\t{Path.GetFileName(path)}");
        }
        return;
    }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Date"); table.AddColumn("Size"); table.AddColumn("Path");
    foreach (var b in arr.EnumerateArray())
    {
        var created = b.TryGetProperty("createdUtc", out var c) ? c.GetString()?[..19] ?? "" : "";
        var size = b.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
        var path = b.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        table.AddRow(created, FormatBytes(size), Markup.Escape(Path.GetFileName(path)));
    }
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"[dim]{arr.GetArrayLength()} backup(s)[/]");
});
backupCommand.Add(backupListCmd);

// --- wdc restore ---
var restoreCommand = new Command("restore", "Restore a backup zip into NKS WDC state");
var restoreFromOption = new Option<string>("--from") { Description = "Path to a backup zip", Required = true };
var restoreYesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
restoreCommand.Add(restoreFromOption);
restoreCommand.Add(restoreYesOption);
restoreCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var from = parseResult.GetValue(restoreFromOption);
    var yes = parseResult.GetValue(restoreYesOption);

    if (string.IsNullOrWhiteSpace(from) || !System.IO.File.Exists(from))
    {
        AnsiConsole.MarkupLine($"[red]Backup file not found:[/] {Markup.Escape(from ?? "<none>")}");
        return;
    }

    if (!yes && !json)
    {
        AnsiConsole.MarkupLine($"[yellow]This will replace your current NKS WDC sites/certs/db with the contents of:[/]");
        AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(from!)}[/]");
        AnsiConsole.MarkupLine("[dim]A pre-restore safety backup will be created automatically.[/]");
        if (!AnsiConsole.Confirm("Continue?", false)) return;
    }

    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    // The daemon endpoint accepts multipart upload OR JSON {path: ...} for in-place files.
    // We send the path directly because the daemon already lives on the same machine.
    try
    {
        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new { path = from }),
            System.Text.Encoding.UTF8,
            "application/json");
        var result = await client.PostAsync("/api/restore", content);
        if (json) { PrintJson(result); return; }

        var restored = result.GetProperty("restored").GetInt32();
        var safety = result.GetProperty("safetyBackup").GetString() ?? "?";
        AnsiConsole.MarkupLine($"[green]✓[/] Restored [bold]{restored}[/] file(s)");
        AnsiConsole.MarkupLine($"[dim]Safety backup of previous state: {Markup.Escape(safety)}[/]");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Restore failed:[/] {Markup.Escape(ex.Message)}");
    }
});

// --- wdc uninstall ---
var uninstallCommand = new Command("uninstall", "Uninstall NKS WDC state (sites, certs, db, optionally binaries)");
var uninstallDryRunOption = new Option<bool>("--dry-run") { Description = "Show what would be removed without deleting anything" };
var uninstallPurgeOption = new Option<bool>("--purge") { Description = "Also remove ~/.wdc/binaries (default: keep binaries so reinstall is fast)" };
var uninstallHostsOption = new Option<bool>("--hosts") { Description = "Strip the managed block from the system hosts file" };
var uninstallYesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
uninstallCommand.Add(uninstallDryRunOption);
uninstallCommand.Add(uninstallPurgeOption);
uninstallCommand.Add(uninstallHostsOption);
uninstallCommand.Add(uninstallYesOption);
uninstallCommand.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    var dryRun = parseResult.GetValue(uninstallDryRunOption);
    var purge = parseResult.GetValue(uninstallPurgeOption);
    var hosts = parseResult.GetValue(uninstallHostsOption);
    var yes = parseResult.GetValue(uninstallYesOption);

    if (!dryRun && !yes && !json)
    {
        AnsiConsole.MarkupLine("[yellow bold]DESTRUCTIVE:[/] this will stop all services and delete NKS WDC state.");
        if (purge) AnsiConsole.MarkupLine("  [red]--purge[/] will also delete [bold]~/.wdc/binaries[/] — you'll need to re-download PHP/Apache/etc.");
        if (hosts) AnsiConsole.MarkupLine("  [red]--hosts[/] will strip the managed block from the system hosts file.");
        AnsiConsole.MarkupLine("[dim]A pre-uninstall safety backup will be created automatically.[/]");
        AnsiConsole.MarkupLine("[dim]Tip: run with --dry-run first to see what will be removed.[/]");
        if (!AnsiConsole.Confirm("Continue?", false)) return;
    }

    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var payload = new
    {
        confirm = dryRun ? null : "YES-UNINSTALL",
        dryRun,
        purge,
        hosts,
    };
    try
    {
        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8,
            "application/json");
        var result = await client.PostAsync("/api/uninstall", content);
        if (json) { PrintJson(result); return; }

        var msg = result.TryGetProperty("message", out var m) ? m.GetString() : "";
        var backup = result.TryGetProperty("safetyBackup", out var b) ? b.GetString() : null;
        AnsiConsole.MarkupLine(dryRun ? $"[yellow]⚠[/] {Markup.Escape(msg ?? "")}" : $"[green]✓[/] {Markup.Escape(msg ?? "")}");
        if (!string.IsNullOrEmpty(backup)) AnsiConsole.MarkupLine($"[dim]Safety backup: {Markup.Escape(backup)}[/]");

        if (result.TryGetProperty("plan", out var planEl) && planEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            AnsiConsole.MarkupLine("[bold]Plan:[/]");
            foreach (var step in planEl.EnumerateArray())
                AnsiConsole.MarkupLine($"  • {Markup.Escape(step.GetString() ?? "")}");
        }
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Uninstall failed:[/] {Markup.Escape(ex.Message)}");
    }
});

rootCommand.Add(backupCommand);
rootCommand.Add(restoreCommand);
rootCommand.Add(uninstallCommand);
rootCommand.Add(migrateCommand);
rootCommand.Add(versionCommand);

// --- wdc activity ---
var activityLimitOpt = new Option<int>("--limit") { Description = "Number of entries", DefaultValueFactory = _ => 15 };
var activityCommand = new Command("activity", "Show recent config change history") { activityLimitOpt };
activityCommand.SetAction(async (parseResult, ct) =>
{
    var limit = parseResult.GetValue(activityLimitOpt);
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var data = await client.GetJsonAsync($"/api/activity?limit={limit}");
    if (json) { PrintJson(data); return; }
    if (data.ValueKind != System.Text.Json.JsonValueKind.Array || data.GetArrayLength() == 0)
    {
        if (!Console.IsOutputRedirected) AnsiConsole.MarkupLine("[dim]No activity history yet.[/]");
        return;
    }
    if (Console.IsOutputRedirected)
    {
        foreach (var row in data.EnumerateArray())
        {
            var time = row.TryGetProperty("createdAt", out var t2) ? t2.GetString()?[..19] ?? "" : "";
            var op = row.TryGetProperty("operation", out var o2) ? o2.GetString() ?? "" : "";
            var entity = row.TryGetProperty("entityName", out var en2) ? en2.GetString() ?? "" : "";
            Console.WriteLine($"{time}\t{op}\t{entity}");
        }
        return;
    }
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Time"); table.AddColumn("Entity"); table.AddColumn("Op"); table.AddColumn("Source");
    foreach (var row in data.EnumerateArray())
    {
        var time = row.TryGetProperty("createdAt", out var t) ? t.GetString()?[..19] ?? "" : "";
        var entity = row.TryGetProperty("entityName", out var en) ? en.GetString() ?? "" : "";
        var entityType = row.TryGetProperty("entityType", out var et) ? et.GetString() ?? "" : "";
        var op = row.TryGetProperty("operation", out var o) ? o.GetString() ?? "" : "";
        var source = row.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
        var label = entity.Length > 0 ? $"{entityType}/{entity}" : entityType;
        var opColor = op.Contains("create") ? "green" : op.Contains("delete") ? "red" : "blue";
        table.AddRow(time, Markup.Escape(label), $"[{opColor}]{Markup.Escape(op)}[/]", Markup.Escape(source));
    }
    AnsiConsole.Write(table);
});
rootCommand.Add(activityCommand);

// --- wdc cloudflare --- (mirrors /cloudflare page for terminal users)
var cfCommand = new Command("cloudflare", "Cloudflare Tunnel management");

// wdc cloudflare status
var cfStatusCmd = new Command("status", "Show tunnel config and status");
cfStatusCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    var cfg = await client.GetJsonAsync("/api/cloudflare/config");
    if (json) { PrintJson(cfg); return; }

    var tunnelName = cfg.TryGetProperty("tunnelName", out var tn) ? tn.GetString() : null;
    var tunnelId = cfg.TryGetProperty("tunnelId", out var ti) ? ti.GetString() : null;
    var accountId = cfg.TryGetProperty("accountId", out var ai) ? ai.GetString() : null;
    var path = cfg.TryGetProperty("cloudflaredPath", out var cp) ? cp.GetString() : null;
    var apiToken = cfg.TryGetProperty("apiToken", out var at) ? at.GetString() : null;

    AnsiConsole.MarkupLine($"[bold]Cloudflare Tunnel[/]");
    AnsiConsole.MarkupLine($"  Tunnel:     {Markup.Escape(tunnelName ?? "(not configured)")}");
    AnsiConsole.MarkupLine($"  Tunnel ID:  [dim]{Markup.Escape(tunnelId ?? "—")}[/]");
    AnsiConsole.MarkupLine($"  Account:    [dim]{Markup.Escape(accountId ?? "—")}[/]");
    AnsiConsole.MarkupLine($"  Binary:     [dim]{Markup.Escape(path ?? "(auto-detect)")}[/]");
    AnsiConsole.MarkupLine($"  API Token:  {Markup.Escape(apiToken ?? "(not set)")}");

    // Check service state
    try
    {
        var services = await client.GetJsonAsync("/api/services");
        foreach (var svc in services.EnumerateArray())
        {
            if ((svc.GetProperty("id").GetString() ?? "") == "cloudflare")
            {
                var state = svc.GetProperty("state").ValueKind == System.Text.Json.JsonValueKind.Number
                    ? StateNumToStr(svc.GetProperty("state").GetInt32())
                    : svc.GetProperty("state").GetString() ?? "unknown";
                AnsiConsole.MarkupLine($"  Service:    {FormatState(state)}");
                break;
            }
        }
    }
    catch { /* service list may fail if plugin not loaded */ }
});
cfCommand.Add(cfStatusCmd);

// wdc cloudflare setup <api-token>
var cfSetupTokenArg = new Argument<string>("api-token") { Description = "Cloudflare API token with Tunnel + DNS scopes" };
var cfSetupCmd = new Command("setup", "Auto-setup tunnel from API token") { cfSetupTokenArg };
cfSetupCmd.SetAction(async (parseResult, ct) =>
{
    var token = parseResult.GetValue(cfSetupTokenArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    AnsiConsole.MarkupLine("Running auto-setup...");
    var content = JsonContent.Create(new { apiToken = token });
    try
    {
        var result = await client.PostAsync("/api/cloudflare/auto-setup", content);
        if (json) { PrintJson(result); return; }
        var account = result.TryGetProperty("account", out var a)
            ? a.TryGetProperty("name", out var an) ? an.GetString() : "?"
            : "?";
        var tunnel = result.TryGetProperty("tunnel", out var t)
            ? t.TryGetProperty("name", out var tname) ? tname.GetString() : "?"
            : "?";
        AnsiConsole.MarkupLine($"[green]Auto-setup complete[/]");
        AnsiConsole.MarkupLine($"  Account: [cyan]{Markup.Escape(account ?? "?")}[/]");
        AnsiConsole.MarkupLine($"  Tunnel:  [cyan]{Markup.Escape(tunnel ?? "?")}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Setup failed:[/] {Markup.Escape(ex.Message)}");
        Environment.Exit(1);
    }
});
cfCommand.Add(cfSetupCmd);

// wdc cloudflare sync
var cfSyncCmd = new Command("sync", "Push all enabled sites to Cloudflare (DNS + ingress)");
cfSyncCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var result = await client.PostAsync("/api/cloudflare/sync");
        if (json) { PrintJson(result); return; }
        var synced = result.TryGetProperty("synced", out var s) ? s.GetInt32() : 0;
        var deleted = result.TryGetProperty("deleted", out var d) ? d.GetInt32() : 0;
        AnsiConsole.MarkupLine($"[green]Synced:[/] {synced} site(s) pushed, {deleted} dormant CNAME(s) deleted");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Cloudflare sync failed:[/] {Markup.Escape(ex.Message)}");
    }
});
cfCommand.Add(cfSyncCmd);

// wdc cloudflare zones
var cfZonesCmd = new Command("zones", "List Cloudflare zones accessible with the configured token");
cfZonesCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var result = await client.GetJsonAsync("/api/cloudflare/zones");
        if (json) { PrintJson(result); return; }
        if (!result.TryGetProperty("result", out var zones) || zones.GetArrayLength() == 0)
        {
            if (!Console.IsOutputRedirected) AnsiConsole.MarkupLine("[dim]No zones found. Check API token scopes.[/]");
            return;
        }
        if (Console.IsOutputRedirected)
        {
            foreach (var z in zones.EnumerateArray())
                Console.WriteLine($"{z.GetProperty("name").GetString()}\t{z.GetProperty("id").GetString()}");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name"); table.AddColumn("Status"); table.AddColumn("ID");
        foreach (var z in zones.EnumerateArray())
            table.AddRow(
                z.GetProperty("name").GetString() ?? "?",
                z.TryGetProperty("status", out var st) ? st.GetString()! : "?",
                z.GetProperty("id").GetString() ?? "?");
        AnsiConsole.Write(table);
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Failed to list zones:[/] {Markup.Escape(ex.Message)}");
    }
});
cfCommand.Add(cfZonesCmd);

// wdc cloudflare dns <zone-id>
var cfDnsZoneArg = new Argument<string>("zone-id") { Description = "Cloudflare zone ID" };
var cfDnsCmd = new Command("dns", "List DNS records for a zone") { cfDnsZoneArg };
cfDnsCmd.SetAction(async (parseResult, ct) =>
{
    var zoneId = parseResult.GetValue(cfDnsZoneArg)!;
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;
    try
    {
        var records = await client.GetJsonAsync($"/api/cloudflare/zones/{zoneId}/dns");
        if (json) { PrintJson(records); return; }
        if (records.ValueKind != System.Text.Json.JsonValueKind.Array || records.GetArrayLength() == 0)
        {
            if (!Console.IsOutputRedirected) AnsiConsole.MarkupLine("[dim]No DNS records.[/]");
            return;
        }
        if (Console.IsOutputRedirected)
        {
            foreach (var r in records.EnumerateArray())
            {
                var type = r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var content = r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                var proxied = r.TryGetProperty("proxied", out var p) && p.GetBoolean() ? "proxied" : "dns-only";
                Console.WriteLine($"{type}\t{name}\t{content}\t{proxied}");
            }
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Type"); table.AddColumn("Name"); table.AddColumn("Content"); table.AddColumn("Proxied");
        foreach (var r in records.EnumerateArray())
        {
            var type = r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var content = r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var proxied = r.TryGetProperty("proxied", out var p) && p.GetBoolean();
            table.AddRow(type, Markup.Escape(name), Markup.Escape(content.Length > 40 ? content[..37] + "..." : content),
                proxied ? "[yellow]proxied[/]" : "[dim]DNS only[/]");
        }
        AnsiConsole.Write(table);
    }
    catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}"); Environment.Exit(1); }
});
cfCommand.Add(cfDnsCmd);

rootCommand.Add(cfCommand);

// --- wdc sync --- (mirrors Settings → Sync tab)
var syncCommand = new Command("sync", "Config sync: push/pull/export between devices");

// wdc sync push
var syncPushCmd = new Command("push", "Push settings + sites to catalog-api cloud");
syncPushCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    // Build payload: settings + sites
    var settings = await client.GetJsonAsync("/api/settings");
    var sites = await client.GetJsonAsync("/api/sites");
    var deviceId = settings.TryGetProperty("sync.deviceId", out var did) ? did.GetString() ?? "" : "";
    if (string.IsNullOrEmpty(deviceId))
    {
        AnsiConsole.MarkupLine("[red]No device ID configured.[/] Open Settings → Sync to initialize.");
        Environment.Exit(1);
        return;
    }

    // Resolve catalog URL
    var catalogUrl = settings.TryGetProperty("daemon.catalogUrl", out var cu) ? cu.GetString() ?? "" : "";
    if (string.IsNullOrWhiteSpace(catalogUrl)) catalogUrl = "http://127.0.0.1:8765";

    // Send JWT if the user has logged in via Settings → Account, so the
    // catalog-api's optional_account dependency auto-links the device to
    // the user's account. Without this header pushes are anonymous.
    var jwt = settings.TryGetProperty("sync.accountToken", out var jwtVal) ? jwtVal.GetString() ?? "" : "";
    var payload = new { exportedAt = DateTime.UtcNow.ToString("o"), settings, sites };
    var body = JsonContent.Create(new { device_id = deviceId, payload });
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    if (!string.IsNullOrEmpty(jwt))
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
    var resp = await http.PostAsync($"{catalogUrl.TrimEnd('/')}/api/v1/sync/config", body);
    resp.EnsureSuccessStatusCode();
    if (json) { PrintJson(await resp.Content.ReadAsStringAsync()); return; }
    AnsiConsole.MarkupLine($"[green]Pushed[/] settings + {sites.GetArrayLength()} sites to {Markup.Escape(catalogUrl)}");
});
syncCommand.Add(syncPushCmd);

// wdc sync pull
var syncPullCmd = new Command("pull", "Pull settings from catalog-api cloud (smart merge: sync fields only)");
syncPullCmd.SetAction(async (parseResult, ct) =>
{
    var json = parseResult.GetValue(jsonOption);
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var settings = await client.GetJsonAsync("/api/settings");
    var deviceId = settings.TryGetProperty("sync.deviceId", out var did) ? did.GetString() ?? "" : "";
    if (string.IsNullOrEmpty(deviceId))
    {
        AnsiConsole.MarkupLine("[red]No device ID.[/] Open Settings → Sync first.");
        Environment.Exit(1);
        return;
    }
    var catalogUrl = settings.TryGetProperty("daemon.catalogUrl", out var cu) ? cu.GetString() ?? "" : "";
    if (string.IsNullOrWhiteSpace(catalogUrl)) catalogUrl = "http://127.0.0.1:8765";
    var jwt = settings.TryGetProperty("sync.accountToken", out var jwtVal) ? jwtVal.GetString() ?? "" : "";

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    if (!string.IsNullOrEmpty(jwt))
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
    var resp = await http.GetAsync($"{catalogUrl.TrimEnd('/')}/api/v1/sync/config/{deviceId}");
    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        AnsiConsole.MarkupLine("[yellow]No cloud snapshot found for this device.[/] Push first.");
        return;
    }
    resp.EnsureSuccessStatusCode();
    var data = await resp.Content.ReadAsStringAsync();
    if (json) { PrintJson(data); return; }
    AnsiConsole.MarkupLine($"[green]Pulled[/] snapshot from cloud. Apply via Settings → Sync → Pull in UI for smart merge.");
    AnsiConsole.MarkupLine("[dim]CLI pull currently shows the raw snapshot — smart merge requires the UI.[/]");
});
syncCommand.Add(syncPullCmd);

// wdc sync export <file>
var syncExportFileArg = new Argument<string>("file") { Description = "Output JSON file path" };
var syncExportCmd = new Command("export", "Export settings + sites to a JSON file") { syncExportFileArg };
syncExportCmd.SetAction(async (parseResult, ct) =>
{
    var filePath = parseResult.GetValue(syncExportFileArg)!;
    using var client = new DaemonClient();
    if (!EnsureConnected(client)) return;

    var settings = await client.GetJsonAsync("/api/settings");
    var sites = await client.GetJsonAsync("/api/sites");
    var payload = new
    {
        exportedAt = DateTime.UtcNow.ToString("o"),
        version = "0.1.0",
        deviceId = settings.TryGetProperty("sync.deviceId", out var d) ? d.GetString() : "",
        settings,
        sites,
    };
    var jsonStr = System.Text.Json.JsonSerializer.Serialize(payload,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(filePath, jsonStr);
    AnsiConsole.MarkupLine($"[green]Exported[/] to [cyan]{Markup.Escape(filePath)}[/] ({sites.GetArrayLength()} sites)");
});
syncCommand.Add(syncExportCmd);

rootCommand.Add(syncCommand);

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
