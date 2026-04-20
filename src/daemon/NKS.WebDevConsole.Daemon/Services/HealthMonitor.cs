using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Services;

public sealed class HealthMonitor : BackgroundService
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
            // Skip the per-module status probe when nobody is listening.
            // GetStatusAsync often queries OS handles (ProcessManager,
            // Apache/MySQL PID lookups), so avoiding the 5 s churn while
            // the frontend is closed keeps idle daemon CPU near zero.
            // HealthMonitor tick cost is the dominant background term on
            // an otherwise-quiet install — the Electron window being shut
            // is the common case.
            if (_sse.ClientCount == 0)
            {
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            IEnumerable<IServiceModule> modules;
            try
            {
                modules = _sp.GetServices<IServiceModule>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check could not enumerate modules");
                await Task.Delay(5000, ct);
                continue;
            }

            // Fan out the status probes concurrently — one slow plugin no
            // longer extends the tick for the rest of the 10+ built-ins.
            // Per-module try/catch stays inside the Select lambda so a
            // faulting module doesn't poison Task.WhenAll.
            var probes = modules.Select(async module =>
            {
                try
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
                catch (OperationCanceledException) { /* tick cancelled */ }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health check failed for module {ServiceId}", module.ServiceId);
                }
            });
            await Task.WhenAll(probes);

            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
