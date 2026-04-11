using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Plugin;

namespace NKS.WebDevConsole.Daemon.Services;

public sealed class ShutdownCoordinator
{
    private readonly ILogger<ShutdownCoordinator> _logger;

    public ShutdownCoordinator(ILogger<ShutdownCoordinator> logger)
    {
        _logger = logger;
    }

    public async Task StopAllAsync(
        IEnumerable<IServiceModule> modules,
        IEnumerable<LoadedPlugin> plugins,
        string portFile,
        CancellationToken ct = default)
    {
        Console.WriteLine("[shutdown] Stopping all services...");

        foreach (var module in modules)
        {
            try
            {
                var status = await module.GetStatusAsync(ct);
                if (status.State == ServiceState.Running)
                {
                    await module.StopAsync(ct);
                    Console.WriteLine($"[shutdown] {module.ServiceId}: stopped");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop service {ServiceId} during shutdown", module.ServiceId);
                Console.WriteLine($"[shutdown] {module.ServiceId}: {ex.Message}");
            }
        }

        foreach (var plugin in plugins)
        {
            try
            {
                await plugin.Instance.StopAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop plugin {PluginId} during shutdown", plugin.Instance.Id);
            }
        }

        try { File.Delete(portFile); } catch { }
        Console.WriteLine("[shutdown] Complete");
    }
}
