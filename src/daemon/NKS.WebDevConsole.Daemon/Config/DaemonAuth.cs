using System.Security.Cryptography;
using System.Text;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Bearer-token gate for the daemon's /api surface. A request carries either
/// the GUI token (full access) or the MCP token (refused outright while
/// <c>mcp.enabled</c> is off, otherwise limited by <see cref="McpTokenPolicy"/>
/// and the operator's MCP capabilities). Lifted out of Program.cs so tests can
/// run the exact production gate against real routing.
/// </summary>
internal static class DaemonAuth
{
    public static IApplicationBuilder UseDaemonAuth(this IApplicationBuilder app, string authToken, string mcpToken)
    {
        // SECURITY: constant-time comparison so timing cannot leak the token.
        var authTokenBytes = Encoding.UTF8.GetBytes(authToken);
        var mcpTokenBytes = Encoding.UTF8.GetBytes(mcpToken);

        return app.Use(async (ctx, next) =>
        {
            if (IsApiRequest(ctx))
            {
                // Parse Authorization: Bearer <token> — use prefix check instead of Replace() which
                // is fragile (multiple occurrences, whitespace) and doesn't handle case variants.
                var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
                string? provided = null;
                if (!string.IsNullOrEmpty(authHeader) &&
                    authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    provided = authHeader.Substring(7).Trim();
                }
                // SSE/WebSocket fallback: browser EventSource/WebSocket APIs cannot set
                // Authorization headers, so token-via-query is allowed *only* on
                // those streaming endpoints. Plain /api/* requests must use the header
                // — a query-string fallback there leaked the bearer into Kestrel
                // access logs and any log-shipping pipeline (Sentry breadcrumb,
                // file sink) for the token's full TTL.
                if (provided is null)
                {
                    var path = ctx.Request.Path.Value ?? string.Empty;
                    var streamingPath = path.StartsWith("/api/events", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/api/ws", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/api/backup/download", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/api/plugins/", StringComparison.OrdinalIgnoreCase)
                        && path.EndsWith("/icon", StringComparison.OrdinalIgnoreCase);
                    if (streamingPath)
                    {
                        provided = ctx.Request.Query["token"].FirstOrDefault();
                    }
                }

                bool ok = false;
                bool isMcp = false;
                if (!string.IsNullOrEmpty(provided))
                {
                    var providedBytes = Encoding.UTF8.GetBytes(provided);
                    // FixedTimeEquals requires same length — reject different length fast
                    ok = providedBytes.Length == authTokenBytes.Length
                         && CryptographicOperations.FixedTimeEquals(providedBytes, authTokenBytes);
                    if (!ok)
                    {
                        isMcp = providedBytes.Length == mcpTokenBytes.Length
                                && CryptographicOperations.FixedTimeEquals(providedBytes, mcpTokenBytes);
                        ok = isMcp;
                    }
                }

                if (!ok)
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsync("Unauthorized");
                    return;
                }

                McpCallerContext.IsMcpToken = isMcp;
                ActivityContext.ViaCli = string.Equals(
                    ctx.Request.Headers[ActivityContext.ClientHeader].FirstOrDefault(), "cli", StringComparison.OrdinalIgnoreCase);
                if (isMcp)
                {
                    var denied = await McpTokenPolicy.EvaluateAsync(ctx);
                    if (denied is not null)
                    {
                        ctx.Response.StatusCode = denied.StatusCode;
                        await ctx.Response.WriteAsJsonAsync(denied.Body, denied.Body.GetType());
                        return;
                    }
                }
            }
            await next();
        });
    }

    /// <summary>
    /// True for anything that reaches, or could reach, an /api route: the path
    /// (with repeated slashes collapsed, any case) starts with the /api
    /// segment, or the router matched an /api route pattern. The second check
    /// keeps the gate on even if a path spelling the router accepts would
    /// not pass the first one.
    /// </summary>
    internal static bool IsApiRequest(HttpContext ctx)
    {
        var raw = ctx.Request.Path.Value ?? string.Empty;
        var collapsed = raw.Contains("//", StringComparison.Ordinal)
            ? System.Text.RegularExpressions.Regex.Replace(raw, "/{2,}", "/")
            : raw;
        if (new PathString(collapsed).StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return true;
        var pattern = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (pattern is null) return false;
        var canonical = McpTokenPolicy.CanonicalPattern(pattern);
        return canonical == "/api" || canonical.StartsWith("/api/", StringComparison.Ordinal);
    }
}
