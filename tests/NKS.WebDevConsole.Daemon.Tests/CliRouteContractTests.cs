using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The <c>wdc</c> CLI ships with the installer and talks to the daemon over
/// the routes in its Program.cs. A route renamed or removed in the daemon
/// breaks it only when someone runs the command, so this pins the two sides
/// together.
/// </summary>
public class CliRouteContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebDevConsole.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WebDevConsole.sln not found above " + AppContext.BaseDirectory);
    }

    private static string Canonical(string method, string pattern) =>
        method.ToUpperInvariant() + " " + McpTokenPolicy.CanonicalPattern(pattern.Split('?')[0]);

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "daemon", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static readonly Regex DaemonMap = new(@"Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]+)""");
    private static readonly Regex DaemonMapMethods = new(@"MapMethods\(\s*""([^""]+)""\s*,\s*\[\s*""([A-Z]+)""");
    private static readonly Regex CliCall = new(@"\b(GetAsync|GetJsonAsync|PostAsync|PostRawAsync|PutAsync|DeleteAsync)(?:<[^>]*>)?\(\s*\$?""([^""]*)""");

    // Commands that register one handler per verb in a loop; the scanner sees only the template.
    private static readonly Dictionary<string, string[]> LoopVerbs = new()
    {
        ["{verb}"] = ["start", "stop", "restart"],
        ["{capturedMethod}"] = ["up", "down", "restart"],
    };

    // Paths where the CLI fills a daemon route parameter with a literal (an app name).
    private static readonly Dictionary<string, string> ParameterLiterals = new()
    {
        ["DELETE /api/binaries/php/{*}"] = "DELETE /api/binaries/{*}/{*}",
    };

    private static HashSet<string> DaemonRoutes()
    {
        var routes = new HashSet<string>();
        foreach (var text in SourceFiles("NKS.WebDevConsole.Daemon").Select(File.ReadAllText))
        {
            foreach (Match m in DaemonMap.Matches(text)) routes.Add(Canonical(m.Groups[1].Value, m.Groups[2].Value));
            foreach (Match m in DaemonMapMethods.Matches(text)) routes.Add(Canonical(m.Groups[2].Value, m.Groups[1].Value));
        }
        return routes;
    }

    private static List<(string Method, string Path)> CliRoutes()
    {
        var verbs = new Dictionary<string, string>
        {
            ["GetAsync"] = "GET", ["GetJsonAsync"] = "GET", ["PostAsync"] = "POST",
            ["PostRawAsync"] = "POST", ["PutAsync"] = "PUT", ["DeleteAsync"] = "DELETE",
        };
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "daemon", "NKS.WebDevConsole.Cli", "Program.cs"));
        var routes = new List<(string, string)>();
        foreach (Match m in CliCall.Matches(program))
        {
            var paths = new List<string> { m.Groups[2].Value };
            foreach (var (placeholder, values) in LoopVerbs)
                paths = paths.SelectMany(p => p.Contains(placeholder) ? values.Select(v => p.Replace(placeholder, v)) : [p]).ToList();
            routes.AddRange(paths.Select(p => (verbs[m.Groups[1].Value], p)));
        }
        return routes.Distinct().ToList();
    }

    [Fact]
    public void Every_route_the_cli_calls_is_declared_by_the_daemon()
    {
        var daemon = DaemonRoutes();
        var cli = CliRoutes();
        Assert.True(cli.Count > 50, $"scanner found only {cli.Count} CLI calls; the pattern no longer matches Program.cs");

        var missing = cli
            .Select(r => Canonical(r.Method, r.Path))
            .Select(key => ParameterLiterals.GetValueOrDefault(key, key))
            .Where(key => !daemon.Contains(key))
            .Distinct()
            .ToList();
        Assert.True(missing.Count == 0, "routes the CLI calls but the daemon does not declare: " + string.Join(", ", missing));
    }

    [Fact]
    public void Php_switch_persists_the_default_through_the_daemon()
    {
        Assert.Contains(("PUT", "/api/php/default"), CliRoutes());
        Assert.Contains("PUT /api/php/default", DaemonRoutes());
    }

    // `wdc logs --follow` opens a WebSocket on /api/logs/{id}/stream. The gate
    // takes ?token= only on the SSE/WebSocket routes it lists, so this one
    // needs the Authorization header the CLI sends.
    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("Query", false)]
    public async Task Log_stream_route_authenticates_by_header_only(string how, bool allowed)
    {
        var reached = false;
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseDaemonAuth("gui-token", "mcp-token");
        app.Run(_ => { reached = true; return Task.CompletedTask; });
        var pipeline = app.Build();

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/logs/apache/stream";
        if (how == "Authorization") ctx.Request.Headers.Authorization = "Bearer gui-token";
        else ctx.Request.QueryString = new QueryString("?token=gui-token");
        await pipeline(ctx);

        Assert.Equal(allowed, reached);
        if (!allowed) Assert.Equal(401, ctx.Response.StatusCode);
    }
}
