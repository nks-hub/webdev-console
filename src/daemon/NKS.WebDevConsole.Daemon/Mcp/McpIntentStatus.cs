using System.Data.Common;
using Dapper;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Read model for a single <c>deploy_intents</c> row, served by
/// <c>GET /api/mcp/intents/{id}</c>. The MCP agent polls it after asking the
/// user to approve an intent, so it reports a single <see cref="State"/>:
/// <c>pending_confirmation</c>, <c>ready</c>, <c>consumed</c>, <c>expired</c>
/// or <c>revoked</c>. The signature and nonce are never returned.
/// </summary>
public sealed record McpIntentStatus(
    string IntentId,
    string Kind,
    string Domain,
    string Host,
    string? ReleaseId,
    string State,
    string ExpiresAt,
    string? ConfirmedAt,
    string? UsedAt,
    string? RevokedAt,
    string CreatedAt,
    string? MatchedGrantId)
{
    /// <summary>
    /// Lifecycle state of an intent. A revoked intent also has <c>used_at</c>
    /// set (so the validator refuses it), which is why <c>revoked_at</c> is
    /// checked first.
    /// </summary>
    public static string ComputeState(
        string? usedAt, string? revokedAt, string? confirmedAt, string expiresAtRaw, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(revokedAt)) return "revoked";
        if (!string.IsNullOrEmpty(usedAt)) return "consumed";
        if (DateTimeOffset.TryParse(expiresAtRaw, out var exp) && exp < now) return "expired";
        if (string.IsNullOrEmpty(confirmedAt)) return "pending_confirmation";
        return "ready";
    }

    /// <summary>Loads one intent, or null when no row has that id.</summary>
    public static async Task<McpIntentStatus?> LoadAsync(DbConnection conn, string intentId, DateTimeOffset now)
    {
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            "SELECT id AS Id, domain AS Domain, host AS Host, release_id AS ReleaseId, kind AS Kind, " +
            "expires_at AS ExpiresAt, confirmed_at AS ConfirmedAt, used_at AS UsedAt, " +
            "revoked_at AS RevokedAt, created_at AS CreatedAt, matched_grant_id AS MatchedGrantId " +
            "FROM deploy_intents WHERE id = @Id",
            new { Id = intentId });
        if (row is null) return null;
        return new McpIntentStatus(
            row.Id, row.Kind, row.Domain, row.Host, row.ReleaseId,
            ComputeState(row.UsedAt, row.RevokedAt, row.ConfirmedAt, row.ExpiresAt, now),
            row.ExpiresAt, row.ConfirmedAt, row.UsedAt, row.RevokedAt, row.CreatedAt, row.MatchedGrantId);
    }

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Host { get; set; } = "";
        public string? ReleaseId { get; set; }
        public string Kind { get; set; } = "";
        public string ExpiresAt { get; set; } = "";
        public string? ConfirmedAt { get; set; }
        public string? UsedAt { get; set; }
        public string? RevokedAt { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? MatchedGrantId { get; set; }
    }
}
