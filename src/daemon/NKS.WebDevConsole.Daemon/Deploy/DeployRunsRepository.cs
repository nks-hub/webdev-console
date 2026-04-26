using Dapper;
using Microsoft.Data.Sqlite;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Deploy;

/// <summary>
/// Concrete <see cref="IDeployRunsRepository"/> backed by SQLite (migration
/// 006). Single-file table design — every method is one round-trip. Plugin-side
/// backends (NksDeployBackend, future LocalRsync/Capistrano/Kamal) own no
/// SQL — they call this through DI and let the daemon serialise persistence.
///
/// All timestamp columns are written as ISO-8601 UTC strings (matching the
/// migration's strftime defaults). Reading back via Dapper relies on
/// <c>DateTimeOffset</c> parameter binding, which Microsoft.Data.Sqlite
/// handles natively for ISO-8601 inputs.
/// </summary>
public sealed class DeployRunsRepository : IDeployRunsRepository
{
    private readonly Database _db;

    public DeployRunsRepository(Database db)
    {
        _db = db;
    }

    public async Task InsertAsync(DeployRunRow row, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO deploy_runs (id, domain, host, release_id, branch, commit_sha, status, " +
            "is_past_ponr, started_at, completed_at, exit_code, error_message, duration_ms, " +
            "triggered_by, backend_id, created_at, updated_at) " +
            "VALUES (@Id, @Domain, @Host, @ReleaseId, @Branch, @CommitSha, @Status, " +
            "@IsPastPonrInt, @StartedAt, @CompletedAt, @ExitCode, @ErrorMessage, @DurationMs, " +
            "@TriggeredBy, @BackendId, @CreatedAt, @UpdatedAt)",
            new
            {
                row.Id,
                row.Domain,
                row.Host,
                row.ReleaseId,
                row.Branch,
                row.CommitSha,
                row.Status,
                IsPastPonrInt = row.IsPastPonr ? 1 : 0,
                StartedAt = row.StartedAt.ToString("o"),
                CompletedAt = row.CompletedAt?.ToString("o"),
                row.ExitCode,
                row.ErrorMessage,
                row.DurationMs,
                row.TriggeredBy,
                row.BackendId,
                CreatedAt = row.CreatedAt.ToString("o"),
                UpdatedAt = row.UpdatedAt.ToString("o"),
            });
    }

    public async Task UpdateStatusAsync(string deployId, string status, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        // Bump updated_at explicitly — the trigger only fires when SOMETHING
        // else changes, and a status that's already the target value would
        // skip the trigger. The trigger's WHEN OLD = NEW guard then prevents
        // recursive re-entry from this explicit update.
        await conn.ExecuteAsync(
            "UPDATE deploy_runs SET status = @Status, updated_at = @UpdatedAt WHERE id = @Id",
            new { Id = deployId, Status = status, UpdatedAt = DateTimeOffset.UtcNow.ToString("o") });
    }

    public async Task MarkPastPonrAsync(string deployId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE deploy_runs SET is_past_ponr = 1, updated_at = @UpdatedAt " +
            "WHERE id = @Id AND is_past_ponr = 0",
            new { Id = deployId, UpdatedAt = DateTimeOffset.UtcNow.ToString("o") });
    }

    public async Task MarkCompletedAsync(
        string deployId,
        bool success,
        int? exitCode,
        string? errorMessage,
        long durationMs,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE deploy_runs SET status = @Status, exit_code = @ExitCode, " +
            "error_message = @ErrorMessage, duration_ms = @DurationMs, " +
            "completed_at = @CompletedAt, updated_at = @UpdatedAt WHERE id = @Id",
            new
            {
                Id = deployId,
                Status = success ? "completed" : "failed",
                ExitCode = exitCode,
                ErrorMessage = errorMessage,
                DurationMs = durationMs,
                CompletedAt = DateTimeOffset.UtcNow.ToString("o"),
                UpdatedAt = DateTimeOffset.UtcNow.ToString("o"),
            });
    }

    public async Task<DeployRunRow?> GetByIdAsync(string deployId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var raw = await conn.QuerySingleOrDefaultAsync<RawRow>(
            BaseSelect + " WHERE id = @Id",
            new { Id = deployId });
        return raw?.ToRecord();
    }

    public async Task<IReadOnlyList<DeployRunRow>> ListForDomainAsync(string domain, int limit, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<RawRow>(
            BaseSelect + " WHERE domain = @Domain ORDER BY started_at DESC LIMIT @Limit",
            new { Domain = domain, Limit = Math.Clamp(limit, 1, 1000) });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<DeployRunRow>> ListInFlightAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<RawRow>(
            BaseSelect + " WHERE status IN ('running', 'awaiting_soak', 'rolling_back')");
        return rows.Select(r => r.ToRecord()).ToList();
    }

    private const string BaseSelect =
        "SELECT id AS Id, domain AS Domain, host AS Host, release_id AS ReleaseId, " +
        "branch AS Branch, commit_sha AS CommitSha, status AS Status, " +
        "is_past_ponr AS IsPastPonrInt, started_at AS StartedAtRaw, " +
        "completed_at AS CompletedAtRaw, exit_code AS ExitCode, " +
        "error_message AS ErrorMessage, duration_ms AS DurationMs, " +
        "triggered_by AS TriggeredBy, backend_id AS BackendId, " +
        "created_at AS CreatedAtRaw, updated_at AS UpdatedAtRaw " +
        "FROM deploy_runs";

    /// <summary>
    /// Internal Dapper row. SQLite stores timestamps as TEXT; we map them to
    /// DateTimeOffset in the projection rather than relying on Dapper's
    /// DateTime conversion (which loses tz info in the round-trip).
    /// </summary>
    private sealed class RawRow
    {
        public string Id { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Host { get; set; } = "";
        public string? ReleaseId { get; set; }
        public string? Branch { get; set; }
        public string? CommitSha { get; set; }
        public string Status { get; set; } = "queued";
        public int IsPastPonrInt { get; set; }
        public string StartedAtRaw { get; set; } = "";
        public string? CompletedAtRaw { get; set; }
        public int? ExitCode { get; set; }
        public string? ErrorMessage { get; set; }
        public long? DurationMs { get; set; }
        public string TriggeredBy { get; set; } = "gui";
        public string BackendId { get; set; } = "nks-deploy";
        public string CreatedAtRaw { get; set; } = "";
        public string UpdatedAtRaw { get; set; } = "";

        public DeployRunRow ToRecord() => new(
            Id,
            Domain,
            Host,
            ReleaseId,
            Branch,
            CommitSha,
            Status,
            IsPastPonrInt != 0,
            DateTimeOffset.Parse(StartedAtRaw),
            string.IsNullOrEmpty(CompletedAtRaw) ? null : DateTimeOffset.Parse(CompletedAtRaw),
            ExitCode,
            ErrorMessage,
            DurationMs,
            TriggeredBy,
            BackendId,
            DateTimeOffset.Parse(CreatedAtRaw),
            DateTimeOffset.Parse(UpdatedAtRaw));
    }
}
