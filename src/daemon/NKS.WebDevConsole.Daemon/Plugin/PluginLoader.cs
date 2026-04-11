using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
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

                // Load plugin.json manifest next to the DLL (if present) so we
                // can surface its description/author/license in /api/plugins
                // without requiring every plugin to override the IWdcPlugin
                // Description property. The plugin.json stays authoritative
                // for metadata — code just owns behavior.
                var manifest = TryLoadManifest(Path.GetDirectoryName(dllPath)!, pluginName);

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
                        _plugins.Add(new LoadedPlugin(plugin, assembly, context, manifest));
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

/// <summary>
/// Parsed <c>plugin.json</c> manifest sitting next to each plugin DLL. Nullable
/// fields so missing keys degrade gracefully — plugins that predate a given
/// metadata field still load.
/// </summary>
public sealed class PluginManifestData
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? License { get; set; }
    public string[]? SupportedPlatforms { get; set; }
    public string[]? Capabilities { get; set; }
    public int[]? DefaultPorts { get; set; }
}

public record LoadedPlugin(
    IWdcPlugin Instance,
    Assembly Assembly,
    AssemblyLoadContext Context,
    PluginManifestData? Manifest = null);

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

    /// <summary>
    /// Attempts to locate and parse <c>plugin.json</c> for a plugin DLL. We
    /// look in two spots: (1) next to the DLL itself (the normal build
    /// output layout) and (2) two levels up under <c>src/plugins/{name}/</c>
    /// which is where the repo keeps the source manifest so dev mode picks
    /// it up even before the first Release build copies it. Failures are
    /// swallowed — a missing/corrupt manifest must never prevent the plugin
    /// from loading.
    /// </summary>
    private PluginManifestData? TryLoadManifest(string dllDir, string assemblyName)
    {
        string[] candidates =
        [
            Path.Combine(dllDir, "plugin.json"),
            // From build/plugins/*.dll walk back to src/plugins/{name}/plugin.json
            Path.Combine(dllDir, "..", "..", "src", "plugins", assemblyName, "plugin.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "plugins", assemblyName, "plugin.json"),
        ];

        foreach (var path in candidates)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (!File.Exists(full)) continue;
                var json = File.ReadAllText(full);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<PluginManifestData>(json, opts);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read plugin manifest at {Path}", path);
            }
        }
        return null;
    }
}
