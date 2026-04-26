using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Deploy;

/// <summary>
/// SQLite-backed <see cref="IDeployGroupsRepository"/> (migration 009).
/// Same operational pattern as <see cref="DeployRunsRepository"/> — every
/// method is one round-trip; the repo owns SQL serialisation and the
/// coordinator owns orchestration.
///
/// JSON encoding details:
///   - Hosts → JSON string array.
///   - HostDeployIds → JSON object keyed by host.
/// Decoding errors fall back to empty collections so a corrupted blob
/// never crashes the daemon (logged-in-future hardening — for now the
/// JSON is daemon-written and trustworthy).
/// </summary>
public sealed class DeployGroupsRepository : IDeployGroupsRepository
{
    private readonly Database _db;

    public DeployGroupsRepository(Database db) { _db = db; }

    public async Task InsertAsync(DeployGroupRow row, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO deploy_groups (id, domain, hosts_json, deploy_ids_json, phase, " +
            "started_at, completed_at, error_message, triggered_by, created_at, updated_at) " +
            "VALUES (@Id, @Domain, @HostsJson, @DeployIdsJson, @Phase, @StartedAt, " +
            "@CompletedAt, @ErrorMessage, @TriggeredBy, @CreatedAt, @UpdatedAt)",
            new
            {
                row.Id,
                row.Domain,
                HostsJson = JsonSerializer.Serialize(row.Hosts),
                DeployIdsJson = JsonSerializer.Serialize(row.HostDeployIds),
                row.Phase,
                StartedAt = row.StartedAt.ToString("o"),
                CompletedAt = row.CompletedAt?.ToString("o"),
                row.ErrorMessage,
                row.TriggeredBy,
                CreatedAt = row.CreatedAt.ToString("o"),
                UpdatedAt = row.UpdatedAt.ToString("o"),
            });
    }

    public async Task UpdatePhaseAsync(
        string groupId,
        string phase,
        bool isTerminal,
        string? errorMessage,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        // Single statement covers terminal + non-terminal: completed_at
        // is set only when isTerminal=true. error_message is overwritten
        // every call, so callers must pass null to clear.
        await conn.ExecuteAsync(
            "UPDATE deploy_groups SET phase = @Phase, " +
            "completed_at = CASE WHEN @IsTerminal = 1 THEN @Now ELSE completed_at END, " +
            "error_message = @ErrorMessage, " +
            "updated_at = @Now " +
            "WHERE id = @Id",
            new
            {
                Id = groupId,
                Phase = phase,
                IsTerminal = isTerminal ? 1 : 0,
                ErrorMessage = errorMessage,
                Now = DateTimeOffset.UtcNow.ToString("o"),
            });
    }

    public async Task RecordHostDeployAsync(
        string groupId,
        string host,
        string deployId,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        // Read-modify-write the JSON map. The group is owned by ONE
        // coordinator (no fan-in writers), so a transaction would be
        // overkill — but we still read inside the same connection so
        // SQLite's default deferred-write semantics apply.
        var current = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT deploy_ids_json FROM deploy_groups WHERE id = @Id",
            new { Id = groupId }) ?? "{}";
        var map = ParseDeployIds(current);
        var mutable = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase)
        {
            [host] = deployId,
        };
        await conn.ExecuteAsync(
            "UPDATE deploy_groups SET deploy_ids_json = @Json, updated_at = @Now WHERE id = @Id",
            new
            {
                Id = groupId,
                Json = JsonSerializer.Serialize(mutable),
                Now = DateTimeOffset.UtcNow.ToString("o"),
            });
    }

    public async Task<DeployGroupRow?> GetByIdAsync(string groupId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var raw = await conn.QuerySingleOrDefaultAsync<RawRow>(
            BaseSelect + " WHERE id = @Id",
            new { Id = groupId });
        return raw?.ToRecord();
    }

    public async Task<IReadOnlyList<DeployGroupRow>> ListInFlightAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<RawRow>(
            BaseSelect +
            " WHERE phase IN ('initializing','preflight','deploying','awaiting_all_soak','rolling_back_all')");
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<DeployGroupRow>> ListForDomainAsync(string domain, int limit, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<RawRow>(
            BaseSelect + " WHERE domain = @Domain ORDER BY started_at DESC LIMIT @Limit",
            new { Domain = domain, Limit = Math.Clamp(limit, 1, 1000) });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    private const string BaseSelect =
        "SELECT id AS Id, domain AS Domain, hosts_json AS HostsJson, " +
        "deploy_ids_json AS DeployIdsJson, phase AS Phase, " +
        "started_at AS StartedAtRaw, completed_at AS CompletedAtRaw, " +
        "error_message AS ErrorMessage, triggered_by AS TriggeredBy, " +
        "created_at AS CreatedAtRaw, updated_at AS UpdatedAtRaw " +
        "FROM deploy_groups";

    private static IReadOnlyList<string> ParseHosts(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IReadOnlyDictionary<string, string> ParseDeployIds(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch { return new Dictionary<string, string>(); }
    }

    private sealed class RawRow
    {
        public string Id { get; set; } = "";
        public string Domain { get; set; } = "";
        public string HostsJson { get; set; } = "[]";
        public string DeployIdsJson { get; set; } = "{}";
        public string Phase { get; set; } = "initializing";
        public string StartedAtRaw { get; set; } = "";
        public string? CompletedAtRaw { get; set; }
        public string? ErrorMessage { get; set; }
        public string TriggeredBy { get; set; } = "gui";
        public string CreatedAtRaw { get; set; } = "";
        public string UpdatedAtRaw { get; set; } = "";

        public DeployGroupRow ToRecord() => new(
            Id,
            Domain,
            ParseHosts(HostsJson),
            ParseDeployIds(DeployIdsJson),
            Phase,
            DateTimeOffset.Parse(StartedAtRaw),
            string.IsNullOrEmpty(CompletedAtRaw) ? null : DateTimeOffset.Parse(CompletedAtRaw),
            ErrorMessage,
            TriggeredBy,
            DateTimeOffset.Parse(CreatedAtRaw),
            DateTimeOffset.Parse(UpdatedAtRaw));
    }
}
