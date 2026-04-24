namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Cross-ALC settings accessor. Plugins can consume this via DI to read
/// daemon-owned configuration (ports, paths, flags) without taking a
/// direct dependency on the daemon's SettingsStore implementation.
///
/// Keys are namespaced by a `category` (e.g. "ports", "paths", "general").
/// The concrete implementation (SettingsStore in the daemon) persists to
/// SQLite; plugins just read the values they need on startup or on a
/// settings-changed callback. There is no write side intentionally —
/// plugins should not mutate global daemon settings; if a plugin needs
/// its own storage, use its own subsystem.
/// </summary>
public interface IWdcSettings
{
    /// <summary>Return the raw string value or null if the key is absent.</summary>
    string? GetString(string category, string key);

    /// <summary>Parse as int; fall back to defaultValue on missing/invalid.</summary>
    int GetInt(string category, string key, int defaultValue = 0);

    /// <summary>Parse as bool ("true"/"false"/"1"/"0"); fall back to defaultValue.</summary>
    bool GetBool(string category, string key, bool defaultValue = false);
}
