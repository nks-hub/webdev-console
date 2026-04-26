namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Persistence contract for the <c>deploy_runs</c> table (migration 006).
/// Exposed via Core so plugin-side <c>IDeployBackend</c> implementations can
/// resolve it across the AssemblyLoadContext boundary — implementation lives
/// in the daemon (<c>DeployRunsRepository</c>).
///
/// All methods are thin SQL wrappers; the daemon's Database connection
/// factory handles pooling. Repositories own status transitions; backends
/// own the deploy mechanics.
/// </summary>
public interface IDeployRunsRepository
{
    /// <summary>
    /// Insert a new deploy_runs row. Caller supplies the UUID id (so it can
    /// echo it back to the API caller before the deploy task starts) and the
    /// initial metadata. Status defaults to 'queued' at the SQL layer.
    /// </summary>
    Task InsertAsync(DeployRunRow row, CancellationToken ct);

    /// <summary>
    /// Move the run to a new status (e.g. 'running', 'awaiting_soak',
    /// 'rolling_back'). Bumps updated_at via the migration's trigger.
    /// </summary>
    Task UpdateStatusAsync(string deployId, string status, CancellationToken ct);

    /// <summary>
    /// Flip is_past_ponr to 1. Idempotent — calling repeatedly is a no-op.
    /// Called from the backend the moment the irreversible step (symlink
    /// switch / image swap) succeeds, so the daemon's cancel handler can
    /// reject subsequent cancel requests with 409 deploy_past_ponr.
    /// </summary>
    Task MarkPastPonrAsync(string deployId, CancellationToken ct);

    /// <summary>
    /// Terminal-state writer. <paramref name="success"/> selects the final
    /// status ('completed' / 'failed'). Stores exit_code, error_message,
    /// duration_ms, and completed_at. Single round-trip.
    /// </summary>
    Task MarkCompletedAsync(
        string deployId,
        bool success,
        int? exitCode,
        string? errorMessage,
        long durationMs,
        CancellationToken ct);

    /// <summary>Return one row by id, or null if not present.</summary>
    Task<DeployRunRow?> GetByIdAsync(string deployId, CancellationToken ct);

    /// <summary>
    /// Most recent N rows for a domain, newest started_at first. Used by the
    /// history page and the wdc_deploy_history MCP tool.
    /// </summary>
    Task<IReadOnlyList<DeployRunRow>> ListForDomainAsync(string domain, int limit, CancellationToken ct);

    /// <summary>
    /// Phase 6.2 — record a successful pre-deploy snapshot on the run row.
    /// Migration 010 adds the two columns this writes (path + size).
    /// Idempotent: writes overwrite any prior values for the same run.
    /// </summary>
    Task UpdatePreDeployBackupAsync(
        string deployId,
        string path,
        long sizeBytes,
        CancellationToken ct);

    /// <summary>
    /// Phase 6.15b — list every per-host run that belongs to the given
    /// group_id. Used by the group history endpoint to enrich the
    /// hostDeployIds map with per-host terminal status, so the GUI can
    /// offer "replay only failed hosts" as a refined replay action.
    /// Order is unspecified — callers index by host name.
    /// </summary>
    Task<IReadOnlyList<DeployRunRow>> ListByGroupAsync(string groupId, CancellationToken ct);

    /// <summary>
    /// Phase 6.15b — stamp the group_id FK on a per-host deploy run.
    /// Called by the group coordinator after the per-host StartDeployAsync
    /// returns its deployId so the run row joins into ListByGroupAsync.
    /// Idempotent — overwrites any prior value.
    /// </summary>
    Task SetGroupIdAsync(string deployId, string groupId, CancellationToken ct);

    /// <summary>
    /// Rows still marked 'running' / 'awaiting_soak' / 'rolling_back'. Daemon
    /// queries this on startup for stale-run recovery — anything still here
    /// after a daemon restart had its supervising subprocess killed and needs
    /// the lock-cleanup flow.
    /// </summary>
    Task<IReadOnlyList<DeployRunRow>> ListInFlightAsync(CancellationToken ct);
}

/// <summary>
/// Row shape for deploy_runs. Mirrors the SQL columns 1:1 (snake_case mapped
/// to PascalCase by the repository's Dapper queries). Cross-ALC safe — only
/// BCL types.
/// </summary>
public sealed record DeployRunRow(
    string Id,
    string Domain,
    string Host,
    string? ReleaseId,
    string? Branch,
    string? CommitSha,
    string Status,
    bool IsPastPonr,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    string? ErrorMessage,
    long? DurationMs,
    string TriggeredBy,
    string BackendId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    /// <summary>Phase 6.2 — populated when a pre-deploy snapshot ran.</summary>
    string? PreDeployBackupPath = null,
    long? PreDeployBackupSizeBytes = null,
    /// <summary>
    /// Phase 6.19a — FK to deploy_groups when this run is part of a
    /// multi-host fan-out (set by NksDeployGroupCoordinator via
    /// SetGroupIdAsync). NULL for single-host deploys. Surface it here
    /// so the GUI can render a "View related group" link in the drawer.
    /// </summary>
    string? GroupId = null);
