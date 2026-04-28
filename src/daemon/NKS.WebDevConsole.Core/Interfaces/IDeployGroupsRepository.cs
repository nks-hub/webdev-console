namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Persistence contract for the <c>deploy_groups</c> table (migration 009).
/// Mirrors the design of <see cref="IDeployRunsRepository"/> — the Core
/// assembly is in <c>PluginLoader.SharedAssemblies</c>, so plugin-side
/// coordinators (NksDeployGroupCoordinator, future LocalGroupCoordinator)
/// resolve the same interface across the AssemblyLoadContext boundary.
///
/// Hosts and host→deployId map are persisted as JSON strings in two
/// columns; this interface expresses them as IReadOnly* collections so
/// callers don't have to care about the encoding.
/// </summary>
public interface IDeployGroupsRepository
{
    /// <summary>Insert a new group (phase=initializing, no deployIds yet).</summary>
    Task InsertAsync(DeployGroupRow row, CancellationToken ct);

    /// <summary>
    /// Move a group to a new phase. Bumps updated_at via the migration's
    /// trigger. Sets <c>completed_at</c> (and optionally <c>error_message</c>)
    /// for terminal phases (AllSucceeded / RolledBack / GroupFailed /
    /// PartialFailure).
    /// </summary>
    Task UpdatePhaseAsync(
        string groupId,
        string phase,
        bool isTerminal,
        string? errorMessage,
        CancellationToken ct);

    /// <summary>
    /// Append (host → deployId) into the deploy_ids_json blob. Implemented
    /// as a read-modify-write on the single column; the deploy_groups row
    /// belongs to ONE coordinator at a time so concurrent writes from the
    /// same group never race.
    /// </summary>
    Task RecordHostDeployAsync(
        string groupId,
        string host,
        string deployId,
        CancellationToken ct);

    /// <summary>Return one row by id, or null if not present.</summary>
    Task<DeployGroupRow?> GetByIdAsync(string groupId, CancellationToken ct);

    /// <summary>
    /// Active groups (phase IN initializing/preflight/deploying/awaiting_all_soak/rolling_back_all).
    /// Daemon startup queries this for stale-group recovery — same pattern
    /// as <c>ListInFlightAsync</c> on deploy_runs.
    /// </summary>
    Task<IReadOnlyList<DeployGroupRow>> ListInFlightAsync(CancellationToken ct);

    /// <summary>Most recent N groups for a domain, newest started_at first.</summary>
    Task<IReadOnlyList<DeployGroupRow>> ListForDomainAsync(string domain, int limit, CancellationToken ct);
}

/// <summary>
/// Row shape for deploy_groups. JSON-encoded columns surface as decoded
/// collections so callers don't see the storage detail. Cross-ALC safe.
/// </summary>
public sealed record DeployGroupRow(
    string Id,
    string Domain,
    IReadOnlyList<string> Hosts,
    IReadOnlyDictionary<string, string> HostDeployIds,
    string Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage,
    string TriggeredBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
