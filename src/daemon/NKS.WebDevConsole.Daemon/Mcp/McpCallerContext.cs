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
/// AsyncLocal is the right tool here: it flows through await points
/// inside the validator's <c>QueryAsync</c> calls without us threading
/// the context through every method signature.
/// </summary>
public static class McpCallerContext
{
    private static readonly AsyncLocal<string?> _sessionId  = new();
    private static readonly AsyncLocal<string?> _apiKeyId   = new();
    private static readonly AsyncLocal<string?> _instanceId = new();

    public static string? SessionId  { get => _sessionId.Value;  set => _sessionId.Value  = value; }
    public static string? ApiKeyId   { get => _apiKeyId.Value;   set => _apiKeyId.Value   = value; }
    public static string? InstanceId { get => _instanceId.Value; set => _instanceId.Value = value; }

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
    /// Reset all slots — for tests that share an AsyncLocal scope across
    /// cases, or for paranoid clearing at the end of a request handler.
    /// In normal operation each request gets a fresh AsyncLocal frame.
    /// </summary>
    public static void Clear()
    {
        _sessionId.Value = null;
        _apiKeyId.Value = null;
        _instanceId.Value = null;
    }
}
