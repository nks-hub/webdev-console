namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Phase 7.3 — read/write surface over the <c>mcp_session_grants</c> table.
/// A grant is "I trust caller X to perform kind Y against target Z for the
/// next N minutes (or forever)". The intent validator consults this repo
/// BEFORE returning <c>pending_confirmation</c>; a matching active grant
/// pre-stamps <c>confirmed_at</c> and the intent proceeds without the GUI
/// banner. Grants are persisted across daemon restarts (SQLite) — that's
/// the entire point versus the existing per-call <c>X-Allow-Unconfirmed</c>
/// header, which has to be re-asserted on every request.
///
/// Cross-ALC safe: the row record uses primitive types only; no plugin
/// type leaks across the boundary.
/// </summary>
public interface IMcpSessionGrantsRepository
{
    /// <summary>
    /// List all currently-active grants (revoked_at IS NULL AND expires_at
    /// is either NULL or in the future), newest first. Used by the GUI
    /// "MCP Grants" page and the validator's pre-check.
    /// </summary>
    Task<IReadOnlyList<McpSessionGrantRow>> ListActiveAsync(CancellationToken ct);

    /// <summary>
    /// Phase 7.5+++ — list EVERY grant row including revoked + expired,
    /// newest first. Forensic / audit use case ("show me which trust
    /// rules were active 30 days ago"). The validator path NEVER calls
    /// this — only operator UIs that explicitly opt into the full view.
    /// </summary>
    Task<IReadOnlyList<McpSessionGrantRow>> ListAllAsync(CancellationToken ct);

    /// <summary>
    /// Insert a new grant. The row gets a UUID v4 PK if <paramref name="id"/>
    /// is null; the caller can pass an explicit id (e.g. when restoring from
    /// backup) but normal flows leave it null.
    /// </summary>
    Task<string> InsertAsync(McpSessionGrantRow row, CancellationToken ct);

    /// <summary>
    /// Soft-revoke (stamps <c>revoked_at</c> = UTC now). Returns true if a
    /// row was updated, false if the id was unknown or already revoked.
    /// </summary>
    Task<bool> RevokeAsync(string id, CancellationToken ct);

    /// <summary>
    /// Phase 7.5+++ — partial update of operator-tunable fields on an
    /// existing grant. Identity fields (scope_type, scope_value, kind/
    /// target patterns, granted_at, granted_by) are immutable — change
    /// those would break the audit chain. Telemetry (match_count,
    /// last_matched_at) is also untouched. Returns true if a row was
    /// updated, false if the id was unknown.
    ///
    /// Null parameters mean "leave unchanged"; pass the new value to
    /// overwrite. <paramref name="expiresAtIso"/> uses sentinel
    /// <c>"__null__"</c> to explicitly clear (set to permanent), since
    /// plain null would mean "don't touch".
    /// </summary>
    Task<bool> UpdateMutableAsync(
        string id,
        int? minCooldownSeconds,
        string? expiresAtIso,
        string? note,
        CancellationToken ct);

    /// <summary>
    /// Find the FIRST active grant that matches the calling context.
    /// Match rules:
    /// <list type="bullet">
    ///   <item>scope_type='always' AND no scope_value → matches any caller</item>
    ///   <item>scope_type='session' AND scope_value=<paramref name="sessionId"/></item>
    ///   <item>scope_type='instance' AND scope_value=<paramref name="instanceId"/></item>
    ///   <item>scope_type='api_key' AND scope_value=<paramref name="apiKeyId"/></item>
    /// </list>
    /// AND kind_pattern is '*' OR equals <paramref name="kind"/>
    /// AND target_pattern is '*' OR equals <paramref name="target"/>
    /// AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at &gt; now).
    /// Returns null when no grant matches (validator falls back to GUI flow).
    /// </summary>
    Task<McpSessionGrantRow?> FindMatchingActiveAsync(
        string? sessionId,
        string? instanceId,
        string? apiKeyId,
        string kind,
        string target,
        CancellationToken ct);

    /// <summary>
    /// Phase 7.5+++ — bump <c>match_count</c> and <c>last_matched_at</c>
    /// for the given grant id. Called by <c>DeployIntentValidator</c>
    /// when a <see cref="FindMatchingActiveAsync"/> hit auto-confirms an
    /// intent. Idempotent in the sense that two concurrent validators
    /// matching the same grant just produce two increments (the column
    /// is +1, not "set to N"). Best-effort: telemetry write failure is
    /// never allowed to break the auth path.
    /// </summary>
    Task RecordMatchAsync(string id, CancellationToken ct);
}

/// <summary>
/// One row of <c>mcp_session_grants</c>. Primitive-typed for cross-ALC
/// safety. Timestamps are ISO-8601 UTC strings (matches the SQLite text
/// representation used elsewhere in the schema).
/// </summary>
public sealed record McpSessionGrantRow(
    string? Id,
    string ScopeType,
    string? ScopeValue,
    string KindPattern,
    string TargetPattern,
    string GrantedAt,
    string? ExpiresAt,
    string GrantedBy,
    string? RevokedAt,
    string? Note,
    // Phase 7.5+++ — telemetry. Both default to "never matched" so existing
    // consumers (tests, plugin SDK) compile without forced ctor changes.
    int MatchCount = 0,
    string? LastMatchedAt = null,
    // Phase 7.5+++ — rate limit (0 = no cooldown, current behavior).
    int MinCooldownSeconds = 0);
