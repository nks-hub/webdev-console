using Dapper;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Typed wrapper over the existing SQLite <c>settings</c> table (created by
/// <c>Migrations/001_initial.sql</c>). Used by Program.cs to resolve runtime
/// toggles like <c>autoStartEnabled</c> that were previously hardcoded.
///
/// Storage is the same <c>category/key/value</c> table that the existing
/// <c>GET / PUT /api/settings</c> endpoints read and write, so the Settings
/// page UI flips exactly the same rows this service reads. Keeping a single
/// source of truth avoids the JSON-vs-SQLite drift we'd get from two stores.
///
/// Known keys (all under the <c>daemon</c> category):
///   - <c>autoStartEnabled</c> (bool, default true) — start all services on
///     daemon boot. Consulted once at <c>Program.cs</c> bootstrap.
/// </summary>
public sealed class SettingsStore
{
    private readonly Database _database;

    public SettingsStore(Database database)
    {
        _database = database;
    }

    /// <summary>
    /// When true, the daemon auto-starts all service plugins after boot.
    /// Default true (matches the hardcoded pre-Phase-8 behaviour).
    /// </summary>
    public bool AutoStartEnabled => GetBool("daemon", "autoStartEnabled", defaultValue: true);

    /// <summary>Reads a boolean setting. Falsy: "false", "0", "off". Everything else is true.</summary>
    public bool GetBool(string category, string key, bool defaultValue = false)
    {
        var raw = GetString(category, key);
        if (raw is null) return defaultValue;
        return raw.Trim().ToLowerInvariant() switch
        {
            "false" or "0" or "off" or "no" => false,
            "true" or "1" or "on" or "yes" => true,
            _ => defaultValue,
        };
    }

    /// <summary>Reads a string setting or null if unset.</summary>
    public string? GetString(string category, string key)
    {
        try
        {
            using var conn = _database.CreateConnection();
            return conn.QuerySingleOrDefault<string>(
                "SELECT value FROM settings WHERE category = @Category AND key = @Key",
                new { Category = category, Key = key });
        }
        catch
        {
            // Table may not exist yet on very first boot before migrations run
            // in tests — return default rather than crash the daemon.
            return null;
        }
    }

    /// <summary>Upserts a setting. Used by tests and internal callers; frontend uses PUT /api/settings.</summary>
    public void Set(string category, string key, string value)
    {
        using var conn = _database.CreateConnection();
        conn.Execute(
            @"INSERT INTO settings (category, key, value)
              VALUES (@Category, @Key, @Value)
              ON CONFLICT(category, key) DO UPDATE SET value = @Value,
                  updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')",
            new { Category = category, Key = key, Value = value });
    }
}
