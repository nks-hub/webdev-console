using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Plugin;

namespace NKS.WebDevConsole.Daemon.Services;

public sealed class ShutdownCoordinator
{
    private readonly ILogger<ShutdownCoordinator> _logger;
    private readonly TimeSpan _stopTimeout;

    public ShutdownCoordinator(ILogger<ShutdownCoordinator> logger, TimeSpan? stopTimeout = null)
    {
        _logger = logger;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task StopAllAsync(
        IEnumerable<IServiceModule> modules,
        IEnumerable<LoadedPlugin> plugins,
        string portFile,
        CancellationToken ct = default)
    {
        Console.WriteLine("[shutdown] Stopping all services...");

        // Fan out stops concurrently — each module's StopAsync terminates
        // its own process and doesn't depend on others' live state (ports
        // get released on process exit regardless of order). Sequential
        // shutdown used to make a daemon restart wait N × per-module
        // stop-timeout in the worst case; with 8 modules and a 15 s cap
        // that was up to 2 minutes of dead time on an unhealthy tick.
        // Per-task try/catch preserves the "one bad module doesn't wedge
        // the rest" guarantee we had in the sequential foreach.
        var moduleStops = modules.Select(async module =>
        {
            try
            {
                var status = await module.GetStatusAsync(ct);
                if (status.State == ServiceState.Running)
                {
                    using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stopCts.CancelAfter(_stopTimeout);
                    await module.StopAsync(stopCts.Token).WaitAsync(_stopTimeout, ct);
                    Console.WriteLine($"[shutdown] {module.ServiceId}: stopped");
                }
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Timed out stopping service {ServiceId} during shutdown", module.ServiceId);
                Console.WriteLine($"[shutdown] {module.ServiceId}: timed out");
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Timed out stopping service {ServiceId} during shutdown", module.ServiceId);
                Console.WriteLine($"[shutdown] {module.ServiceId}: timed out");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop service {ServiceId} during shutdown", module.ServiceId);
                Console.WriteLine($"[shutdown] {module.ServiceId}: {ex.Message}");
            }
        });
        await Task.WhenAll(moduleStops);

        var pluginStops = plugins.Select(async plugin =>
        {
            try
            {
                using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stopCts.CancelAfter(_stopTimeout);
                await plugin.Instance.StopAsync(stopCts.Token).WaitAsync(_stopTimeout, ct);
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Timed out stopping plugin {PluginId} during shutdown", plugin.Instance.Id);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Timed out stopping plugin {PluginId} during shutdown", plugin.Instance.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop plugin {PluginId} during shutdown", plugin.Instance.Id);
            }
        });
        await Task.WhenAll(pluginStops);

        try { File.Delete(portFile); } catch { }
        Console.WriteLine("[shutdown] Complete");
    }
}
