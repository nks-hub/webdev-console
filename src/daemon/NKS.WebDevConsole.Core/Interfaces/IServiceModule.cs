using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Core.Interfaces;

public interface IServiceModule
{
    string ServiceId { get; }
    string DisplayName { get; }
    ServiceType Type { get; }
    Task<ValidationResult> ValidateConfigAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task ReloadAsync(CancellationToken ct);
    Task<ServiceStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct);
}
