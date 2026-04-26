using System.Text.Json;

namespace NKS.WebDevConsole.Plugin.SDK.Deploy;

/// <summary>
/// Phases of a deploy lifecycle, broad enough that future backends
/// (Capistrano, Kamal, custom rsync) can map their internal state machine
/// onto these. Specific step names live in <see cref="DeployEvent.Step"/>;
/// this enum just classifies the macro-state for UI rendering and the
/// (domain, host) lock state machine.
/// </summary>
public enum DeployPhase
{
    Queued,
    PreflightChecks,
    Fetching,
    Building,
    Migrating,
    AboutToSwitch,
    Switched,
    HealthCheck,
    AwaitingSoak,
    Done,
    Failed,
    Cancelled,
    RollingBack,
    RolledBack,
}

/// <summary>
/// Inbound deploy request. Caller-supplied IdempotencyKey lets the daemon
/// dedupe duplicate POSTs (10-min TTL); BackendOptions is opaque JSON the
/// concrete backend (NksDeployBackend, future LocalRsyncBackend, etc.)
/// deserializes itself — keeps the SDK contract narrow and avoids churn
/// when backends gain new options.
/// </summary>
public sealed record DeployRequest(
    string Domain,
    string Host,
    string IdempotencyKey,
    string TriggeredBy,
    JsonElement BackendOptions,
    /// <summary>
    /// Phase 6.2 — opt-in pre-deploy DB snapshot. When non-null and
    /// <see cref="DeploySnapshotOptions.Include"/> is true, the backend
    /// dumps the site's database BEFORE spawning the deploy. Snapshot
    /// failure is fatal — the deploy never starts.
    /// </summary>
    DeploySnapshotOptions? Snapshot = null);

/// <summary>
/// Pre-deploy database snapshot configuration. Defaults are conservative
/// — opt-in only, 30-day retention so an old prod restore is still
/// possible after a few weeks.
/// </summary>
public sealed record DeploySnapshotOptions(
    bool Include,
    int? RetentionDays = 30);

/// <summary>
/// One progress event emitted via <see cref="IProgress{T}"/> during a
/// running deploy. <see cref="IsTerminal"/> marks the final event for the
/// run; <see cref="IsPastPonr"/> flips true once the irreversible step
/// (e.g. nksdeploy's symlink_switch at priority 70) has crossed —
/// the wdc UI uses it to switch the cancel button to "rollback only".
/// </summary>
public sealed record DeployEvent(
    string DeployId,
    DeployPhase Phase,
    string Step,
    string Message,
    DateTimeOffset Timestamp,
    bool IsTerminal,
    bool IsPastPonr);

/// <summary>
/// Snapshot of a deploy's current or final state. Returned by
/// <see cref="IDeployBackend.GetStatusAsync"/> and emitted as the body of
/// the terminal SSE event.
/// </summary>
public sealed record DeployResult(
    string DeployId,
    bool Success,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ReleaseId,
    string? CommitSha,
    DeployPhase FinalPhase);

/// <summary>
/// Historical deploy record. Backends can synthesize these from their own
/// remote state (nksdeploy reads /.dep/history.json over SSH; LocalRsync
/// reads its local journal; etc.). Surfaced by the wdc history page and
/// the wdc_deploy_history MCP tool.
/// </summary>
public sealed record DeployHistoryEntry(
    string DeployId,
    string Domain,
    string Host,
    string Branch,
    DeployPhase FinalPhase,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? CommitSha,
    string? ReleaseId,
    string? Error);
