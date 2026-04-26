namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Phase 6.2 — creates a pre-deploy database snapshot for a site. Cross-ALC
/// interface so plugin-side <see cref="Plugin.SDK.Deploy.IDeployBackend"/>
/// implementations can request snapshots without depending on daemon
/// internals.
///
/// The daemon-side implementation discovers the site's database (MySQL /
/// PostgreSQL / SQLite) from the site config + linked daemon settings,
/// shells out to the appropriate dump tool (mysqldump / pg_dump / file
/// copy for SQLite), gzips the result, and writes it to
/// <c>{WdcPaths.BackupsRoot}/pre-deploy/{deployId}.sql.gz</c>.
///
/// Snapshot failure is fatal to the deploy — the backend should NOT start
/// the subprocess if <see cref="CreateAsync"/> throws. Callers can opt out
/// per-deploy via <c>DeployRequest.Snapshot</c>.
/// </summary>
public interface IPreDeploySnapshotter
{
    /// <summary>
    /// Create a snapshot for <paramref name="domain"/>, tagged with
    /// <paramref name="deployId"/>. Returns the absolute path + size of
    /// the resulting archive plus how long the dump took. Throws on any
    /// failure (no DB linked, dump tool missing, disk full, permission
    /// denied) — callers translate to <c>error_message="pre_deploy_backup_failed: {detail}"</c>.
    /// </summary>
    Task<PreDeploySnapshotResult> CreateAsync(string domain, string deployId, CancellationToken ct);
}

/// <summary>
/// Outcome of a successful snapshot. Caller writes Path + SizeBytes onto
/// the deploy_runs row (migration 010 columns); Duration is informational
/// for logging / metrics.
/// </summary>
public sealed record PreDeploySnapshotResult(
    string Path,
    long SizeBytes,
    TimeSpan Duration);
