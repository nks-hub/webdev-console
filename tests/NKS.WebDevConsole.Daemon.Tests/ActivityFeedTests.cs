using Dapper;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// What <c>GET /api/activity</c> returns: the settings a user changes, not the
/// ones the daemon writes on its own, and database rows only for a real change.
/// </summary>
public sealed class ActivityFeedTests : IDisposable
{
    private readonly ActivityTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private void SaveSetting(string name)
    {
        var dot = name.IndexOf('.');
        using var conn = _db.Database.CreateConnection();
        conn.Execute("INSERT INTO settings (category, key, value, value_type) VALUES (@Category, @Key, 'x', 'string')",
            new { Category = name[..dot], Key = name[(dot + 1)..] });
    }

    private async Task<List<string>> Names(int limit = 50) =>
        (await ActivityEndpoints.ReadRecentAsync(_db.Database, limit)).Select(r => (string)r.entityName).ToList();

    [Fact]
    public async Task Settings_the_daemon_writes_on_its_own_are_not_listed()
    {
        foreach (var internalKey in new[]
                 { "sync.deviceId", "sync.accountToken", "sync.accountEmail", "sync.lastSyncTime", "catalog.plugin.lastSyncAt", "catalog.plugin.lastSyncOk" })
            SaveSetting(internalKey);

        Assert.DoesNotContain(await Names(), n => n.StartsWith("sync.device") || n.StartsWith("sync.account") || n.StartsWith("sync.last") || n.StartsWith("catalog."));
    }

    [Fact]
    public async Task Settings_a_user_changes_stay_listed_next_to_sites_backups_and_databases()
    {
        foreach (var userKey in new[] { "mcp.enabled", "ports.http", "backup.content.ssl", "service.apache.autoStart" })
            SaveSetting(userKey);
        var history = _db.Repository();
        history.Record("site", "shop.loc", "INSERT");
        history.Record("backup", "manual.zip", "INSERT");
        history.Record("database", "shop_db", "INSERT");

        var names = await Names();

        foreach (var expected in new[] { "mcp.enabled", "ports.http", "backup.content.ssl", "service.apache.autoStart", "shop.loc", "manual.zip", "shop_db" })
            Assert.Contains(expected, names);
    }

    [Fact]
    public async Task The_limit_counts_rows_the_feed_shows()
    {
        SaveSetting("ports.https");
        for (var i = 0; i < 10; i++) SaveSetting($"sync.lastSyncTime{i}");

        // Ten hidden rows are newer than the visible one; they must not push it out of a short page.
        var names = await Names(limit: 3);

        Assert.Contains("ports.https", names);
    }

    [Fact]
    public void The_user_setting_list_holds_no_key_the_daemon_writes_itself()
    {
        var keys = ActivityEndpoints.UserSettings;

        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Matches(@"^[a-z]+\.[A-Za-z_.]+$", k));
        foreach (var internalKey in new[] { "sync.deviceId", "sync.accountToken", "sync.accountEmail", "sync.lastSyncTime" })
            Assert.DoesNotContain(internalKey, keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("catalog."));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1\r\n", true)]
    [InlineData("0\n", false)]
    [InlineData("", null)]
    [InlineData("ERROR 1045", null)]
    [InlineData(null, null)]
    public void The_existence_probe_reads_the_count_mysql_prints(string? output, bool? expected) =>
        Assert.Equal(expected, DatabaseEndpoints.ParseDatabaseExists(output));

    [Theory]
    [InlineData(false, false, "INSERT")]  // created a database that was not there
    [InlineData(true, true, "DELETE")]    // dropped one that was there
    [InlineData(true, false, null)]       // IF NOT EXISTS on an existing database changed nothing
    [InlineData(false, true, null)]       // IF EXISTS on a missing database changed nothing
    [InlineData(null, false, null)]       // mysql could not say: record nothing
    [InlineData(null, true, null)]
    public void A_database_row_is_written_only_for_a_real_create_or_drop(bool? existedBefore, bool dropped, string? expectedOperation)
    {
        DatabaseEndpoints.RecordDatabaseChange(_db.Repository(), "shop_db", existedBefore, dropped);

        var rows = _db.Rows("entity_type = 'database'");
        if (expectedOperation is null) Assert.Empty(rows);
        else Assert.Equal(expectedOperation, (string)Assert.Single(rows).operation);
    }
}
