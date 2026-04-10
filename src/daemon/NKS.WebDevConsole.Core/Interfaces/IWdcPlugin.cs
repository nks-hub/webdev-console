using Microsoft.Extensions.DependencyInjection;

namespace NKS.WebDevConsole.Core.Interfaces;

public interface IWdcPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }
    void Initialize(IServiceCollection services, IPluginContext context);
    Task StartAsync(IPluginContext context, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
