using Microsoft.AspNetCore.Http;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Phase 7.3 — ambient caller identity for the in-flight HTTP request,
/// read by <see cref="DeployIntentValidator"/> to look up persistent
/// trust grants without changing the cross-ALC <c>IDeployIntentValidator</c>
/// contract (which would break every shipped plugin).
///
/// Set by the request-pipeline middleware in <c>Program.cs</c> from
/// the headers the MCP server injects on behalf of the agent:
/// <list type="bullet">
///   <item><c>X-Mcp-Session-Id</c>   — short-lived session token (per agent run)</item>
///   <item><c>X-Mcp-Api-Key-Id</c>   — durable identity fingerprint (per API key)</item>
///   <item><c>X-Mcp-Instance-Id</c>  — wdc instance UUID (this install)</item>
/// </list>
///
/// <para>
/// These ids are caller-supplied and are NOT authentication. Authentication
/// is the bearer token: <see cref="IsMcpToken"/> is decided by the auth
/// middleware from which token matched, and any process holding the MCP
/// token can send whatever X-Mcp-* values it likes. Session/instance/api-key
/// scoped grants are therefore a convenience boundary (they keep a grant
/// given to one agent run from silently applying to the next), not a
/// security boundary between MCP clients that share the token.
/// </para>
///
/// AsyncLocal is the right tool here: it flows through await points
/// inside the validator's <c>QueryAsync</c> calls without us threading
/// the context through every method signature.
/// </summary>
public static class McpCallerContext
{
    private static readonly AsyncLocal<string?> _sessionId  = new();
    private static readonly AsyncLocal<string?> _apiKeyId   = new();
    private static readonly AsyncLocal<string?> _instanceId = new();
    private static readonly AsyncLocal<bool> _isMcpToken = new();

    public static string? SessionId  { get => _sessionId.Value;  set => _sessionId.Value  = value; }
    public static string? ApiKeyId   { get => _apiKeyId.Value;   set => _apiKeyId.Value   = value; }
    public static string? InstanceId { get => _instanceId.Value; set => _instanceId.Value = value; }

    /// <summary>
    /// True when the request authenticated with the MCP token from
    /// <c>nks-wdc-mcp.port</c> rather than the GUI token. Unlike the
    /// X-Mcp-* headers this cannot be set by the caller: the auth middleware
    /// decides it from which token matched. MCP callers must present a
    /// confirmed intent on every destructive route, cannot use
    /// X-Allow-Unconfirmed, and cannot approve intents or create grants.
    /// </summary>
    public static bool IsMcpToken { get => _isMcpToken.Value; set => _isMcpToken.Value = value; }

    /// <summary>
    /// True when the caller carries at least one identifier the grants
    /// table can match against. False on internal calls, GUI calls (those
    /// don't need grants — the GUI banner IS the confirmation), and any
    /// route that didn't pass through the McpCallerContext middleware.
    /// </summary>
    public static bool HasAnyIdentity =>
        !string.IsNullOrEmpty(_sessionId.Value) ||
        !string.IsNullOrEmpty(_apiKeyId.Value)  ||
        !string.IsNullOrEmpty(_instanceId.Value);

    /// <summary>
    /// True when the grants table may be consulted for this caller: either
    /// the request authenticated with the MCP token (so 'always' grants
    /// apply even when the MCP server sent no identity headers) or the
    /// caller carries an identity a scoped grant can match.
    /// </summary>
    public static bool CanUseGrants => IsMcpToken || HasAnyIdentity;

    /// <summary>Upper bound for an X-Mcp-* identity value; longer values are ignored.</summary>
    public const int MaxIdentityLength = 128;

    /// <summary>
    /// Populate the identity slots from the X-Mcp-* request headers. Called
    /// once per /api request by the pipeline middleware in Program.cs.
    /// Values are trimmed; empty or over-long values are treated as absent.
    /// </summary>
    public static void ApplyIdentityHeaders(IHeaderDictionary headers)
    {
        SessionId  = ReadIdentity(headers, "X-Mcp-Session-Id");
        ApiKeyId   = ReadIdentity(headers, "X-Mcp-Api-Key-Id");
        InstanceId = ReadIdentity(headers, "X-Mcp-Instance-Id");
    }

    private static string? ReadIdentity(IHeaderDictionary headers, string name)
    {
        var raw = headers[name].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxIdentityLength) return null;
        return raw;
    }

    /// <summary>
    /// Reset all slots — for tests that share an AsyncLocal scope across
    /// cases, or for paranoid clearing at the end of a request handler.
    /// In normal operation each request gets a fresh AsyncLocal frame.
    /// </summary>
    public static void Clear()
    {
        _sessionId.Value = null;
        _apiKeyId.Value = null;
        _instanceId.Value = null;
        _isMcpToken.Value = false;
    }
}
