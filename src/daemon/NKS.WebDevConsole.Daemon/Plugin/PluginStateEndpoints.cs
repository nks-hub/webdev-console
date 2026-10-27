using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using Dapper;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;
using NKS.WebDevConsole.Daemon.Cloudflare;
using NKS.WebDevConsole.Daemon.Ssl;
using NKS.WebDevConsole.Daemon.Hosts;
using NKS.WebDevConsole.Daemon.Deploy;
using CliWrap.Buffered;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Plugin;

/// <summary>
/// Plugin enable/disable.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class PluginStateEndpoints
{
    public static void MapPluginStateEndpoints(this WebApplication app, PluginLoader pluginLoader)
    {
        // Plugin enable/disable — persists to ~/.wdc/data/plugin-state.json via PluginState.
        // Plugins remain loaded either way; disabled plugins are hidden from the UI service
        // list and skipped by Start All.
        app.MapPost("/api/plugins/{id}/enable", (string id, PluginState pluginState, ILoggerFactory lf) =>
        {
            var pluginLogger = lf.CreateLogger("PluginToggle");
            var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
            if (plugin == null)
            {
                pluginLogger.LogWarning("Enable refused: plugin {PluginId} not loaded", id);
                return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });
            }
            // F91.2: enforce dependency graph on enable. Build the set of currently-
            // enabled plugins (excluding this one, since we're about to toggle it)
            // and refuse the enable if any hard or any-of dep is unsatisfied. Keeps
            // users from putting the system into a state where e.g. PHP is enabled
            // but no web-server plugin is running to host PHP-FPM. The dependency
            // diagnostics surface in the response so the UI can show "can't enable
            // PHP: at least one of [apache, nginx, caddy] must be enabled".
            var enabledIds = new HashSet<string>(
                pluginLoader.Plugins
                    .Where(pl => pluginState.IsEnabled(pl.Instance.Id))
                    .Select(pl => pl.Instance.Id),
                StringComparer.OrdinalIgnoreCase);
            var missing = PluginLoaderInternals.ValidateDependencies(
                plugin.Manifest?.Dependencies, enabledIds);
            if (missing.Count > 0)
            {
                pluginLogger.LogWarning("Enable refused for {PluginId}: missing deps [{Missing}]", id, string.Join(",", missing));
                return Results.BadRequest(new
                {
                    error = "dependencies-not-satisfied",
                    id,
                    missingDependencies = missing,
                });
            }
            pluginState.SetEnabled(id, true);
            pluginLogger.LogInformation("Plugin {PluginId} enabled", id);
            return Results.Ok(new { id, enabled = true });
        });

        app.MapPost("/api/plugins/{id}/disable", (string id, PluginState pluginState, ILoggerFactory lf) =>
        {
            var pluginLogger = lf.CreateLogger("PluginToggle");
            var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
            if (plugin == null)
            {
                pluginLogger.LogWarning("Disable refused: plugin {PluginId} not loaded", id);
                return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });
            }
            // F91.2: refuse disable when another enabled plugin would lose a
            // satisfied dependency. E.g. Composer hard-depends on PHP — disabling
            // PHP while Composer is on would orphan Composer. List the dependents
            // so the UI can guide the user to disable them first.
            var dependents = new List<string>();
            foreach (var other in pluginLoader.Plugins)
            {
                if (other.Instance.Id == id) continue;
                if (!pluginState.IsEnabled(other.Instance.Id)) continue;
                var deps = other.Manifest?.Dependencies;
                if (deps is null) continue;
                var hardHit = deps.Hard?.Any(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase)) == true;
                var anyOfHit = deps.AnyOf?.Any(group =>
                    group?.Any(g => string.Equals(g, id, StringComparison.OrdinalIgnoreCase)) == true
                    // AnyOf only breaks when NO other candidate from the group is enabled.
                    && !group.Any(g =>
                        !string.Equals(g, id, StringComparison.OrdinalIgnoreCase)
                        && pluginState.IsEnabled(g))) == true;
                if (hardHit || anyOfHit) dependents.Add(other.Instance.Id);
            }
            if (dependents.Count > 0)
            {
                pluginLogger.LogWarning("Disable refused for {PluginId}: active dependents [{Dependents}]", id, string.Join(",", dependents));
                return Results.BadRequest(new
                {
                    error = "has-dependents",
                    id,
                    dependents,
                });
            }
            pluginState.SetEnabled(id, false);
            pluginLogger.LogInformation("Plugin {PluginId} disabled", id);
            return Results.Ok(new { id, enabled = false });
        });
    }
}
