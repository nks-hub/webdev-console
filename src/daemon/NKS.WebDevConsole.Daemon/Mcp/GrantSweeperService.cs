using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Phase 7.5+++ — garbage-collects rows in <c>mcp_session_grants</c>.
/// A grant is sweepable when:
///   - it expired (expires_at &lt; now) AND was never revoked, AND the
///     expiry passed more than <see cref="ExpiredRetention"/> ago, OR
///   - it was explicitly revoked more than <see cref="RevokedRetention"/>
///     ago (audit tail kept for 30 days).
///
/// Permanent grants (expires_at IS NULL) are never swept by the
/// expired branch — they only get removed once the operator revokes
/// them and the 30-day audit window passes.
///
/// The repository's read query already filters expired/revoked rows
/// out of the active set, so sweeping them is purely a cleanup task —
/// it never changes runtime auth behaviour. Failure is non-fatal
/// (next tick retries); table-not-existing is tolerated for fresh
/// DBs that haven't yet run migration 012.
/// </summary>
public sealed class GrantSweeperService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ExpiredRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan RevokedRetention = TimeSpan.FromDays(30);

    private readonly Database _db;
    private readonly ILogger<GrantSweeperService> _logger;

    public GrantSweeperService(Database db, ILogger<GrantSweeperService> logger)
    {
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger boot a bit longer than IntentSweeperService so the two
        // janitors don't compete for the SQLite write lock on startup.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
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
                _logger.LogDebug(ex,
                    "Grant sweeper iteration failed (next tick will retry)");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        var rows = await SweepAsync(conn, DateTimeOffset.UtcNow,
            ExpiredRetention, RevokedRetention, ct);

        if (rows > 0)
        {
            _logger.LogInformation(
                "Grant sweeper deleted {Rows} stale mcp_session_grants row(s)", rows);
        }
    }

    /// <summary>
    /// Pure SQL half of the sweep — exposed as <c>internal static</c> so
    /// unit tests can pass a real SQLite connection + synthetic
    /// <paramref name="now"/> and assert row deletion semantics without
    /// spinning the full BackgroundService timer.
    ///
    /// Returns the number of rows deleted. Tolerates missing table
    /// (returns 0) so a fresh DB before migration 012 ran is a no-op.
    /// </summary>
    internal static async Task<int> SweepAsync(
        System.Data.Common.DbConnection conn,
        DateTimeOffset now,
        TimeSpan expiredRetention,
        TimeSpan revokedRetention,
        CancellationToken ct = default)
    {
        var expiredCutoff = now - expiredRetention;
        var revokedCutoff = now - revokedRetention;
        try
        {
            return await conn.ExecuteAsync(
                "DELETE FROM mcp_session_grants WHERE " +
                "(revoked_at IS NULL AND expires_at IS NOT NULL AND expires_at < @ExpiredCutoff) OR " +
                "(revoked_at IS NOT NULL AND revoked_at < @RevokedCutoff)",
                new
                {
                    ExpiredCutoff = expiredCutoff.ToString("o"),
                    RevokedCutoff = revokedCutoff.ToString("o"),
                });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // SQLITE_ERROR (1) — table doesn't exist yet (fresh DB before
            // migration 012). Treat as no-op rather than crash the timer.
            return 0;
        }
    }
}
