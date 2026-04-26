namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Cross-ALC contract used by plugin-side destructive endpoints
/// (NksDeploy's deploy / rollback / cancel handlers, future backends)
/// to validate an HMAC-signed intent token before they fire.
///
/// The MCP server crafts these tokens via <c>POST /api/mcp/intents</c>;
/// the daemon signs the canonical payload with a long-lived per-install
/// HMAC key (kept in <c>{WdcPaths.DataRoot}/mcp-hmac.key</c>, DPAPI-wrapped
/// on Windows, 0600 on POSIX). Validation is single-use — the row's
/// <c>used_at</c> is stamped atomically so a replay across two MCP sessions
/// can never re-fire the same intent.
///
/// Returning <see cref="IntentValidationResult"/> as a value type keeps
/// every plugin DI consumer cross-ALC clean (no plugin-defined exception
/// types to marshal across the boundary).
/// </summary>
public interface IDeployIntentValidator
{
    /// <summary>
    /// Validate <paramref name="intentToken"/> against the persisted intent
    /// row, the HMAC signature, the expiry window, the requested
    /// <paramref name="kind"/> ("deploy" / "rollback" / "cancel"), and the
    /// (domain, host) tuple the plugin is about to act on. On success,
    /// stamps <c>used_at</c> in the same transaction so the same token
    /// cannot fire twice — even from concurrent callers.
    /// </summary>
    Task<IntentValidationResult> ValidateAndConsumeAsync(
        string intentToken,
        string kind,
        string domain,
        string host,
        CancellationToken ct);
}

/// <summary>
/// Result of an intent validation. <see cref="Ok"/>=true means the token
/// matched, was within its expiry, was not previously used, and has now
/// been consumed; the caller is cleared to fire its destructive operation.
/// On failure, <see cref="Reason"/> carries a stable machine-readable code
/// (<c>not_found</c>, <c>expired</c>, <c>already_used</c>,
/// <c>scope_mismatch</c>, <c>signature_mismatch</c>) the plugin can echo
/// in its 401/403 response body so the MCP client can react sensibly.
/// </summary>
public sealed record IntentValidationResult(bool Ok, string? Reason)
{
    public static IntentValidationResult Allow() => new(true, null);
    public static IntentValidationResult Deny(string reason) => new(false, reason);
}
