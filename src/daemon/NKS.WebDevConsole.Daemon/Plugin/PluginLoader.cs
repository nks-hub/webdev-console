using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Plugin;

public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private static readonly HashSet<string> SharedAssemblies = [
        "NKS.WebDevConsole.Core",
        "NKS.WebDevConsole.Plugin.SDK",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
    ];

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared assemblies must come from the host context to preserve type identity
        if (SharedAssemblies.Contains(assemblyName.Name!))
            return null; // fall back to Default context

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}

public partial class PluginLoader
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

        var pluginDlls = Directory.GetFiles(pluginsDirectory, "NKS.WebDevConsole.Plugin.*.dll")
            .Where(f => !f.EndsWith("Plugin.SDK.dll", StringComparison.OrdinalIgnoreCase));

        _logger.LogInformation("Scanning {Dir} — found {Count} plugin candidates", pluginsDirectory, pluginDlls.Count());

        foreach (var dllPath in pluginDlls)
        {
            var pluginName = Path.GetFileNameWithoutExtension(dllPath);
            _logger.LogDebug("Loading {Plugin}...", pluginName);

            try
            {
                var context = new PluginLoadContext(dllPath);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IWdcPlugin).IsAssignableFrom(t) && !t.IsAbstract)
                    .ToList();

                if (pluginTypes.Count == 0)
                {
                    _logger.LogDebug("No IWdcPlugin found in {Plugin}", pluginName);
                    continue;
                }

                foreach (var type in pluginTypes)
                {
                    var plugin = Activator.CreateInstance(type) as IWdcPlugin;
                    if (plugin != null)
                    {
                        // Sanity-check plugin identity so a buggy plugin can't corrupt
                        // the loaded list or the marketplace installed-id set.
                        if (string.IsNullOrWhiteSpace(plugin.Id))
                        {
                            _logger.LogWarning("Plugin {Type} has empty Id — skipping", type.Name);
                            continue;
                        }
                        // SemVer check on the plugin's declared version. Non-fatal:
                        // non-SemVer versions still load so existing plugins keep
                        // working, but the warning surfaces the issue so plugin
                        // authors can migrate to proper SemVer for marketplace
                        // compatibility (the marketplace UI assumes SemVer for
                        // update-available comparisons).
                        if (!IsSemVer(plugin.Version))
                        {
                            _logger.LogWarning(
                                "Plugin {Id} declares non-SemVer version '{Version}' — marketplace update detection may not work",
                                plugin.Id, plugin.Version);
                        }
                        _plugins.Add(new LoadedPlugin(plugin, assembly, context));
                        _logger.LogInformation("Loaded plugin: {Id} v{Version} ({Type})", plugin.Id, plugin.Version, type.Name);
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                _logger.LogError("Failed to load types from {Plugin}: {Errors}", pluginName,
                    string.Join("; ", ex.LoaderExceptions?.Select(e => e?.Message) ?? []));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin {Plugin}", pluginName);
            }
        }
    }
}

public record LoadedPlugin(IWdcPlugin Instance, Assembly Assembly, AssemblyLoadContext Context);

internal static class PluginLoaderInternals
{
    // Intentionally permissive subset of SemVer 2.0.0:
    //   MAJOR.MINOR.PATCH, each 1-4 digits, optional -prerelease with
    //   alphanumerics/dots/hyphens, optional +build metadata. Matches
    //   the standard cases (1.0.0, 1.2.3-beta.1, 2.0.0+build.42) while
    //   rejecting obvious junk ("dev", "unknown", "latest").
    private static readonly System.Text.RegularExpressions.Regex SemVerRegex =
        new(@"^\d{1,4}\.\d{1,4}\.\d{1,4}(-[A-Za-z0-9.-]+)?(\+[A-Za-z0-9.-]+)?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsSemVer(string? version) =>
        !string.IsNullOrWhiteSpace(version) && SemVerRegex.IsMatch(version);
}

public partial class PluginLoader
{
    private static bool IsSemVer(string? version) => PluginLoaderInternals.IsSemVer(version);
}
