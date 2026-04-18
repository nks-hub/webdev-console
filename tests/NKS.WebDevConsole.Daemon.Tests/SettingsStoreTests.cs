using Dapper;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="SettingsStore"/> — wraps the SQLite settings table
/// added in 001_initial.sql. These tests use a per-test temp DB file so
/// they never touch the developer's real <c>~/.wdc/data/state.db</c>.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _tempDb;
    private readonly Database _database;

    public SettingsStoreTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), "nks-settings-tests-" + Guid.NewGuid().ToString("N") + ".db");
        _database = new Database(_tempDb);

        // Minimal schema for these tests — mirrors the settings table from
        // Migrations/001_initial.sql without the other tables we don't need.
        using var conn = _database.CreateConnection();
        conn.Execute(@"
            CREATE TABLE settings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                category TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                value_type TEXT NOT NULL DEFAULT 'string',
                description TEXT,
                is_readonly INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                UNIQUE(category, key)
            );
        ");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    [Fact]
    public void AutoStartEnabled_defaults_to_true_when_unset()
    {
        var store = new SettingsStore(_database);
        Assert.True(store.AutoStartEnabled);
    }

    [Fact]
    public void AutoStartEnabled_returns_false_when_set_to_false()
    {
        var store = new SettingsStore(_database);
        store.Set("daemon", "autoStartEnabled", "false");
        Assert.False(store.AutoStartEnabled);
    }

    [Fact]
    public void AutoStartEnabled_returns_true_when_set_to_true()
    {
        var store = new SettingsStore(_database);
        store.Set("daemon", "autoStartEnabled", "true");
        Assert.True(store.AutoStartEnabled);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("yes", true)]
    public void GetBool_coerces_common_string_values(string raw, bool expected)
    {
        var store = new SettingsStore(_database);
        store.Set("test", "flag", raw);
        Assert.Equal(expected, store.GetBool("test", "flag", defaultValue: !expected));
    }

    [Fact]
    public void GetBool_returns_default_for_unknown_key()
    {
        var store = new SettingsStore(_database);
        Assert.True(store.GetBool("nothing", "here", defaultValue: true));
        Assert.False(store.GetBool("nothing", "here", defaultValue: false));
    }

    [Fact]
    public void GetString_returns_null_for_unknown_key()
    {
        var store = new SettingsStore(_database);
        Assert.Null(store.GetString("nothing", "here"));
    }

    [Fact]
    public void Set_overwrites_existing_value()
    {
        var store = new SettingsStore(_database);
        store.Set("test", "k", "v1");
        Assert.Equal("v1", store.GetString("test", "k"));
        store.Set("test", "k", "v2");
        Assert.Equal("v2", store.GetString("test", "k"));
    }

    [Fact]
    public void Second_instance_reads_values_written_by_first()
    {
        var first = new SettingsStore(_database);
        first.Set("roundtrip", "a", "one");
        first.Set("roundtrip", "b", "two");

        var second = new SettingsStore(_database);
        Assert.Equal("one", second.GetString("roundtrip", "a"));
        Assert.Equal("two", second.GetString("roundtrip", "b"));
    }

    [Fact]
    public void CatalogUrl_defaults_to_public_nks_hub_when_unset()
    {
        // Since commit 3370d3b the built-in default points at the public
        // catalog at https://wdc.nks-hub.cz so end-users don't have to
        // edit settings before binaries installs work. Local dev still
        // overrides via NKS_WDC_CATALOG_URL env.
        Environment.SetEnvironmentVariable("NKS_WDC_CATALOG_URL", null);
        var store = new SettingsStore(_database);
        Assert.Equal("https://wdc.nks-hub.cz", store.CatalogUrl);
    }

    [Fact]
    public void CatalogUrl_reads_from_stored_setting()
    {
        Environment.SetEnvironmentVariable("NKS_WDC_CATALOG_URL", null);
        var store = new SettingsStore(_database);
        store.Set("daemon", "catalogUrl", "https://catalog.example.com");
        Assert.Equal("https://catalog.example.com", store.CatalogUrl);
    }

    [Fact]
    public void CatalogUrl_stored_overrides_env_var()
    {
        Environment.SetEnvironmentVariable("NKS_WDC_CATALOG_URL", "https://env.example.com");
        try
        {
            var store = new SettingsStore(_database);
            store.Set("daemon", "catalogUrl", "https://stored.example.com");
            Assert.Equal("https://stored.example.com", store.CatalogUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NKS_WDC_CATALOG_URL", null);
        }
    }
}
