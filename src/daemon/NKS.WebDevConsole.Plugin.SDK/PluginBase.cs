using Microsoft.Extensions.DependencyInjection;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Plugin.SDK;

/// <summary>
/// Convenience base class for IWdcPlugin implementations.
/// Provides no-op defaults; override only what your plugin needs.
/// </summary>
public abstract class PluginBase : IWdcPlugin
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Version { get; }

    public virtual void Initialize(IServiceCollection services, IPluginContext context) { }
    public virtual Task StartAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;
    public virtual Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
