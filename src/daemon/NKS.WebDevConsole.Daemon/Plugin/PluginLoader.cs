using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Plugin;

public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}

public class PluginLoader
{
    private readonly ILogger<PluginLoader> _logger;
    private readonly List<LoadedPlugin> _plugins = [];

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public PluginLoader(ILogger<PluginLoader> logger)
    {
        _logger = logger;
    }

    public void LoadPlugins(string pluginsDirectory)
    {
        if (!Directory.Exists(pluginsDirectory))
        {
            _logger.LogWarning("Plugins directory not found: {Dir}", pluginsDirectory);
            return;
        }

        foreach (var dllPath in Directory.GetFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var pluginName = Path.GetFileNameWithoutExtension(dllPath);

            try
            {
                var context = new PluginLoadContext(dllPath);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IWdcPlugin).IsAssignableFrom(t) && !t.IsAbstract);

                foreach (var type in pluginTypes)
                {
                    if (Activator.CreateInstance(type) is IWdcPlugin plugin)
                    {
                        _plugins.Add(new LoadedPlugin(plugin, assembly, context));
                        _logger.LogInformation("Loaded plugin: {Id} v{Version}", plugin.Id, plugin.Version);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin from {Path}", dllPath);
            }
        }
    }
}

public record LoadedPlugin(IWdcPlugin Instance, Assembly Assembly, AssemblyLoadContext Context);
