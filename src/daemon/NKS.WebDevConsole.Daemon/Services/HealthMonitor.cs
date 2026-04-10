using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Services;

public class HealthMonitor : BackgroundService
{
    private readonly ProcessManager _processManager;
    private readonly SseService _sse;
    private readonly ILogger<HealthMonitor> _logger;

    public HealthMonitor(ProcessManager pm, SseService sse, ILogger<HealthMonitor> logger)
    {
        _processManager = pm;
        _sse = sse;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct);

            foreach (var status in _processManager.GetAllStatuses())
            {
                if (status.State == ServiceState.Running)
                {
                    await _sse.BroadcastAsync("metrics", new
                    {
                        serviceId = status.Id,
                        cpu = status.CpuPercent,
                        memory = status.MemoryBytes,
                        uptime = status.Uptime?.TotalSeconds
                    });
                }
            }
        }
    }
}
