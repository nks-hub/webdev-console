namespace NKS.WebDevConsole.Plugin.SDK.Deploy;

/// <summary>
/// Plugin-side contract for atomic multi-host deploys. The daemon resolves
/// this through the same plugin DI container that hosts the per-backend
/// <see cref="IDeployBackend"/> — the coordinator is expected to compose
/// the underlying single-host backend's <c>StartDeployAsync</c> /
/// <c>RollbackAsync</c> calls in parallel, NOT to re-implement the deploy
/// pipeline.
///
/// State machine (DeployGroupPhase):
///   Initializing → Preflight → Deploying → AwaitingAllSoak →
///       AllSucceeded                            (happy path)
///   Preflight | Deploying | AwaitingAllSoak →
///       RollingBackAll → RolledBack             (any failure pre-PONR)
///   AwaitingAllSoak →
///       PartialFailure                          (failure post-PONR)
///   Initializing | Preflight →
///       GroupFailed                             (no host crossed PONR)
///
/// Implementations MUST persist state transitions through the daemon's
/// <c>IDeployGroupRepository</c> so the GUI's group drawer survives a
/// daemon restart (stale-group recovery follows the same pattern as
/// stale single-host runs from Phase 5.1).
/// </summary>
public interface IDeployGroupCoordinator
{
    /// <summary>
    /// Mint a groupId, INSERT the deploy_groups row (phase=initializing),
    /// fan out preflight + deploys, return the groupId immediately. Caller
    /// observes progress via the <paramref name="progress"/> sink (which
    /// the daemon REST handler bridges onto the SSE
    /// <c>deploy:group-event</c> channel) or by polling
    /// <see cref="GetGroupStatusAsync"/>.
    /// </summary>
    Task<string> StartGroupAsync(
        DeployGroupRequest req,
        IProgress<DeployGroupEvent> progress,
        CancellationToken ct);

    /// <summary>
    /// Read the current group state. Returns null when the groupId is
    /// unknown so REST handlers can map to 404 cleanly.
    /// </summary>
    Task<DeployGroupStatus?> GetGroupStatusAsync(string groupId, CancellationToken ct);

    /// <summary>
    /// Roll back EVERY host in the group that has a successful release.
    /// Hosts that never crossed PONR are no-ops. Hosts where the rollback
    /// itself fails leave the group in <see cref="DeployGroupPhase.PartialFailure"/>
    /// — the operator must inspect the per-host deploy_runs rows to decide
    /// next steps. Throws <see cref="KeyNotFoundException"/> for unknown id.
    /// </summary>
    Task RollbackGroupAsync(string groupId, CancellationToken ct);
}
