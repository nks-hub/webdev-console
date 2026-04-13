using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Phase 11 perf monitoring — server-side request-count history.
/// Background poller writes one row per (site, sample-tick) into
/// <c>metrics_history</c> so the SiteEdit metrics tab can render windows
/// longer than the 5-minute client-side ring buffer.
///
/// Sampling cadence: 60 seconds. Cheap — each tick reads N access log
/// metadata calls (no full file scan, just SizeBytes + line count cap)
/// and writes one row per active site. For 50 sites that's ~50 fast IO
/// reads + ~50 SQLite inserts in a transaction, well under 1s.
///
/// Retention: rows older than 7 days are pruned at the end of each tick.
/// At 60-second cadence over 7 days that caps the table at ~10k rows
/// per site — fine for SQLite.
/// </summary>
public sealed class MetricsHistoryService : BackgroundService
{
    private readonly Database _db;
    private readonly SiteManager _siteManager;
    private readonly BinaryManager _binaryManager;
    private readonly ILogger<MetricsHistoryService> _logger;

    private static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);

    public MetricsHistoryService(
        Database db,
        SiteManager siteManager,
        BinaryManager binaryManager,
        ILogger<MetricsHistoryService> logger)
    {
        _db = db;
        _siteManager = siteManager;
        _binaryManager = binaryManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly so plugins finish loading before the first tick.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleOnceAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "metrics-history tick failed (continuing)");
            }

            try { await Task.Delay(SamplePeriod, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private Task SampleOnceAsync()
    {
        var sites = _siteManager.Sites.Values.ToList();
        if (sites.Count == 0) return Task.CompletedTask;

        var apacheRoots = _binaryManager.ListInstalled("apache")
            .Select(a => Path.Combine(a.InstallPath, "logs"))
            .ToList();
        if (apacheRoots.Count == 0) return Task.CompletedTask;

        var nowIso = DateTime.UtcNow.ToString("o");
        var samples = new List<(string Domain, string SampledAt, long RequestCount, long SizeBytes, string? LastWriteUtc)>();
        foreach (var site in sites)
        {
            var candidates = new List<string>();
            foreach (var logsDir in apacheRoots)
            {
                candidates.Add(Path.Combine(logsDir, $"{site.Domain}-access.log"));
                candidates.Add(Path.Combine(logsDir, $"{site.Domain}-ssl-access.log"));
            }
            var stats = AccessLogInspector.Inspect(candidates);
            if (stats is null) continue;
            samples.Add((
                site.Domain,
                nowIso,
                stats.LineCount,
                stats.SizeBytes,
                stats.LastWrittenUtc.ToString("o")
            ));
        }
        if (samples.Count == 0) return Task.CompletedTask;

        // Single connection + transaction for all inserts so one disk I/O
        // failure can't leave the history half-applied.
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var s in samples)
            {
                conn.Execute(
                    "INSERT INTO metrics_history (domain, sampled_at, request_count, size_bytes, last_write_utc) " +
                    "VALUES (@Domain, @SampledAt, @RequestCount, @SizeBytes, @LastWriteUtc)",
                    new
                    {
                        s.Domain,
                        s.SampledAt,
                        s.RequestCount,
                        s.SizeBytes,
                        s.LastWriteUtc,
                    },
                    transaction: tx);
            }

            // Prune rows older than the retention period. Done in the same
            // transaction so a concurrent reader never sees the table mid-prune.
            var cutoff = DateTime.UtcNow.Subtract(RetentionPeriod).ToString("o");
            conn.Execute(
                "DELETE FROM metrics_history WHERE sampled_at < @Cutoff",
                new { Cutoff = cutoff },
                transaction: tx);

            tx.Commit();
            _logger.LogDebug("metrics-history tick wrote {Count} samples", samples.Count);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return Task.CompletedTask;
    }
}
