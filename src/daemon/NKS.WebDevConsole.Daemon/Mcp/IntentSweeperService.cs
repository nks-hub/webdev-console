using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
/// Failure is non-fatal — the next tick retries. The sweep tolerates the
/// table not yet existing (fresh DB before migration 006/007 ran).
/// </summary>
public sealed class IntentSweeperService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UsedRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan ExpiredRetention = TimeSpan.FromDays(1);

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
    }
}
