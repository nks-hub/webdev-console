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
    // Shared across every plugin.json read — JsonSerializer caches type
    // contracts per options reference, so a fresh one per plugin fragments
    // the cache. Case-insensitive matches the leniency the daemon's other
    // body deserializers already apply.
    private static readonly JsonSerializerOptions ManifestJson =
        new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<PluginLoader> _logger;
    private readonly List<LoadedPlugin> _plugins = [];

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>
    /// Calls IWdcPlugin.RegisterEndpoints on every loaded plugin and wires the
    /// returned endpoints into the host routing pipeline under /api/{pluginId}/.
    /// Existing Bearer auth middleware (matching /api/* prefix) covers them
    /// without per-endpoint .RequireAuthorization() calls. Idempotent — safe
    /// to call once after app.Build() and before app.RunAsync(). Plugins that
    /// throw during RegisterEndpoints are logged and skipped; one bad plugin
    /// never blocks the others.
    /// </summary>
    public void WireEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app)
    {
        // Snapshot the host-side route table BEFORE running any plugin's
        // RegisterEndpoints. Used to filter out endpoint registrations whose
        // (METHOD, path-template) already exist on the host — without this
        // a plugin claiming a path that the daemon's Program.cs already
        // serves causes an AmbiguousMatchException at the first request,
        // breaking BOTH the plugin and the host handler. The most important
        // case during the nksdeploy plugin migration: the daemon currently
        // owns /api/nks.wdc.deploy/* routes inline, and the plugin (in the
        // sibling webdev-console-plugins repo) registers overlapping paths.
        // Phase #109 will move ownership to the plugin; until then, the
        // host wins and the plugin's clashing endpoints are skipped with a
        // visible log so operators can see the migration is in flight.
        // Route templates that differ only by parameter NAME (e.g. host's
        // `{snapshotId}` vs plugin's `{deployId}` for the same URL shape)
        // would slip through a raw-text comparison and cause both routes
        // to register — ASP.NET then resolves ambiguously to whichever
        // it picks, breaking the host-wins guarantee we rely on for
        // incremental plugin migration. Canonicalize by replacing every
        // `{...}` segment with `{*}` so two patterns that match the same
        // URL set hash to the same key regardless of parameter names.
        static string Canonicalize(string raw) =>
            System.Text.RegularExpressions.Regex.Replace(raw, @"\{[^{}]*\}", "{*}");
        var existing = new HashSet<(string Method, string Path)>(
            app.DataSources
               .SelectMany(ds => ds.Endpoints)
               .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
               .SelectMany(re =>
                   (re.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()
                       ?.HttpMethods ?? Array.Empty<string>())
                   .Select(method => (method.ToUpperInvariant(), Canonicalize(re.RoutePattern.RawText ?? string.Empty)))));

        foreach (var loaded in _plugins)
        {
            var pluginId = loaded.Manifest?.Id ?? loaded.Instance.Id;
            try
            {
                var reg = new NKS.WebDevConsole.Core.Interfaces.EndpointRegistration(pluginId);
                loaded.Instance.RegisterEndpoints(reg);
                var skipped = 0;
                foreach (var ep in reg.Endpoints)
                {
                    // Canonicalize the plugin's path the same way the
                    // host snapshot was, so parameter-name differences
                    // ({deployId} vs {snapshotId}) don't slip through.
                    var key = (ep.Method.ToUpperInvariant(), Canonicalize(ep.Path));
                    if (existing.Contains(key))
                    {
                        // Host already serves this route — skip silently in
                        // production, log at warning so operators see the
                        // overlap during incremental plugin migrations.
                        _logger.LogWarning(
                            "Plugin {Id} endpoint {Method} {Path} skipped — host already owns this route",
                            pluginId, ep.Method, ep.Path);
                        skipped++;
                        continue;
                    }
                    app.MapMethods(ep.Path, [ep.Method], ep.Handler)
                       .WithName($"{pluginId}:{ep.Method}:{ep.Path}");
                    // Track newly-added (canonicalized) so a SECOND
                    // plugin claiming the same route doesn't double-
                    // register either.
                    existing.Add(key);
                }
                var registered = reg.Endpoints.Count - skipped;
                if (registered > 0)
                    _logger.LogInformation("Plugin {Id} registered {Count} endpoint(s){Skipped}",
                        pluginId, registered,
                        skipped > 0 ? $" ({skipped} skipped due to host conflict)" : "");
                else if (skipped > 0)
                    _logger.LogInformation("Plugin {Id} declared {Count} endpoint(s) but all are owned by the host — none wired",
                        pluginId, skipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Id} RegisterEndpoints failed; skipping", pluginId);
            }
        }
    }

    public PluginLoader(ILogger<PluginLoader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// F91.9: delete a plugin's DLL + its sibling artifacts (pdb, deps.json,
    /// runtimeconfig.json, xml doc). Best-effort — any still-locked file is
    /// skipped and logged. Called at load time for blacklisted plugins so
    /// the DLL doesn't linger across reboots.
    /// </summary>
    private void TryPurgePluginFiles(string dllPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(dllPath);
            var baseName = Path.GetFileNameWithoutExtension(dllPath);
            if (dir is null || string.IsNullOrEmpty(baseName)) return;
            foreach (var f in Directory.EnumerateFiles(dir, baseName + ".*"))
            {
                try { File.Delete(f); }
                catch (Exception ex) { _logger.LogDebug("Could not delete {File}: {Msg}", f, ex.Message); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Purge failed for {Dll}", dllPath);
        }
    }

    public void LoadPlugins(string pluginsDirectory, NKS.WebDevConsole.Daemon.Services.PluginState? pluginState = null)
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

            // F91.9: peek at plugin.json BEFORE touching the DLL. If the
            // declared id is on the uninstalled blacklist, skip the load
            // entirely and purge lingering files (File.Delete succeeds now
            // that no ALC holds them).
            var preManifest = TryLoadManifest(Path.GetDirectoryName(dllPath)!, pluginName);
            var preId = preManifest?.Id;
            if (pluginState is not null && !string.IsNullOrWhiteSpace(preId)
                && pluginState.IsUninstalled(preId))
            {
                _logger.LogInformation("Plugin {Id} is uninstalled — skipping load and purging files", preId);
                TryPurgePluginFiles(dllPath);
                pluginState.ClearUninstalled(preId);
                continue;
            }

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
                var manifest = preManifest ?? TryLoadManifest(Path.GetDirectoryName(dllPath)!, pluginName);

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
                        // F91.9 safety net: if plugin.json was missing, the
                        // blacklist check above was skipped. Re-evaluate with
                        // the runtime-declared id now that we have it.
                        if (pluginState is not null && pluginState.IsUninstalled(plugin.Id))
                        {
                            _logger.LogInformation("Plugin {Id} is uninstalled (discovered via code) — skipping", plugin.Id);
                            // We can't purge right now because the DLL is loaded in `context`.
                            // Leave the id in the blacklist so the next boot cleans it up.
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
    // F87: plugin dependency graph. A plugin may declare hard dependencies
    // (MUST be enabled before this plugin can start) and any-of choices
    // (at least one of the listed plugins MUST be enabled — e.g. PHP
    // requires Apache OR Nginx OR Caddy as a web server host).
    // Optional: missing fields keep current permissive behaviour.
    public PluginDependencies? Dependencies { get; set; }
    // Task 25b: optional URL to a UMD/ESM bundle the frontend lazy-loads
    // when the user navigates to this plugin's custom page. Null when the
    // plugin ships no custom UI (standard schema-rendered settings only).
    public string? PageBundleUrl { get; set; }
    // Task 25b: static port defaults declared in plugin.json. Secondary data
    // path — runtime DI registrations (IPortMetadata) are primary. Loader
    // parses and exposes without validation beyond shape.
    public ManifestPortEntry[]? Ports { get; set; }
}

/// <summary>
/// Single entry in the <c>ports</c> array of plugin.json.
/// </summary>
public sealed class ManifestPortEntry
{
    public string? Key { get; set; }
    public string? Label { get; set; }
    public int Default { get; set; }
}

public sealed class PluginDependencies
{
    /// <summary>Plugin IDs that MUST be enabled before this plugin starts.</summary>
    public string[]? Hard { get; set; }

    /// <summary>Groups where at least one member MUST be enabled. Each inner
    /// array is an OR set — any one of its plugin IDs satisfies the group.</summary>
    public string[][]? AnyOf { get; set; }
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

    /// <summary>
    /// F87: evaluate a plugin's dependency manifest against the set of
    /// currently loaded plugin IDs. Returns empty list when all deps
    /// satisfied, otherwise a list of human-readable diagnostic strings
    /// naming each missing hard dep + each unsatisfied any-of group.
    /// Callers (PluginLoader + /api/plugins enable endpoint) surface
    /// the list to the user instead of silently failing / hanging.
    /// </summary>
    public static IReadOnlyList<string> ValidateDependencies(
        PluginDependencies? deps,
        System.Collections.Generic.ISet<string> availablePluginIds)
    {
        if (deps is null) return Array.Empty<string>();
        var missing = new List<string>();
        foreach (var hard in deps.Hard ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(hard)) continue;
            if (!availablePluginIds.Contains(hard))
                missing.Add($"required plugin '{hard}' is not available");
        }
        foreach (var group in deps.AnyOf ?? Array.Empty<string[]>())
        {
            if (group is null || group.Length == 0) continue;
            var anySatisfied = false;
            foreach (var candidate in group)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && availablePluginIds.Contains(candidate))
                {
                    anySatisfied = true;
                    break;
                }
            }
            if (!anySatisfied)
                missing.Add($"at least one of [{string.Join(", ", group)}] must be available");
        }
        return missing;
    }
}

public partial class PluginLoader
{
    private static bool IsSemVer(string? version) => PluginLoaderInternals.IsSemVer(version);

    /// <summary>
    /// Attempts to locate and parse <c>plugin.json</c> for a plugin DLL. The
    /// normal path is next to the DLL itself (the plugin project's build
    /// output / stage-plugins drop). We also try the sibling
    /// <c>webdev-console-plugins</c> checkout a few levels up so a
    /// monorepo-local dev run picks up a freshly-edited manifest before
    /// the sibling plugin is even built. Failures are swallowed — a
    /// missing/corrupt manifest must never prevent the plugin from
    /// loading.
    /// </summary>
    private PluginManifestData? TryLoadManifest(string dllDir, string assemblyName)
    {
        string[] candidates =
        [
            Path.Combine(dllDir, "plugin.json"),
            // Sibling plugins-repo checkout: <repo>/../webdev-console-plugins/{name}/plugin.json
            Path.Combine(dllDir, "..", "..", "..", "..", "..", "..",
                "webdev-console-plugins", assemblyName, "plugin.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
                "webdev-console-plugins", assemblyName, "plugin.json"),
        ];

        foreach (var path in candidates)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (!File.Exists(full)) continue;
                var json = File.ReadAllText(full);
                return JsonSerializer.Deserialize<PluginManifestData>(json, ManifestJson);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read plugin manifest at {Path}", path);
            }
        }
        return null;
    }
}
