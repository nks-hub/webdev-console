using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.SDK;

public abstract class PluginBase : IPluginModule
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Version { get; }
    public virtual Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
}

public abstract class ServicePluginBase : PluginBase, IServicePlugin
{
    public abstract Task StartAsync(CancellationToken ct);
    public abstract Task StopAsync(CancellationToken ct);
    public virtual async Task RestartAsync(CancellationToken ct)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }
    public abstract ServiceStatus GetStatus();
}
