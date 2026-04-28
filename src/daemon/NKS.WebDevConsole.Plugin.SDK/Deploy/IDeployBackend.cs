namespace NKS.WebDevConsole.Plugin.SDK.Deploy;

/// <summary>
/// Contract a deploy backend plugin implements. Lives in Plugin.SDK so the
/// daemon can resolve it across the AssemblyLoadContext boundary as a
/// shared type. v1 ships <c>NksDeployBackend</c> (PHP CLI subprocess) plus
/// a <c>LocalRsyncBackend</c> stub that proves the contract isn't shaped
/// inside-out around nksdeploy's vocabulary; future backends (Capistrano,
/// Kamal, custom) implement the same surface.
///
/// All methods are async and respect the supplied <see cref="CancellationToken"/>.
/// The daemon orchestrates lock acquisition, idempotency, audit logging, and
/// SSE fan-out — the backend itself only owns the deploy mechanics.
/// </summary>
public interface IDeployBackend
{
    /// <summary>Stable identifier used in routing, audit logs, and DI keys.</summary>
    string BackendId { get; }

    /// <summary>
    /// Quick predicate: can this backend deploy the given site? Lets the
    /// daemon pick among multiple registered backends without invoking
    /// expensive probes. Implementations should return false fast and
    /// avoid I/O — full validation happens later.
    /// </summary>
    bool CanDeploy(string domain);

    /// <summary>
    /// Start a deploy. Returns the assigned deployId immediately; progress
    /// flows through <paramref name="progress"/> until a terminal event
    /// (<see cref="DeployEvent.IsTerminal"/> = true) arrives. The returned
    /// task completes when the deploy reaches a terminal state.
    /// </summary>
    Task<string> StartDeployAsync(
        DeployRequest request,
        IProgress<DeployEvent> progress,
        CancellationToken ct);

    /// <summary>Look up a deploy run by id (in-flight or historical).</summary>
    Task<DeployResult> GetStatusAsync(string deployId, CancellationToken ct);

    /// <summary>
    /// Recent deploy history for a domain. Implementations may cap the
    /// return size — caller passes <paramref name="limit"/> as a hint.
    /// </summary>
    Task<IReadOnlyList<DeployHistoryEntry>> GetHistoryAsync(
        string domain,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// Roll the live release back. Implementations decide what "rollback"
    /// means for their model (symlink swap, image-tag swap, snapshot
    /// restore). The daemon enforces that this is only callable when the
    /// referenced deploy is in or past the irreversible phase.
    /// </summary>
    Task RollbackAsync(string deployId, CancellationToken ct);

    /// <summary>
    /// Best-effort cancel. Valid only before <see cref="DeployEvent.IsPastPonr"/>;
    /// the daemon refuses to call this once PONR has been crossed (caller
    /// must use <see cref="RollbackAsync"/> instead).
    /// </summary>
    Task CancelAsync(string deployId, CancellationToken ct);
}
