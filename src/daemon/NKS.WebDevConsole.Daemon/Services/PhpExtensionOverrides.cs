using System.Collections.Concurrent;
using System.Text.Json;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Persists per-version PHP extension enable/disable overrides on top of the
/// default set computed by <c>PhpExtensionManager.GetDefaultEnabledExtensions</c>.
///
/// Shape of <c>~/.wdc/data/php-extensions.json</c>:
/// <code>
/// { "8.4": { "redis": true, "imagick": false }, "8.3": { "redis": false } }
/// </code>
///
/// Empty file / missing entries mean "use the default" — so users who never
/// touch the UI get the same behaviour as before the override layer existed.
/// Used by the PHP plugin's module initialisation to build the final
/// extensions list the ini template renders, and by the REST endpoint that
/// the PhpManager.vue el-switch posts to.
/// </summary>
public sealed class PhpExtensionOverrides
{
    private static readonly string StateFilePath = Path.Combine(
        WdcPaths.DataRoot, "php-extensions.json");

    // Reuse one JsonSerializerOptions instance across all Save() calls — the
    // serializer caches type-contracts per options reference, so a fresh one
    // per write fragments that cache and re-reflects the payload shape every
    // time the user toggles an extension switch.
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _state = new();

    public PhpExtensionOverrides()
    {
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            _state.Clear();
            if (!File.Exists(StateFilePath)) return;
            try
            {
                var json = File.ReadAllText(StateFilePath);
                using var doc = JsonDocument.Parse(json);
                foreach (var ver in doc.RootElement.EnumerateObject())
                {
                    var dict = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    if (ver.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var ext in ver.Value.EnumerateObject())
                        {
                            if (ext.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                                dict[ext.Name] = ext.Value.GetBoolean();
                        }
                    }
                    _state[ver.Name] = dict;
                }
            }
            catch
            {
                _state.Clear();
            }
        }
    }

    /// <summary>
    /// Returns the override map for a given version (<c>majorMinor</c> like "8.4").
    /// Empty dictionary if no overrides were recorded for that version.
    /// </summary>
    public IReadOnlyDictionary<string, bool> GetOverrides(string majorMinor)
    {
        return _state.TryGetValue(majorMinor, out var dict)
            ? dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>();
    }

    public void SetOverride(string majorMinor, string extensionName, bool enabled)
    {
        lock (_lock)
        {
            var dict = _state.GetOrAdd(majorMinor,
                _ => new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
            dict[extensionName] = enabled;
            Save();
        }
    }

    public void ClearOverride(string majorMinor, string extensionName)
    {
        lock (_lock)
        {
            if (_state.TryGetValue(majorMinor, out var dict))
            {
                dict.TryRemove(extensionName, out _);
                Save();
            }
        }
    }

    /// <summary>
    /// Merges the default extension set with the stored overrides for this
    /// version. Defaults come first, then overrides either flip an existing
    /// entry or add a new one (user explicitly enabled something outside the
    /// default dev set).
    /// </summary>
    public IReadOnlyList<(string Name, bool Enabled)> ApplyOverrides(
        string majorMinor,
        IReadOnlyList<(string Name, bool Enabled)> defaults)
    {
        var overrides = GetOverrides(majorMinor);
        if (overrides.Count == 0) return defaults;

        var merged = defaults.ToDictionary(
            e => e.Name,
            e => e.Enabled,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (name, enabled) in overrides)
            merged[name] = enabled;

        return merged.Select(kvp => (kvp.Key, kvp.Value)).ToList();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        var payload = _state.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));
        var json = JsonSerializer.Serialize(payload, IndentedJson);
        // Atomic write: temp + rename survives a daemon crash mid-write.
        var tmp = StateFilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, StateFilePath, overwrite: true);
    }
}
