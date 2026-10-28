using System.Text.RegularExpressions;
using Dapper;
using DbUp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>A state.db migrated by the real scripts, for the tests of the code that writes config_history.</summary>
internal sealed class ActivityTestDb : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wdc-activity-{Guid.NewGuid():N}.db");

    public ActivityTestDb(int? lastMigration = null)
    {
        Database = new Database(_path);
        if (lastMigration is null)
        {
            Assert.True(new MigrationRunner(NullLogger<MigrationRunner>.Instance).Run(Database.ConnectionString));
            return;
        }
        var upgrade = DeployChanges.To
            .SqliteDatabase(Database.ConnectionString)
            .WithScriptsEmbeddedInAssembly(typeof(MigrationRunner).Assembly,
                s => s.Contains(".Migrations.") && int.Parse(Regex.Match(s, @"\.Migrations\.(\d+)_").Groups[1].Value) <= lastMigration)
            .LogToNowhere()
            .Build()
            .PerformUpgrade();
        Assert.True(upgrade.Successful, upgrade.Error?.Message);
    }

    public Database Database { get; }

    public ConfigHistoryRepository Repository() => new(Database);

    public void MigrateRest() =>
        Assert.True(new MigrationRunner(NullLogger<MigrationRunner>.Instance).Run(Database.ConnectionString));

    public List<dynamic> Rows(string where = "1 = 1")
    {
        using var conn = Database.CreateConnection();
        return conn.Query($"SELECT * FROM config_history WHERE {where} ORDER BY id").ToList();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// The writer behind the Overview activity feed, and migration 022 that lets
/// config_history hold its rows.
/// </summary>
public class ConfigHistoryRepositoryTests
{
    [Fact]
    public void Migration_022_keeps_old_rows_audit_triggers_and_view_and_accepts_the_new_vocabulary()
    {
        using var db = new ActivityTestDb(lastMigration: 21);
        var baseline = db.Rows().Count;
        using (var conn = db.Database.CreateConnection())
            conn.Execute("INSERT INTO settings (category, key, value, value_type) VALUES ('test', 'before', '1', 'string')");
        var before = Assert.Single(db.Rows("entity_name = 'test.before'"));
        Assert.Equal("trigger", (string)before.source);

        db.MigrateRest();

        // The row written by the trigger survives with its id, and so does every older row.
        var kept = Assert.Single(db.Rows("entity_name = 'test.before'"));
        Assert.Equal((long)before.id, (long)kept.id);
        Assert.Equal(baseline + 1, db.Rows().Count);

        // The audit trigger still reaches the rebuilt table, and the view still reads it.
        using (var conn = db.Database.CreateConnection())
        {
            conn.Execute("INSERT INTO settings (category, key, value, value_type) VALUES ('test', 'after', '2', 'string')");
            Assert.Equal(baseline + 2, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM config_history"));
            Assert.Contains("test.after", conn.Query<string>("SELECT entity_name FROM v_recent_changes").ToList());
        }

        // Every index is back, and the table accepts what the daemon now records.
        using (var conn = db.Database.CreateConnection())
        {
            var indexes = conn.Query<string>("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'config_history'").ToList();
            foreach (var name in new[] { "entity", "entity_name", "created_at", "operation", "entity_time", "source" })
                Assert.Contains($"idx_config_history_{name}", indexes);
            conn.Execute("INSERT INTO config_history (entity_type, entity_name, operation, source) VALUES ('backup', 'a.zip', 'RESTORE', 'mcp')");
            Assert.Throws<SqliteException>(() =>
                conn.Execute("INSERT INTO config_history (entity_type, entity_name, operation, source) VALUES ('nonsense', 'x', 'INSERT', 'app')"));
        }
    }

    [Fact]
    public void Record_stores_entity_name_operation_changed_fields_and_values_with_the_app_as_default_source()
    {
        using var db = new ActivityTestDb();
        db.Repository().Record("site", "blog.loc", "UPDATE", ["phpVersion"], """{"phpVersion":"8.3"}""", """{"phpVersion":"8.4"}""");

        var row = Assert.Single(db.Rows("entity_type = 'site'"));
        Assert.Equal("blog.loc", (string)row.entity_name);
        Assert.Equal("UPDATE", (string)row.operation);
        Assert.Equal("""["phpVersion"]""", (string)row.changed_fields);
        Assert.Equal("""{"phpVersion":"8.3"}""", (string)row.old_values);
        Assert.Equal("""{"phpVersion":"8.4"}""", (string)row.new_values);
        Assert.Equal("app", (string)row.source);
        Assert.Equal(0L, (long)row.entity_id);
    }

    [Fact]
    public void Source_is_mcp_for_the_mcp_token_cli_for_the_cli_header_and_app_otherwise()
    {
        using var db = new ActivityTestDb();
        var repo = db.Repository();
        try
        {
            McpCallerContext.IsMcpToken = true;
            repo.Record("database", "from_mcp", "INSERT");
            McpCallerContext.IsMcpToken = false;
            ActivityContext.ViaCli = true;
            repo.Record("database", "from_cli", "INSERT");
            ActivityContext.ViaCli = false;
            repo.Record("database", "from_app", "INSERT");
        }
        finally
        {
            McpCallerContext.IsMcpToken = false;
            ActivityContext.ViaCli = false;
        }

        Assert.Equal("mcp", (string)Assert.Single(db.Rows("entity_name = 'from_mcp'")).source);
        Assert.Equal("cli", (string)Assert.Single(db.Rows("entity_name = 'from_cli'")).source);
        Assert.Equal("app", (string)Assert.Single(db.Rows("entity_name = 'from_app'")).source);
    }

    [Fact]
    public void A_database_create_and_drop_are_accepted_by_the_schema()
    {
        // The two database routes need a MySQL client to run, so this covers what they write.
        using var db = new ActivityTestDb();
        var repo = db.Repository();
        repo.Record("database", "shop_db", "INSERT");
        repo.Record("database", "shop_db", "DELETE");

        Assert.Equal(["INSERT", "DELETE"], db.Rows("entity_type = 'database'").Select(r => (string)r.operation));
    }

    [Fact]
    public void Record_never_throws_when_the_write_fails()
    {
        // A database with no config_history table: the operation being recorded must not fail with it.
        var path = Path.Combine(Path.GetTempPath(), $"wdc-activity-empty-{Guid.NewGuid():N}.db");
        try
        {
            new ConfigHistoryRepository(new Database(path)).Record("site", "x.loc", "INSERT");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
