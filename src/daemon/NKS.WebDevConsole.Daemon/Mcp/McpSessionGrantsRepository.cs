using Dapper;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Phase 7.3 — SQLite-backed <see cref="IMcpSessionGrantsRepository"/>
/// (migration 012). Same operational pattern as
/// <see cref="DeployIntentValidator"/>: every method opens its own
/// connection (SQLite serialises writes; pooling is unnecessary for
/// per-instance daemon traffic).
///
/// Hot path is <see cref="FindMatchingActiveAsync"/>, called by the
/// intent validator on every destructive op. The partial index
/// <c>idx_mcp_session_grants_active</c> keeps it O(log n) over active
/// rows only.
/// </summary>
public sealed class McpSessionGrantsRepository : IMcpSessionGrantsRepository
{
    private readonly Database _db;

    public McpSessionGrantsRepository(Database db) { _db = db; }

    public async Task<IReadOnlyList<McpSessionGrantRow>> ListActiveAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<RawRow>(
            "SELECT id AS Id, scope_type AS ScopeType, scope_value AS ScopeValue, " +
            "kind_pattern AS KindPattern, target_pattern AS TargetPattern, " +
            "granted_at AS GrantedAt, expires_at AS ExpiresAt, granted_by AS GrantedBy, " +
            "revoked_at AS RevokedAt, note AS Note, " +
            "match_count AS MatchCount, last_matched_at AS LastMatchedAt " +
            "FROM mcp_session_grants " +
            "WHERE revoked_at IS NULL " +
            "  AND (expires_at IS NULL OR expires_at > strftime('%Y-%m-%dT%H:%M:%fZ','now')) " +
            "ORDER BY granted_at DESC");
        return rows.Select(ToRecord).ToList();
    }

    public async Task<string> InsertAsync(McpSessionGrantRow row, CancellationToken ct)
    {
        var id = string.IsNullOrEmpty(row.Id) ? Guid.NewGuid().ToString("D") : row.Id;
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO mcp_session_grants " +
            "(id, scope_type, scope_value, kind_pattern, target_pattern, " +
            " granted_at, expires_at, granted_by, note) " +
            "VALUES (@Id, @ScopeType, @ScopeValue, @KindPattern, @TargetPattern, " +
            " @GrantedAt, @ExpiresAt, @GrantedBy, @Note)",
            new
            {
                Id = id,
                row.ScopeType,
                row.ScopeValue,
                row.KindPattern,
                row.TargetPattern,
                GrantedAt = string.IsNullOrEmpty(row.GrantedAt)
                    ? DateTimeOffset.UtcNow.ToString("o")
                    : row.GrantedAt,
                row.ExpiresAt,
                row.GrantedBy,
                row.Note,
            });
        return id;
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            "UPDATE mcp_session_grants SET revoked_at = @Now " +
            "WHERE id = @Id AND revoked_at IS NULL",
            new { Id = id, Now = DateTimeOffset.UtcNow.ToString("o") });
        return rows > 0;
    }

    public async Task<McpSessionGrantRow?> FindMatchingActiveAsync(
        string? sessionId,
        string? instanceId,
        string? apiKeyId,
        string kind,
        string target,
        CancellationToken ct)
    {
        // Build the scope OR clause: 'always' grants always match; identity
        // grants match only when the caller carries the corresponding id.
        // Skipping a NULL identity slot keeps this from accidentally matching
        // grants for OTHER callers ('always' is the only no-id match).
        var clauses = new List<string> { "(scope_type = 'always')" };
        var p = new DynamicParameters();
        p.Add("Kind", kind);
        p.Add("Target", target);

        if (!string.IsNullOrEmpty(sessionId))
        {
            clauses.Add("(scope_type = 'session' AND scope_value = @SessionId)");
            p.Add("SessionId", sessionId);
        }
        if (!string.IsNullOrEmpty(instanceId))
        {
            clauses.Add("(scope_type = 'instance' AND scope_value = @InstanceId)");
            p.Add("InstanceId", instanceId);
        }
        if (!string.IsNullOrEmpty(apiKeyId))
        {
            clauses.Add("(scope_type = 'api_key' AND scope_value = @ApiKeyId)");
            p.Add("ApiKeyId", apiKeyId);
        }

        var sql =
            "SELECT id AS Id, scope_type AS ScopeType, scope_value AS ScopeValue, " +
            "kind_pattern AS KindPattern, target_pattern AS TargetPattern, " +
            "granted_at AS GrantedAt, expires_at AS ExpiresAt, granted_by AS GrantedBy, " +
            "revoked_at AS RevokedAt, note AS Note, " +
            "match_count AS MatchCount, last_matched_at AS LastMatchedAt " +
            "FROM mcp_session_grants " +
            "WHERE revoked_at IS NULL " +
            "  AND (expires_at IS NULL OR expires_at > strftime('%Y-%m-%dT%H:%M:%fZ','now')) " +
            "  AND (kind_pattern = '*' OR kind_pattern = @Kind) " +
            "  AND (target_pattern = '*' OR target_pattern = @Target) " +
            "  AND (" + string.Join(" OR ", clauses) + ") " +
            "ORDER BY granted_at DESC LIMIT 1";

        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var raw = await conn.QuerySingleOrDefaultAsync<RawRow>(sql, p);
        return raw is null ? null : ToRecord(raw);
    }

    public async Task RecordMatchAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(
                "UPDATE mcp_session_grants SET " +
                "  match_count = match_count + 1, " +
                "  last_matched_at = @Now " +
                "WHERE id = @Id",
                new { Id = id, Now = DateTimeOffset.UtcNow.ToString("o") });
        }
        catch
        {
            // Telemetry must NEVER break the auth path. The auto-confirm
            // already happened upstream; failure here just means the
            // counter doesn't tick. Next match retries.
        }
    }

    private static McpSessionGrantRow ToRecord(RawRow r) => new(
        r.Id, r.ScopeType, r.ScopeValue, r.KindPattern, r.TargetPattern,
        r.GrantedAt, r.ExpiresAt, r.GrantedBy, r.RevokedAt, r.Note,
        r.MatchCount, r.LastMatchedAt);

    /// <summary>Internal Dapper row.</summary>
    private sealed class RawRow
    {
        public string Id { get; set; } = "";
        public string ScopeType { get; set; } = "";
        public string? ScopeValue { get; set; }
        public string KindPattern { get; set; } = "*";
        public string TargetPattern { get; set; } = "*";
        public string GrantedAt { get; set; } = "";
        public string? ExpiresAt { get; set; }
        public string GrantedBy { get; set; } = "gui";
        public string? RevokedAt { get; set; }
        public string? Note { get; set; }
        public int MatchCount { get; set; }
        public string? LastMatchedAt { get; set; }
    }
}
