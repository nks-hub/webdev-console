using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Phase 8 — periodic retention sweep for the <c>mcp_tool_calls</c> audit
/// table. The table grows by every read AI assistants make, so without
/// pruning it accumulates indefinitely. Every hour we delete rows older
/// than the retention window — operator-tunable via the
/// <c>mcp.toolCallRetentionDays</c> setting (default 30, clamped 1-365).
///
/// Failure is non-fatal — the next tick retries. The sweep tolerates the
/// table not yet existing (fresh DB before migration 017 ran).
/// </summary>
public sealed class McpToolCallsSweeperService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private const int DefaultRetentionDays = 30;
    private const int MinRetentionDays = 1;
    private const int MaxRetentionDays = 365;

    private readonly McpToolCallsRepository _repo;
    private readonly SettingsStore _settings;
    private readonly ILogger<McpToolCallsSweeperService> _logger;

    public McpToolCallsSweeperService(
        McpToolCallsRepository repo,
        SettingsStore settings,
        ILogger<McpToolCallsSweeperService> logger)
    {
        _repo = repo;
        _settings = settings;
        _logger = logger;
    }

    private int ResolveRetentionDays()
    {
        var raw = _settings.GetInt("mcp", "toolCallRetentionDays", defaultValue: DefaultRetentionDays);
        return Math.Clamp(raw, MinRetentionDays, MaxRetentionDays);
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
                var retentionDays = ResolveRetentionDays();
                var deleted = await _repo.PruneAsync(retentionDays, stoppingToken);
                if (deleted > 0)
                    _logger.LogInformation("Pruned {Count} mcp_tool_calls rows older than {Days} days",
                        deleted, retentionDays);
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
