using Microsoft.Extensions.DependencyInjection;

namespace NKS.WebDevConsole.Core.Interfaces;

public interface IWdcPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }

    /// <summary>
    /// User-facing description shown in the plugin detail pane of the
    /// Settings &gt; Plugins page. Default implementation returns an empty
    /// string — the <c>/api/plugins</c> endpoint falls back to reading
    /// <c>plugin.json</c> next to the DLL when this is empty, so plugins
    /// authored before this property existed keep working.
    /// Override to return a 1–3 sentence summary of what the plugin does,
    /// what binaries it manages, and what settings it needs.
    /// </summary>
    string Description => string.Empty;

    void Initialize(IServiceCollection services, IPluginContext context);
    Task StartAsync(IPluginContext context, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
