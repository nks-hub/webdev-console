using System.Text.Json;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// What a caller holding the MCP token (an AI agent behind the MCP server)
/// may not do at all. Destructive operations are not listed here: those go
/// through the intent gate, which requires a GUI-confirmed intent for MCP
/// callers. These routes are refused outright because they would let the
/// agent approve its own intents, widen its own trust, or reconfigure the
/// daemon's trust anchors.
/// </summary>
public static class McpTokenPolicy
{
    // Mirrors PROTECTED_SETTING_PREFIXES in services/mcp-server/src/tools/settings.ts.
    public static readonly string[] ProtectedSettingPrefixes =
        ["mcp.", "deploy.", "daemon.", "plugins.", "sync.", "paths.", "telemetry."];
    public static readonly string[] ProtectedSettingKeys = ["backup.dir"];

    public static bool IsProtectedSetting(string key)
    {
        var k = key.Trim().ToLowerInvariant();
        return ProtectedSettingKeys.Contains(k) || ProtectedSettingPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>Returns why the request is refused, or null when it may proceed.</summary>
    public static async Task<string?> DenyReasonAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        var method = ctx.Request.Method;
        var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

        if (path.StartsWith("/api/mcp/intents/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/confirm", StringComparison.OrdinalIgnoreCase))
            return "intents are approved in the WDC app, not by the agent";

        if (path.StartsWith("/api/mcp/grants", StringComparison.OrdinalIgnoreCase) && !isRead)
            return "trust grants are managed in the WDC app";

        if (path.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase))
            return "admin routes are not available to the MCP token";

        if (path.Equals("/api/settings", StringComparison.OrdinalIgnoreCase) && !isRead)
        {
            var keys = await ReadSettingKeysAsync(ctx);
            var blocked = keys.Where(IsProtectedSetting).ToArray();
            if (blocked.Length > 0)
                return "operator-only settings: " + string.Join(", ", blocked);
        }
        return null;
    }

    private static async Task<IReadOnlyList<string>> ReadSettingKeysAsync(HttpContext ctx)
    {
        ctx.Request.EnableBuffering();
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().Select(p => p.Name).ToList()
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
