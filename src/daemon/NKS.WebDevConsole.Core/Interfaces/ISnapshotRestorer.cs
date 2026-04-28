namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Phase 6.4 — restore a previously taken pre-deploy snapshot. Symmetric
/// to <see cref="IPreDeploySnapshotter"/>. Cross-ALC interface so the
/// plugin's REST + MCP layers can ask the daemon for a restore without
/// reaching into Daemon-internal types.
///
/// Restore is OPERATOR-DRIVEN — never auto-triggered on rollback. The
/// asymmetry is deliberate: rollback rewinds code (idempotent, reversible
/// by re-deploying), but DB restore overwrites live data (irreversible).
/// We refuse to make that decision automatically.
///
/// Resolution flow:
///   1. Read deploy_runs row by deployId, get pre_deploy_backup_path.
///   2. Detect archive type from header bytes (SQLite file vs SQL text).
///   3. Re-resolve target site's .env to know where to restore TO
///      (operator may have changed credentials between snapshot and now).
///   4. Run mysql/psql/file-copy to apply.
///
/// Refuses cleanly when:
///   - The deploy_runs row has no backup path (snapshot wasn't taken)
///   - The archive header marks it as SCAFFOLD (no real dump produced)
///   - The .env DB type doesn't match the archive type
/// </summary>
public interface ISnapshotRestorer
{
    /// <summary>
    /// Restore the snapshot recorded on <paramref name="deployId"/>'s
    /// deploy_runs row into the live database for <paramref name="domain"/>.
    /// Throws on every failure mode — no silent partial restores.
    /// </summary>
    Task<SnapshotRestoreResult> RestoreAsync(string domain, string deployId, CancellationToken ct);
}

/// <summary>
/// Outcome of a successful restore. <see cref="BytesProcessed"/> is the
/// archive size (post-decompression where applicable) for telemetry;
/// <see cref="Duration"/> includes archive decode + DB apply.
/// </summary>
public sealed record SnapshotRestoreResult(
    string Mode,        // "sqlite" | "mysql" | "pgsql"
    long BytesProcessed,
    TimeSpan Duration);
