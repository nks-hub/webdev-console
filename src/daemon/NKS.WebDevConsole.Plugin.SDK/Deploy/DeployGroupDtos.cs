using System.Text.Json;

namespace NKS.WebDevConsole.Plugin.SDK.Deploy;

/// <summary>
/// Phase 6.1 — atomic multi-host deploy of one site to several hosts in a
/// single user-initiated operation (e.g. <c>staging</c> +
/// <c>production-eu</c> + <c>production-us</c>). Either every host's deploy
/// succeeds or every host that already crossed PONR is rolled back. Hosts
/// that failed BEFORE PONR are simply abandoned (their deploy_runs row
/// terminates as cancelled / failed); the GUI surfaces the group as
/// <see cref="DeployGroupPhase.RolledBack"/>.
///
/// All types are cross-ALC safe — only BCL primitives and SDK records.
/// </summary>
public enum DeployGroupPhase
{
    /// <summary>Group row inserted, no per-host work started yet.</summary>
    Initializing,
    /// <summary>Per-host preflight checks running in parallel.</summary>
    Preflight,
    /// <summary>Per-host deploys running in parallel.</summary>
    Deploying,
    /// <summary>All hosts past PONR, awaiting health-check soak window.</summary>
    AwaitingAllSoak,
    /// <summary>Every host reached <c>completed</c> — terminal happy path.</summary>
    AllSucceeded,
    /// <summary>
    /// At least one host failed AFTER PONR; the failed host's release is
    /// stuck (rollback there is the operator's call), other hosts have
    /// been rolled back. Manual intervention required.
    /// </summary>
    PartialFailure,
    /// <summary>Active rollback fan-out in progress.</summary>
    RollingBackAll,
    /// <summary>Every host that progressed has been rolled back. Terminal.</summary>
    RolledBack,
    /// <summary>
    /// Group ended before any deploy crossed PONR — preflight failure or
    /// early deploy failure on enough hosts that we abandoned the rest.
    /// No release tree was switched on any host.
    /// </summary>
    GroupFailed,
}

/// <summary>
/// Payload for <see cref="IDeployGroupCoordinator.StartGroupAsync"/>. The
/// host list must be non-empty and de-duplicated; per-host
/// <see cref="DeployRequest.BackendOptions"/> are derived from the group's
/// shared <paramref name="BackendOptions"/> at fan-out time so all hosts
/// see consistent flags (branch override etc.).
/// </summary>
public sealed record DeployGroupRequest(
    string Domain,
    IReadOnlyList<string> Hosts,
    string IdempotencyKey,
    string TriggeredBy,
    JsonElement BackendOptions);

/// <summary>
/// Wire envelope for SSE channel <c>deploy:group-event</c>. Per-host events
/// (DeployId + Host populated) and group-level transitions (both null) ride
/// the same channel so the GUI's group drawer can render a single
/// chronological log without de-multiplexing two streams.
/// </summary>
public sealed record DeployGroupEvent(
    string GroupId,
    string? DeployId,
    string? Host,
    DeployGroupPhase Phase,
    string Step,
    string Message,
    DateTimeOffset Timestamp,
    bool IsTerminal);

/// <summary>
/// Snapshot of a group as returned by GET endpoint and the terminal SSE
/// envelope. <see cref="HostDeployIds"/> is the join key into deploy_runs
/// for per-host detail (history page deep-link, log scroll-back).
/// </summary>
public sealed record DeployGroupStatus(
    string GroupId,
    string Domain,
    IReadOnlyList<string> Hosts,
    DeployGroupPhase Phase,
    IReadOnlyDictionary<string, string> HostDeployIds,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);
