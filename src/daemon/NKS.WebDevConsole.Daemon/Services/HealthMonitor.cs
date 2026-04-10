using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Services;

public class HealthMonitor : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly SseService _sse;
    private readonly ILogger<HealthMonitor> _logger;

    public HealthMonitor(IServiceProvider sp, SseService sse, ILogger<HealthMonitor> logger)
    {
        _sp = sp;
        _sse = sse;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait for plugins to initialize
        await Task.Delay(3000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var modules = _sp.GetServices<IServiceModule>();
                foreach (var module in modules)
                {
                    var status = await module.GetStatusAsync(ct);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Health check cycle failed");
            }

            await Task.Delay(5000, ct);
        }
    }
}
