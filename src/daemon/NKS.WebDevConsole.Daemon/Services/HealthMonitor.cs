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
    // Last observed state per service. Plugin-managed services (Apache, MySQL,
    // PHP…) run their own processes rather than going through ProcessManager,
    // so ProcessManager's "service" event never fired for them: a crash or an
    // external stop only showed up on the UI's next 5 s poll, and only if the
    // window happened to be polling. A state change seen here is broadcast as
    // "service:state" so the UI refreshes immediately.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServiceState> _lastState = new();

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
                    var previous = _lastState.TryGetValue(status.Id, out var prev) ? prev : (ServiceState?)null;
                    _lastState[status.Id] = status.State;
                    if (previous is not null && previous != status.State)
                    {
                        await _sse.BroadcastAsync("service:state", new
                        {
                            serviceId = status.Id,
                            state = status.State.ToString().ToLowerInvariant(),
                            previous = previous.Value.ToString().ToLowerInvariant(),
                            pid = status.Pid,
                        });
                    }
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
