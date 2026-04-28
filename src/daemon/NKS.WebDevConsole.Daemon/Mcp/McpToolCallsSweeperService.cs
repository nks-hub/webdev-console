using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Phase 8 — periodic retention sweep for the <c>mcp_tool_calls</c> audit
/// table. The table grows by every read AI assistants make, so without
/// pruning it accumulates indefinitely. Every hour we delete rows older
/// than the retention window (default 30 days).
///
/// Failure is non-fatal — the next tick retries. The sweep tolerates the
/// table not yet existing (fresh DB before migration 017 ran).
/// </summary>
public sealed class McpToolCallsSweeperService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private const int DefaultRetentionDays = 30;

    private readonly McpToolCallsRepository _repo;
    private readonly ILogger<McpToolCallsSweeperService> _logger;

    public McpToolCallsSweeperService(
        McpToolCallsRepository repo,
        ILogger<McpToolCallsSweeperService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial 30s delay so a freshly-started daemon doesn't burn CPU
        // on housekeeping during the boot window.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await _repo.PruneAsync(DefaultRetentionDays, stoppingToken);
                if (deleted > 0)
                    _logger.LogInformation("Pruned {Count} mcp_tool_calls rows older than {Days} days",
                        deleted, DefaultRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mcp_tool_calls sweep failed (will retry next tick)");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
