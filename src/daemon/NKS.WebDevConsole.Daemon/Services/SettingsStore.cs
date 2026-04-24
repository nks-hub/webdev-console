using Dapper;
using NKS.WebDevConsole.Core.Interfaces;
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
public sealed class SettingsStore : IWdcSettings
{
    private readonly Database _database;

    public SettingsStore(Database database)
    {
        _database = database;
    }

    /// <summary>
    /// <see cref="IWdcSettings.GetInt"/> implementation — parses whatever the
    /// settings row holds as an int. Invalid or missing rows yield the
    /// caller-supplied default so plugins don't have to repeat the
    /// try-parse dance on every read. Shares the same table as every other
    /// GetString/GetBool read, so `PUT /api/settings` updates are visible
    /// on the next call.
    /// </summary>
    public int GetInt(string category, string key, int defaultValue = 0)
    {
        var raw = GetString(category, key);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        return int.TryParse(
            raw.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) ? parsed : defaultValue;
    }

    /// <summary>
    /// When true, the daemon auto-starts all service plugins after boot.
    /// Default true (matches the hardcoded pre-Phase-8 behaviour).
    /// </summary>
    public bool AutoStartEnabled => GetBool("daemon", "autoStartEnabled", defaultValue: true);

    /// <summary>
    /// F95: when true, the <c>PluginCatalogSyncService</c> fetches the
    /// plugin catalog from <see cref="CatalogUrl"/> and downloads any
    /// missing plugin release artifacts into <c>~/.wdc/plugins/</c> on
    /// a 6-hour loop. Default false so dev builds running against the
    /// monorepo's bundled <c>build/plugins/</c> directory don't waste a
    /// network round-trip. Legacy env var <c>NKS_WDC_PLUGIN_AUTOSYNC</c>
    /// still works when this row is unset.
    /// </summary>
    public bool PluginAutoSyncEnabled => GetBool("plugins", "autoSyncEnabled", defaultValue: false);

    /// <summary>
    /// URL of the cloud catalog + config-sync service that the daemon
    /// pulls binary release metadata from. Editable via the Settings
    /// page in the Electron UI (<c>/api/settings</c>) so users can
    /// point at their self-hosted <c>services/catalog-api</c> deployment
    /// without restarting. Fallback order:
    /// <c>settings.daemon.catalogUrl</c> → env <c>NKS_WDC_CATALOG_URL</c>
    /// → built-in default (the public NKS catalog at https://wdc.nks-hub.cz).
    /// </summary>
    public string CatalogUrl
    {
        get
        {
            var stored = GetString("daemon", "catalogUrl");
            // Migration: pre-v0.2.3 installs auto-pointed at the local
            // catalog-api sidecar (127.0.0.1:8765) because Electron spawned
            // it and wrote the URL via env. The sidecar was removed in
            // v0.2.3 — treat any loopback entry as stale and fall through
            // to the public default so Binaries pages stop showing an
            // empty 3-release fixture to end users.
            if (!string.IsNullOrWhiteSpace(stored) && !IsLoopbackCatalogUrl(stored))
                return stored;
            var env = Environment.GetEnvironmentVariable("NKS_WDC_CATALOG_URL");
            if (!string.IsNullOrWhiteSpace(env) && !IsLoopbackCatalogUrl(env))
                return env;
            return "https://wdc.nks-hub.cz";
        }
    }

    private static bool IsLoopbackCatalogUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var lower = url.Trim().ToLowerInvariant();
        return lower.Contains("127.0.0.1:8765")
            || lower.Contains("localhost:8765")
            || lower.Contains("://127.0.0.1/")
            || lower.Contains("://localhost/");
    }

    /// <summary>
    /// TCP port on which the WDC-managed mysqld listens. Stored as
    /// <c>ports.mysql</c> in the settings table (same key the Porty UI tab
    /// writes). Falls back to 3306 when unset so existing installs are
    /// unaffected.
    /// </summary>
    public int MysqlPort
    {
        get
        {
            var raw = GetString("ports", "mysql");
            if (raw is not null && int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var port) && port > 0)
                return port;
            return 3306;
        }
    }

    /// <summary>
    /// Like <see cref="MysqlPort"/> but returns false when no explicit <c>ports.mysql</c>
    /// row exists, so callers can fall back to plugin-DI port discovery (F49b) rather
    /// than blindly defaulting to 3306.
    /// </summary>
    public bool TryReadMysqlPort(out int port)
    {
        port = 3306;
        var raw = GetString("ports", "mysql");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            port = parsed;
            return true;
        }
        return false;
    }

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
