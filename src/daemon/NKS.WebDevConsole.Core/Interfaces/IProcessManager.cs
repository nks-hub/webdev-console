using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Manages service module lifecycles. Discovers registered IServiceModule instances
/// and provides start/stop/restart orchestration.
/// Concrete implementation will be provided by the Daemon project.
/// </summary>
public interface IProcessManager
{
    Task<IReadOnlyList<IServiceModule>> GetModulesAsync(CancellationToken ct);
    Task StartServiceAsync(string serviceId, CancellationToken ct);
    Task StopServiceAsync(string serviceId, CancellationToken ct);
    Task RestartServiceAsync(string serviceId, CancellationToken ct);
    Task<ServiceStatus> GetServiceStatusAsync(string serviceId, CancellationToken ct);
}
