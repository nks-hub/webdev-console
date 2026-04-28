using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Garbage-collects rows in the <c>deploy_intents</c> table on a periodic
/// timer. An intent is sweepable when:
///   - it was consumed (used_at IS NOT NULL) more than 7 days ago, or
///   - it expired without being used and the expiry passed more than
///     1 day ago.
///
/// We keep a 7-day audit tail of consumed intents so the history page and
/// any future "who ran what" view can still join against them — beyond
/// that, the row is dead weight (the corresponding deploy_runs row carries
/// the durable record). Unused-and-expired intents get a shorter 1-day
/// grace because they have no audit value at all.
///
/// Phase 6.5b — also sweeps orphaned pre-deploy snapshot files. A
/// <c>{BackupsRoot}/pre-deploy/{deployId}.sql.gz</c> is orphaned when:
///   - the file is older than <see cref="OrphanSnapshotRetention"/>, AND
///   - no <c>deploy_runs</c> row references it via
///     <c>pre_deploy_backup_path</c>.
/// Snapshots NEWER than the retention window are kept regardless of row
/// presence so an in-flight deploy that hasn't yet UPDATEd its row never
/// loses its dump under it.
///
/// Failure is non-fatal — the next tick retries. The sweep tolerates the
/// table not yet existing (fresh DB before migration 006/007 ran).
/// </summary>
public sealed class IntentSweeperService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UsedRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan ExpiredRetention = TimeSpan.FromDays(1);
    /// <summary>
    /// Orphaned snapshot files keep around this long before the sweeper
    /// deletes them — wider window than intent rows because a restored
    /// snapshot might still be useful for forensic reasons. Operators
    /// can manually copy any archive they care about into a permanent
    /// location before the sweep window expires.
    /// </summary>
    private static readonly TimeSpan OrphanSnapshotRetention = TimeSpan.FromDays(30);

    private readonly Database _db;
    private readonly ILogger<IntentSweeperService> _logger;

    public IntentSweeperService(Database db, ILogger<IntentSweeperService> logger)
    {
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a beat after boot so migrations finish + the recovery
        // sweep above isn't fighting us for the SQLite write lock.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Intent sweeper iteration failed (next tick will retry)");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        var usedCutoff = DateTimeOffset.UtcNow - UsedRetention;
        var expiredCutoff = DateTimeOffset.UtcNow - ExpiredRetention;

        // Single statement, two predicates joined by OR — SQLite plans this
        // efficiently with the idx_deploy_intents_expires_at index for the
        // expired branch and a table scan for the used branch (volume is
        // small enough that the scan cost is negligible).
        var rows = await conn.ExecuteAsync(
            "DELETE FROM deploy_intents WHERE " +
            "(used_at IS NOT NULL AND used_at < @UsedCutoff) OR " +
            "(used_at IS NULL AND expires_at < @ExpiredCutoff)",
            new
            {
                UsedCutoff = usedCutoff.ToString("o"),
                ExpiredCutoff = expiredCutoff.ToString("o"),
            });

        if (rows > 0)
        {
            _logger.LogInformation("Intent sweeper deleted {Rows} stale deploy_intents row(s)", rows);
        }

        // Phase 6.5b — orphaned pre-deploy snapshot files.
        await SweepOrphanSnapshotsAsync(conn, ct);
    }

    /// <summary>
    /// Delete <c>{BackupsRoot}/pre-deploy/*.sql.gz</c> files that are older
    /// than <see cref="OrphanSnapshotRetention"/> AND have no matching
    /// <c>deploy_runs.pre_deploy_backup_path</c>. Reads the live path set
    /// in one round-trip so we don't probe the DB per file.
    /// </summary>
    private async Task SweepOrphanSnapshotsAsync(
        System.Data.Common.DbConnection conn,
        CancellationToken ct)
    {
        var dir = Path.Combine(WdcPaths.BackupsRoot, "pre-deploy");
        if (!Directory.Exists(dir)) return;

        var referencedPaths = (await conn.QueryAsync<string>(
            "SELECT pre_deploy_backup_path FROM deploy_runs " +
            "WHERE pre_deploy_backup_path IS NOT NULL"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deleted = SweepOrphanSnapshotFiles(dir, OrphanSnapshotRetention,
            referencedPaths, DateTime.UtcNow, _logger);
        if (deleted > 0)
        {
            _logger.LogInformation(
                "Intent sweeper deleted {Count} orphan pre-deploy snapshot file(s) older than {Days} days",
                deleted, (int)OrphanSnapshotRetention.TotalDays);
        }
    }

    /// <summary>
    /// Pure file-system half of the orphan-snapshot sweep — no DB, no
    /// time source. Exposed as <c>internal static</c> so unit tests can
    /// pass a temp dir + synthetic <paramref name="now"/> + a pre-built
    /// referenced-paths set without spinning the full BackgroundService.
    ///
    /// Returns the count of files actually deleted (skips files newer
    /// than the retention window or referenced by deploy_runs).
    /// </summary>
    internal static int SweepOrphanSnapshotFiles(
        string dir,
        TimeSpan retention,
        HashSet<string> referencedPaths,
        DateTime now,
        ILogger logger)
    {
        if (!Directory.Exists(dir)) return 0;
        var cutoff = now - retention;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "*.sql.gz"))
        {
            var fi = new FileInfo(path);
            if (fi.LastWriteTimeUtc >= cutoff) continue;
            if (referencedPaths.Contains(fi.FullName)) continue;
            try
            {
                fi.Delete();
                deleted++;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Failed to delete orphan snapshot {Path} (will retry next sweep)", fi.FullName);
            }
        }
        return deleted;
    }
}
