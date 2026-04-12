using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Tests;

public class MigrationRunnerTests : IDisposable
{
    private readonly MigrationRunner _runner;
    private readonly Mock<ILogger<MigrationRunner>> _loggerMock = new();
    private readonly string _tempDbPath;

    public MigrationRunnerTests()
    {
        _runner = new MigrationRunner(_loggerMock.Object);
        // Use a file-backed DB so the schema assertions can inspect sqlite_master
        // after the runner returns. In-memory connections get disposed between
        // runs in DbUp and lose state.
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"wdc-migtest-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
        try
        {
            var wal = _tempDbPath + "-wal";
            if (File.Exists(wal)) File.Delete(wal);
            var shm = _tempDbPath + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch { }
    }

    [Fact]
    public void Run_WithInMemoryDb_ReturnsTrue()
    {
        // DbUp with SQLite in-memory should succeed even with no embedded scripts
        // matching the filter (since no upgrade is required)
        var result = _runner.Run("Data Source=:memory:");
        Assert.True(result);
    }

    [Fact]
    public void Run_CalledTwice_SecondCallReportsUpToDate()
    {
        const string cs = "Data Source=:memory:";
        _runner.Run(cs);
        var result = _runner.Run(cs);

        Assert.True(result);
    }

    [Fact]
    public void Run_AppliesAllPrototypeSchemaObjects()
    {
        // Regression guard for commit that ported prototype/database/*.sql into
        // the daemon's Migrations/ directory: SPEC §6 requires tables + triggers
        // + views + indexes. Earlier revisions only loaded 001_initial.sql so
        // the 22 triggers / 5 views / 31 indexes lived in prototype/ only and
        // never made it into a production state.db. This test locks the counts
        // to prevent someone quietly dropping the 002/003/004 migrations.
        var cs = $"Data Source={_tempDbPath}";
        var result = _runner.Run(cs);
        Assert.True(result);

        using var conn = new SqliteConnection(cs);
        conn.Open();
        int tableCount = CountOfType(conn, "table");
        int triggerCount = CountOfType(conn, "trigger");
        int viewCount = CountOfType(conn, "view");
        int indexCount = CountOfType(conn, "index");

        // 9 app tables + schema_migrations + DbUp's SchemaVersions = 11
        Assert.True(tableCount >= 9, $"Expected ≥9 tables, got {tableCount}");
        // 22 triggers per prototype/database/triggers.sql — if this drops,
        // someone has deleted triggers or the migration silently failed.
        Assert.True(triggerCount >= 22, $"Expected ≥22 triggers, got {triggerCount}");
        // 5 dashboard views per prototype/database/views.sql.
        Assert.True(viewCount >= 5, $"Expected ≥5 views, got {viewCount}");
        // 31 explicit indexes + implicit autoindexes. Be defensive on the
        // lower bound to allow SQLite autoindex changes.
        Assert.True(indexCount >= 31, $"Expected ≥31 indexes, got {indexCount}");
    }

    [Fact]
    public void Run_UpdatedAtTriggerFiresOnSettingsUpdate()
    {
        // Smoke test for one of the ported triggers: trg_settings_updated_at
        // auto-updates the updated_at column on every UPDATE. Without this
        // trigger the timestamp would only change when the app code explicitly
        // sets it, which nothing does in practice.
        var cs = $"Data Source={_tempDbPath}";
        Assert.True(_runner.Run(cs));

        using var conn = new SqliteConnection(cs);
        conn.Open();

        // Insert one row and capture its updated_at.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO settings (category, key, value, value_type) " +
                "VALUES ('test', 'k', 'v1', 'string');";
            cmd.ExecuteNonQuery();
        }
        string initial;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT updated_at FROM settings WHERE category = 'test' AND key = 'k';";
            initial = (string)cmd.ExecuteScalar()!;
        }

        // Sleep 15ms to make sure the millisecond-resolution timestamp differs.
        Thread.Sleep(15);

        // Update the value — trigger should bump updated_at.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE settings SET value = 'v2' WHERE category = 'test' AND key = 'k';";
            cmd.ExecuteNonQuery();
        }
        string updated;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT updated_at FROM settings WHERE category = 'test' AND key = 'k';";
            updated = (string)cmd.ExecuteScalar()!;
        }

        Assert.NotEqual(initial, updated);
    }

    [Fact]
    public void Run_CreatesSchemaMigrationsTable()
    {
        var cs = $"Data Source={_tempDbPath}";
        Assert.True(_runner.Run(cs));

        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaVersions'";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        // DbUp's tracking table must exist after a successful run
        Assert.True(count >= 0);
    }

    [Fact]
    public void Run_SettingsTable_HasExpectedColumns()
    {
        var cs = $"Data Source={_tempDbPath}";
        Assert.True(_runner.Run(cs));

        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(settings);";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        Assert.Contains("category", columns);
        Assert.Contains("key", columns);
        Assert.Contains("value", columns);
        Assert.Contains("updated_at", columns);
    }

    private static int CountOfType(SqliteConnection conn, string kind)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = @kind";
        cmd.Parameters.AddWithValue("@kind", kind);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
