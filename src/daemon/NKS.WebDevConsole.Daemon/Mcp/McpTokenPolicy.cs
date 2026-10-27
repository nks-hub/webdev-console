using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>Why the MCP token may not make a request: the HTTP status and JSON body to answer with.</summary>
public sealed record McpDenial(int StatusCode, object Body);

/// <summary>A top-level property of a JSON request body.</summary>
internal readonly record struct BodyProperty(string Name, JsonValueKind Kind, string? Text, JsonElement Value);

/// <summary>
/// Decides what a caller holding the MCP token (an AI agent behind the MCP
/// server) may do; see <see cref="McpCapabilities"/> for the tiers.
///
/// The decision is made on the endpoint the router matched (its route
/// pattern, parameter names and letter case ignored), not on the raw path, so
/// spellings the router treats as the same route (trailing slash, case) are
/// judged the same way. A request the router matched to no route falls under
/// <see cref="McpCapabilities.UnlistedRoutes"/>.
///
/// Answers:
/// <list type="bullet">
///   <item>403 <c>{ error: "mcp_forbidden", detail }</c> for the trust root.</item>
///   <item>403 <c>{ error: "mcp_capability_disabled", capability, settingKey, detail, hint }</c>
///   when the capability covering the route or body field is off.</item>
///   <item>403/425 <c>{ error: "intent_rejected", reason, detail, capability, intentKind,
///   intentDomain, intentHost, hint }</c> when an enabled capability needs a
///   human-approved intent (X-Intent-Token) and none was approved.</item>
/// </list>
/// </summary>
public static class McpTokenPolicy
{
    // Mirrors the mcp.* guard in services/mcp-server/src/tools/settings.ts.
    public static bool IsMcpSetting(string key) =>
        key.Trim().ToLowerInvariant().StartsWith("mcp.", StringComparison.Ordinal);

    public static bool IsOperatorSetting(string key)
    {
        var k = key.Trim().ToLowerInvariant();
        return McpCapabilities.OperatorSettingKeys.Contains(k)
               || McpCapabilities.OperatorSettingPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>Routes the MCP token can never call, whatever the capabilities say.</summary>
    public static readonly IReadOnlyList<McpRoute> NeverAllowed =
    [
        new("POST", "/api/mcp/intents/{intentId}/confirm"),
        new("POST", "/api/mcp/intents/{intentId}/revoke"),
        new("POST", "/api/mcp/grants"),
        new("PATCH", "/api/mcp/grants/{id}"),
        new("DELETE", "/api/mcp/grants/{id}"),
        new("POST", "/api/mcp/grants/import"),
        new("POST", "/api/mcp/grants/sweep-now"),
        new("POST", "/api/mcp/grants/test-match"),
    ];

    private static readonly HashSet<(string, string)> AlwaysSet =
        McpCapabilities.AlwaysAllowed.Select(Key).ToHashSet();

    private static readonly HashSet<(string, string)> NeverSet = NeverAllowed.Select(Key).ToHashSet();

    private static readonly Dictionary<(string, string), (McpCapability Capability, McpRoute Route)> GroupRoutes =
        McpCapabilities.All
            .SelectMany(c => c.Routes.Select(r => (Key: Key(r), Value: (c, r))))
            .ToDictionary(x => x.Key, x => x.Value);

    private static (string, string) Key(McpRoute r) => (r.Method.ToUpperInvariant(), CanonicalPattern(r.Pattern));

    private static readonly (string, string) SettingsPut = ("PUT", "/api/settings");
    private static readonly (string, string) SitePost = ("POST", "/api/sites");
    private static readonly (string, string) SitePut = ("PUT", "/api/sites/{*}");
    private static readonly (string, string) CloudflareConfigPut = ("PUT", "/api/cloudflare/config");
    private static readonly (string, string) DatabaseExport = ("GET", "/api/databases/{*}/export");
    private static readonly (string, string) BackupPost = ("POST", "/api/backup");
    private static readonly (string, string) TableBrowse = ("GET", "/api/databases/v2/{*}/tables/{*}/data");
    private static readonly (string, string) ConfigValidate = ("POST", "/api/config/validate");

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

    private static string NormalizedMethod(string method)
    {
        var m = method.ToUpperInvariant();
        return m == "HEAD" ? "GET" : m;
    }

    /// <summary>
    /// Production entry point: capability switches come from SettingsStore
    /// (<c>mcp.capabilities.&lt;id&gt;</c>), intents are checked with the
    /// daemon's IDeployIntentValidator, the current site config for
    /// PUT /api/sites/{domain} comes from SiteManager.
    /// </summary>
    public static Task<McpDenial?> EvaluateAsync(HttpContext ctx)
    {
        var settings = ctx.RequestServices.GetService<SettingsStore>();
        Func<string, bool> isEnabled = settings is null
            ? id => McpCapabilities.Get(id).DefaultEnabled
            : id => McpCapabilities.IsEnabled(settings, id);
        var sites = ctx.RequestServices.GetService<SiteManager>();
        Func<string, JsonElement?>? currentSite = null;
        if (sites is not null)
        {
            // Serialise the way GET /api/sites/{domain} answers, so a body
            // that echoes that answer compares equal field by field.
            var json = ctx.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
                       ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
            currentSite = domain => sites.Get(domain) is { } site ? JsonSerializer.SerializeToElement(site, json) : null;
        }
        return EvaluateAsync(ctx, isEnabled, ctx.RequestServices.GetService<IDeployIntentValidator>(), currentSite);
    }

    /// <summary>
    /// Returns why the request is refused, or null when it may proceed.
    /// <paramref name="currentSite"/> gives the current config of a site as
    /// JSON (null when there is none): a PUT /api/sites/{domain} body carries
    /// the whole config, and only the fields it changes are judged. Without
    /// it every field in the body counts as a change.
    /// </summary>
    public static async Task<McpDenial?> EvaluateAsync(
        HttpContext ctx, Func<string, bool> isEnabled, IDeployIntentValidator? intentValidator,
        Func<string, JsonElement?>? currentSite = null)
    {
        var method = NormalizedMethod(ctx.Request.Method);
        // The router has already run (WebApplication adds UseRouting ahead of
        // the app's own middleware), so this is the route the request reaches.
        var pattern = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (pattern is null)
        {
            var unlisted = McpCapabilities.Get(McpCapabilities.UnlistedRoutes);
            return isEnabled(unlisted.Id) ? null : CapabilityDisabled(unlisted, $"{method} {ctx.Request.Path} matches no route");
        }

        var key = (method, CanonicalPattern(pattern));
        var routeText = $"{method} {pattern}";

        // Trust root: approving intents, managing grants, any other write under /api/mcp.
        if (NeverSet.Contains(key)
            || (method != "GET" && key.Item2.StartsWith("/api/mcp/", StringComparison.Ordinal) && !AlwaysSet.Contains(key)))
            return Forbidden($"{routeText} is never available to the MCP token: intents are approved and grants managed in the WDC app");

        McpRoute? route = null;
        McpCapability? routeCapability = null;
        if (!AlwaysSet.Contains(key))
        {
            if (GroupRoutes.TryGetValue(key, out var group))
                (routeCapability, route) = group;
            else
                routeCapability = McpCapabilities.Get(McpCapabilities.UnlistedRoutes);
            if (!isEnabled(routeCapability.Id))
                return CapabilityDisabled(routeCapability, routeText);
        }

        // Raw SQL in ?where= and a file path in ?out= skip the guards the MCP
        // tools rely on; the shipped tools send neither.
        if (key == TableBrowse && ctx.Request.Query.ContainsKey("where"))
            return Forbidden("?where= is raw SQL and not available to the MCP token; use wdc_query for filtered reads");
        if (key == BackupPost && ctx.Request.Query.ContainsKey("out"))
            return Forbidden("?out= is not available to the MCP token; backups are written to the backups folder");

        var needsBody = key == SettingsPut || key == SitePost || key == SitePut || key == CloudflareConfigPut || key == ConfigValidate
                        || route?.IntentTarget?.Contains("{body.", StringComparison.Ordinal) == true;
        IReadOnlyList<BodyProperty> body = [];
        if (needsBody)
        {
            // The endpoint would decode a body this policy cannot read (another
            // charset, say) and act on fields the checks below never saw.
            if (await ReadObjectPropertiesAsync(ctx) is not { } read)
                return new(StatusCodes.Status400BadRequest, new
                {
                    error = "mcp_bad_body",
                    detail = $"{routeText} needs a UTF-8 JSON object body from the MCP token",
                });
            body = read;
        }

        if (key == SettingsPut)
        {
            var names = body.Select(p => p.Name).ToList();
            var mcpKeys = names.Where(IsMcpSetting).ToArray();
            if (mcpKeys.Length > 0)
                return Forbidden("mcp.* settings decide what the MCP token may do and are changed only in the WDC app: " + string.Join(", ", mcpKeys));
            var operatorKeys = names.Where(IsOperatorSetting).ToArray();
            if (operatorKeys.Length > 0 && !isEnabled(McpCapabilities.SettingsOperator))
                return CapabilityDisabled(McpCapabilities.Get(McpCapabilities.SettingsOperator), "operator-only settings: " + string.Join(", ", operatorKeys));
        }
        else if (key == SitePost || key == SitePut)
        {
            if (key == SitePut && currentSite is not null)
                body = ChangedProperties(body, currentSite(ExpandTarget("{domain}", ctx, body)));
            var setsStartCommand = body.Any(p =>
                p.Name.Equals("nodeStartCommand", StringComparison.OrdinalIgnoreCase)
                && !(p.Kind == JsonValueKind.Null || p.Kind == JsonValueKind.String && string.IsNullOrWhiteSpace(p.Text)));
            var extra = body
                .Select(p => p.Name)
                .Where(n => !n.Equals("nodeStartCommand", StringComparison.OrdinalIgnoreCase)
                            && !McpCapabilities.BasicSiteFields.Contains(n, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (extra.Length > 0 && !isEnabled(McpCapabilities.SiteAdvancedFields))
                return CapabilityDisabled(McpCapabilities.Get(McpCapabilities.SiteAdvancedFields), "site fields: " + string.Join(", ", extra));
            if (setsStartCommand)
            {
                var node = McpCapabilities.Get(McpCapabilities.NodeProcess);
                if (!isEnabled(node.Id))
                    return CapabilityDisabled(node, "nodeStartCommand runs a shell command");
                var fields = node.BodyFields.First(f => (f.Method.ToUpperInvariant(), CanonicalPattern(f.Pattern)) == key);
                if (await IntentGateAsync(ctx, intentValidator, node, fields.IntentKind!, fields.IntentTarget!, McpCapabilities.IntentHost, body) is { } denied)
                    return denied;
            }
        }
        else if (key == CloudflareConfigPut)
        {
            var credentials = body
                .Select(p => p.Name)
                .Where(n => McpCapabilities.CloudflareCredentialFields.Contains(n, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (credentials.Length > 0)
            {
                var cf = McpCapabilities.Get(McpCapabilities.CloudflareCredentials);
                if (!isEnabled(cf.Id))
                    return CapabilityDisabled(cf, "Cloudflare credential fields: " + string.Join(", ", credentials));
                var fields = cf.BodyFields.Single();
                if (await IntentGateAsync(ctx, intentValidator, cf, fields.IntentKind!, fields.IntentTarget!, McpCapabilities.IntentHost, body) is { } denied)
                    return denied;
            }
        }

        else if (key == ConfigValidate
                 && body.Any(p => p.Name.Equals("content", StringComparison.OrdinalIgnoreCase) && p.Kind != JsonValueKind.Null)
                 && !isEnabled(McpCapabilities.ServiceConfig))
        {
            // Validating a draft of an Apache include swaps it into the live file for the test run.
            return CapabilityDisabled(McpCapabilities.Get(McpCapabilities.ServiceConfig), "validate a config draft (content)");
        }
        else if (key == DatabaseExport && ctx.Request.Query.ContainsKey(McpCapabilities.ExportOutputPathParameter)
                 && !isEnabled(McpCapabilities.DatabaseAdmin))
        {
            return CapabilityDisabled(McpCapabilities.Get(McpCapabilities.DatabaseAdmin), "export to a file path (outputPath)");
        }

        if (route is { IntentKind: not null, IntentByEndpoint: false } && routeCapability is not null)
        {
            if (await IntentGateAsync(ctx, intentValidator, routeCapability, route.IntentKind, route.IntentTarget ?? "*", route.IntentHost, body) is { } denied)
                return denied;
        }
        return null;
    }

    private static McpDenial Forbidden(string detail) =>
        new(StatusCodes.Status403Forbidden, new { error = "mcp_forbidden", detail });

    private static McpDenial CapabilityDisabled(McpCapability capability, string detail) =>
        new(StatusCodes.Status403Forbidden, new
        {
            error = "mcp_capability_disabled",
            capability = capability.Id,
            settingKey = capability.SettingKey,
            detail,
            hint = $"The WDC operator has not allowed \"{capability.Title}\" for AI agents. " +
                   $"Enable it in WDC → Settings → MCP ({capability.SettingKey}).",
        });

    /// <summary>Expands an intent target template: <c>{name}</c> route value, <c>{body.name}</c> body field.</summary>
    internal static string ExpandTarget(string template, HttpContext ctx, IReadOnlyList<BodyProperty> body) =>
        Regex.Replace(template, @"\{(body\.)?([A-Za-z0-9_]+)\}", m =>
        {
            var name = m.Groups[2].Value;
            if (m.Groups[1].Success)
                return body.LastOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Text ?? "";
            return ctx.Request.RouteValues.TryGetValue(name, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
        });

    private static async Task<McpDenial?> IntentGateAsync(
        HttpContext ctx,
        IDeployIntentValidator? validator,
        McpCapability capability,
        string kind,
        string targetTemplate,
        string host,
        IReadOnlyList<BodyProperty> body)
    {
        var domain = ExpandTarget(targetTemplate, ctx, body);
        var hint = $"Ask the user to approve it: call wdc_request_confirmation for intent kind \"{kind}\" " +
                   $"on \"{domain}\", then send the approved intentToken as X-Intent-Token.";
        if (validator is null)
            return new(StatusCodes.Status403Forbidden, new
            {
                error = "intent_rejected", reason = "validator_unavailable", detail = (string?)null,
                capability = capability.Id, intentKind = kind, intentDomain = domain, intentHost = host, hint,
            });

        var token = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault() ?? "";
        var verdict = await validator.ValidateAndConsumeAsync(token, kind, domain, host, allowUnconfirmed: false, ctx.RequestAborted);
        if (verdict.Ok) return null;
        return new(verdict.Reason == "pending_confirmation" ? 425 : StatusCodes.Status403Forbidden, new
        {
            error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail,
            capability = capability.Id, intentKind = kind, intentDomain = domain, intentHost = host, hint,
        });
    }

    /// <summary>
    /// The body properties whose value differs from the same property (by
    /// name, ignoring case) of <paramref name="current"/>; all of them when
    /// there is no current object. A null value equals a missing property.
    /// </summary>
    internal static IReadOnlyList<BodyProperty> ChangedProperties(IReadOnlyList<BodyProperty> body, JsonElement? current)
    {
        if (current is not { ValueKind: JsonValueKind.Object } cur) return body;
        var existing = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cur.EnumerateObject()) existing[p.Name] = p.Value;
        return body.Where(p =>
        {
            if (!existing.TryGetValue(p.Name, out var was))
                return p.Kind != JsonValueKind.Null;
            if (p.Kind == JsonValueKind.Null && was.ValueKind == JsonValueKind.Null) return false;
            return !JsonElement.DeepEquals(p.Value, was);
        }).ToList();
    }

    /// <summary>Top-level properties of the JSON body; null when the body is not readable UTF-8 JSON.</summary>
    private static async Task<IReadOnlyList<BodyProperty>?> ReadObjectPropertiesAsync(HttpContext ctx)
    {
        var charset = ctx.Request.GetTypedHeaders().ContentType?.Charset.Value;
        if (!string.IsNullOrEmpty(charset)
            && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            && !charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            return null;
        if (ctx.Request.ContentLength == 0) return [];
        ctx.Request.EnableBuffering();
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject()
                    .Select(p => new BodyProperty(
                        p.Name, p.Value.ValueKind, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null, p.Value.Clone()))
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            // No body at all leaves nothing for the endpoint to act on either.
            ctx.Request.Body.Position = 0;
            return ctx.Request.Body.ReadByte() == -1 ? [] : null;
        }
        finally
        {
            ctx.Request.Body.Position = 0;
        }
    }
}
