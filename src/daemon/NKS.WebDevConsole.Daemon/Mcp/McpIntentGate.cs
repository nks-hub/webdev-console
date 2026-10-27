using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// The intent gate the destructive routes run inline, as one call: when the
/// request carries <c>X-Intent-Token</c> or comes from the MCP token, the
/// intent must be valid, approved and match (kind, domain, host). Requests
/// from the GUI token without a token header pass untouched, so GUI buttons
/// keep working. <c>X-Allow-Unconfirmed</c> is honoured for the GUI token
/// only (the validator ignores it for the MCP token).
/// </summary>
public static class McpIntentGate
{
    /// <summary>Returns the refusal to send, or null when the request may proceed.</summary>
    public static async Task<IResult?> CheckAsync(
        HttpContext ctx, IDeployIntentValidator validator, string kind, string domain, string host, CancellationToken ct)
    {
        var token = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(token) && !McpCallerContext.IsMcpToken) return null;

        var allowUnconfirmed = string.Equals(
            ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
        var verdict = await validator.ValidateAndConsumeAsync(token ?? "", kind, domain, host, allowUnconfirmed, ct);
        if (verdict.Ok) return null;
        return Results.Json(
            new { error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail },
            statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
    }
}
