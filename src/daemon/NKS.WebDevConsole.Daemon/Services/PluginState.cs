using System.Text.Json;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Tracks user-toggled plugin enabled/disabled state. Plugins are always
/// *loaded* at startup (so their assemblies and UI schemas are available),
/// but a disabled plugin's services are hidden from the UI and skipped by
/// Start All.
///
/// Persisted to <c>~/.wdc/data/plugin-state.json</c> as a simple JSON array
/// of disabled plugin IDs. Absence of the file means "all loaded plugins are
/// enabled" (the default).
/// </summary>
public sealed class PluginState
{
    private static readonly string StateFilePath = Path.Combine(
        WdcPaths.DataRoot, "plugin-state.json");
    // F91.9: persistent "please don't load this plugin again" list.
    // Uninstall writes here because File.Delete on a loaded DLL is a no-op
    // on Windows (ALC holds the handle). Next daemon boot honours the list
    // by skipping Activator.CreateInstance AND retrying the file delete
    // now that the lock is gone. See PluginLoader.LoadPlugins.
    private static readonly string UninstalledFilePath = Path.Combine(
        WdcPaths.DataRoot, "uninstalled-plugins.json");

    // Shared across all Save() calls — JsonSerializer caches type contracts
    // per options instance, so one-per-write fragments the cache.
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Plugins that ship enabled in the install but start DISABLED on a
    /// fresh machine (or after factory reset). These are either alternatives
    /// to the primary stack (Caddy / Nginx are alt web servers, MariaDB is
    /// an alt RDBMS to MySQL) or runtimes most WDC users don't immediately
    /// need (Node.js). Users can enable them from Plugin Manager anytime;
    /// their explicit toggle persists. Keeping them enabled-by-default
    /// meant "Start All" tried to bring up 3 web servers on port 80 simul-
    /// taneously on every fresh install — obvious failure.
    /// </summary>
    private static readonly string[] DefaultDisabledPluginIds =
    {
        "nks.wdc.caddy",
        "nks.wdc.nginx",
        "nks.wdc.mariadb",
        "nks.wdc.node",
    };

    private readonly object _lock = new();
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _uninstalled = new(StringComparer.OrdinalIgnoreCase);

    public PluginState()
    {
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            _disabled.Clear();
            _uninstalled.Clear();
            if (File.Exists(StateFilePath))
            {
                try
                {
                    var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(StateFilePath));
                    if (ids != null) foreach (var id in ids) _disabled.Add(id);
                }
                catch { /* corrupted → empty */ }
            }
            else
            {
                // First run (or post-factory-reset) — seed the default-
                // disabled set so fresh installs don't try to run three
                // web servers at once. Persist the seed so subsequent
                // boots don't re-trigger this branch on every start.
                foreach (var id in DefaultDisabledPluginIds) _disabled.Add(id);
                try { Save(); }
                catch { /* best-effort; IsEnabled still reflects the in-memory seed */ }
            }
            if (File.Exists(UninstalledFilePath))
            {
                try
                {
                    var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(UninstalledFilePath));
                    if (ids != null) foreach (var id in ids) _uninstalled.Add(id);
                }
                catch { /* corrupted → empty */ }
            }
        }
    }

    /// <summary>F91.9: checked by PluginLoader before activating each DLL.</summary>
    public bool IsUninstalled(string pluginId)
    {
        lock (_lock) return _uninstalled.Contains(pluginId);
    }

    /// <summary>
    /// F91.9: mark plugin as uninstalled. PluginLoader skips it on next
    /// boot AND deletes any lingering files. Called from DELETE
    /// /api/plugins/{id} because File.Delete can't remove a loaded DLL on
    /// Windows — the blacklist ensures the plugin stays gone across restart.
    /// </summary>
    public void MarkUninstalled(string pluginId)
    {
        lock (_lock)
        {
            _uninstalled.Add(pluginId);
            SaveUninstalled();
        }
    }

    /// <summary>F91.9: called by PluginLoader after successfully purging the DLL, so the id doesn't stay in the blacklist forever.</summary>
    public void ClearUninstalled(string pluginId)
    {
        lock (_lock)
        {
            if (_uninstalled.Remove(pluginId)) SaveUninstalled();
        }
    }

    public bool IsEnabled(string pluginId)
    {
        lock (_lock) return !_disabled.Contains(pluginId);
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        lock (_lock)
        {
            if (enabled) _disabled.Remove(pluginId);
            else _disabled.Add(pluginId);
            Save();
        }
    }

    public IReadOnlyCollection<string> DisabledIds
    {
        get { lock (_lock) return _disabled.ToArray(); }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        var json = JsonSerializer.Serialize(
            _disabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            IndentedJson);
        // Atomic write: write-to-temp + rename avoids the window where a
        // daemon crash between WriteAllText starting and finishing leaves a
        // truncated state file that breaks all subsequent loads. File.Move
        // with overwrite:true is atomic on NTFS/ext4 at the filesystem level.
        var tmp = StateFilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, StateFilePath, overwrite: true);
    }

    private void SaveUninstalled()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UninstalledFilePath)!);
        var json = JsonSerializer.Serialize(
            _uninstalled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            IndentedJson);
        var tmp = UninstalledFilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, UninstalledFilePath, overwrite: true);
    }
}
