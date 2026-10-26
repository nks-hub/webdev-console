using System.Text.Json;
using System.Text.RegularExpressions;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// What a caller holding the MCP token (an AI agent behind the MCP server)
/// may do. The MCP token is limited to an allow-list: the routes the shipped
/// MCP tools in services/mcp-server/src/tools/*.ts call, and nothing else.
/// Every other route answers 403 for the MCP token, so new daemon routes are
/// closed to the agent until somebody adds them here on purpose.
///
/// The decision is made on the endpoint the router matched (its route
/// pattern), not on the raw request path. Path variants the router treats as
/// the same route (a trailing slash, different letter case) therefore cannot
/// slip past it, and a path the router does not match at all is refused.
///
/// Destructive operations on allowed routes still go through the intent gate,
/// which requires a GUI-confirmed intent for MCP callers. A few allowed routes
/// also have their request body checked here (protected settings, site fields
/// that would run commands or widen exposure).
/// </summary>
public static class McpTokenPolicy
{
    // Mirrors PROTECTED_SETTING_PREFIXES in services/mcp-server/src/tools/settings.ts.
    public static readonly string[] ProtectedSettingPrefixes =
        ["mcp.", "deploy.", "daemon.", "plugins.", "sync.", "paths.", "telemetry."];
    public static readonly string[] ProtectedSettingKeys = ["backup.dir"];

    /// <summary>
    /// Site fields the MCP token may send to <c>POST /api/sites</c>: exactly the
    /// input schema of <c>wdc_create_site</c> (services/mcp-server/src/tools/sites.ts).
    /// Anything else (nodeStartCommand, environment, cloudflare, bind
    /// addresses, ...) either runs a command or widens who can reach the site.
    /// </summary>
    public static readonly string[] AllowedSiteFields =
        ["domain", "documentRoot", "phpVersion", "sslEnabled", "aliases", "framework"];

    /// <summary>
    /// Routes the MCP token may call, as (METHOD, route pattern). Keep in sync
    /// with services/mcp-server/daemon-routes.json, which a Vitest test checks
    /// against the daemonClient calls in services/mcp-server/src/tools/*.ts and
    /// McpTokenPolicyTests checks against this list. Parameter names do not
    /// matter: patterns are compared with every <c>{...}</c> segment replaced.
    /// </summary>
    public static readonly IReadOnlyList<(string Method, string Pattern)> AllowedRoutes =
    [
        // system.ts
        ("GET", "/api/status"),
        ("GET", "/api/system"),
        ("GET", "/api/activity"),
        // sites.ts
        ("GET", "/api/sites"),
        ("GET", "/api/sites/{domain}"),
        ("GET", "/api/sites/{domain}/access-log"),
        ("GET", "/api/sites/{domain}/metrics"),
        ("POST", "/api/sites"),
        ("DELETE", "/api/sites/{domain}"),
        // services.ts
        ("GET", "/api/services"),
        ("POST", "/api/services/{id}/start"),
        ("POST", "/api/services/{id}/stop"),
        // settings.ts
        ("GET", "/api/settings"),
        ("PUT", "/api/settings"),
        // backup.ts
        ("GET", "/api/backup/list"),
        ("POST", "/api/backup"),
        ("POST", "/api/restore"),
        // binaries.ts
        ("GET", "/api/binaries/catalog"),
        ("GET", "/api/binaries/installed"),
        ("POST", "/api/binaries/catalog/refresh"),
        ("POST", "/api/binaries/install"),
        ("DELETE", "/api/binaries/{app}/{version}"),
        // cloudflare.ts
        ("GET", "/api/cloudflare/config"),
        ("PUT", "/api/cloudflare/config"),
        ("GET", "/api/cloudflare/verify"),
        ("GET", "/api/cloudflare/zones"),
        ("GET", "/api/cloudflare/zones/{zoneId}/dns"),
        ("POST", "/api/cloudflare/zones/{zoneId}/dns"),
        ("DELETE", "/api/cloudflare/zones/{zoneId}/dns/{recordId}"),
        ("GET", "/api/cloudflare/tunnels"),
        ("GET", "/api/cloudflare/suggest-subdomain"),
        // confirmation.ts, deploy.ts (intent requests; approval stays in the app)
        ("POST", "/api/mcp/intents"),
        ("POST", "/api/mcp/intents/confirm-request"),
        // databases.ts
        ("GET", "/api/databases"),
        ("POST", "/api/databases"),
        ("DELETE", "/api/databases/{name}"),
        ("GET", "/api/databases/{name}/tables"),
        ("POST", "/api/databases/{name}/query"),
        // mysqlUsers.ts
        ("GET", "/api/plugins/mysql/users"),
        ("POST", "/api/plugins/mysql/users"),
        ("POST", "/api/plugins/mysql/users/password"),
        ("POST", "/api/plugins/mysql/users/grants"),
        ("POST", "/api/plugins/mysql/users/drop"),
        ("POST", "/api/plugins/mysql/change-password"),
        ("POST", "/api/plugins/mysql/reset-password"),
        // php.ts
        ("GET", "/api/php/versions"),
        ("POST", "/api/php/{version}/extensions/{name}"),
        // plugins.ts
        ("GET", "/api/plugins"),
        ("GET", "/api/plugins/marketplace"),
        ("POST", "/api/plugins/{id}/enable"),
        ("POST", "/api/plugins/{id}/disable"),
        ("POST", "/api/plugins/install"),
        // ssl.ts
        ("GET", "/api/ssl/certs"),
        ("POST", "/api/ssl/install-ca"),
        ("POST", "/api/ssl/generate"),
        ("DELETE", "/api/ssl/certs/{domain}"),
        // deploy.ts
        ("GET", "/api/nks.wdc.deploy/sites/{domain}/history"),
        ("GET", "/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}"),
        ("DELETE", "/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}"),
        ("POST", "/api/nks.wdc.deploy/sites/{domain}/hosts/{host}/deploy"),
        ("POST", "/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}/rollback"),
        ("POST", "/api/nks.wdc.deploy/sites/{domain}/groups"),
        ("GET", "/api/nks.wdc.deploy/sites/{domain}/groups/{groupId}"),
        ("POST", "/api/nks.wdc.deploy/sites/{domain}/groups/{groupId}/rollback"),
        ("POST", "/api/nks.wdc.deploy/sites/{domain}/snapshots/{snapshotId}/restore"),
    ];

    private static readonly HashSet<(string Method, string Pattern)> AllowedSet =
        AllowedRoutes.Select(r => (r.Method.ToUpperInvariant(), CanonicalPattern(r.Pattern))).ToHashSet();

    public static bool IsProtectedSetting(string key)
    {
        var k = key.Trim().ToLowerInvariant();
        return ProtectedSettingKeys.Contains(k) || ProtectedSettingPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Canonical form of a route pattern or path for comparison: lower case,
    /// every route parameter replaced by <c>{*}</c>, repeated slashes
    /// collapsed, trailing slash dropped.
    /// </summary>
    public static string CanonicalPattern(string pattern)
    {
        var p = Regex.Replace(pattern.Trim(), @"\{[^{}]*\}", "{*}");
        p = Regex.Replace(p, "/{2,}", "/");
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');
        return p.ToLowerInvariant();
    }

    public static bool IsAllowedRoute(string method, string routePattern)
    {
        var m = method.ToUpperInvariant();
        if (m == "HEAD") m = "GET";
        return AllowedSet.Contains((m, CanonicalPattern(routePattern)));
    }

    /// <summary>Returns why the request is refused, or null when it may proceed.</summary>
    public static async Task<string?> DenyReasonAsync(HttpContext ctx)
    {
        // The router has already run (WebApplication adds UseRouting ahead of
        // the app's own middleware), so the matched endpoint is the route the
        // request will actually reach, whatever the spelling of its path.
        var pattern = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (pattern is null)
            return "this route is not available to the MCP token";

        if (!IsAllowedRoute(ctx.Request.Method, pattern))
            return $"{ctx.Request.Method.ToUpperInvariant()} {pattern} is not available to the MCP token";

        var key = (ctx.Request.Method.ToUpperInvariant(), CanonicalPattern(pattern));
        if (key == ("PUT", "/api/settings"))
        {
            var keys = await ReadObjectPropertiesAsync(ctx);
            var blocked = keys.Select(p => p.Name).Where(IsProtectedSetting).ToArray();
            if (blocked.Length > 0)
                return "operator-only settings: " + string.Join(", ", blocked);
        }
        else if (key == ("POST", "/api/sites"))
        {
            return SiteBodyDenyReason(await ReadObjectPropertiesAsync(ctx));
        }
        return null;
    }

    private static string? SiteBodyDenyReason(IReadOnlyList<(string Name, JsonValueKind Kind, string? Text)> props)
    {
        // Every occurrence counts: with duplicate keys the deserializer keeps the last one.
        var setsStartCommand = props.Any(p =>
            p.Name.Equals("nodeStartCommand", StringComparison.OrdinalIgnoreCase)
            && !(p.Kind == JsonValueKind.Null || p.Kind == JsonValueKind.String && string.IsNullOrWhiteSpace(p.Text)));
        if (setsStartCommand)
            return "nodeStartCommand runs a shell command and cannot be set with the MCP token; set it in the WDC app";

        var extra = props
            .Select(p => p.Name)
            .Where(n => !n.Equals("nodeStartCommand", StringComparison.OrdinalIgnoreCase)
                        && !AllowedSiteFields.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return extra.Length > 0
            ? "site fields not available to the MCP token: " + string.Join(", ", extra)
            : null;
    }

    private static async Task<IReadOnlyList<(string Name, JsonValueKind Kind, string? Text)>> ReadObjectPropertiesAsync(HttpContext ctx)
    {
        ctx.Request.EnableBuffering();
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject()
                    .Select(p => (p.Name, p.Value.ValueKind, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null))
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return []; // the endpoint itself rejects malformed JSON
        }
        finally
        {
            ctx.Request.Body.Position = 0;
        }
    }
}
