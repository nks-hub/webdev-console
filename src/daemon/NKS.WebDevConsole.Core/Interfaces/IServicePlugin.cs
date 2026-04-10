using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Core.Interfaces;

public interface IServicePlugin : IPluginModule
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task RestartAsync(CancellationToken ct);
    ServiceStatus GetStatus();
}
