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

    // Shared across all Save() calls — JsonSerializer caches type contracts
    // per options instance, so one-per-write fragments the cache.
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    public PluginState()
    {
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            _disabled.Clear();
            if (!File.Exists(StateFilePath)) return;
            try
            {
                var json = File.ReadAllText(StateFilePath);
                var ids = JsonSerializer.Deserialize<string[]>(json);
                if (ids != null)
                {
                    foreach (var id in ids) _disabled.Add(id);
                }
            }
            catch
            {
                // Corrupted file — treat as empty, safest default.
            }
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
        File.WriteAllText(StateFilePath, json);
    }
}
