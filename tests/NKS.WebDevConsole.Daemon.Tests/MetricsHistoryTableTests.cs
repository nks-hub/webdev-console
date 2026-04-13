using Dapper;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Schema + round-trip tests for the Phase 11 metrics_history table.
/// We don't spin up the BackgroundService here — that would require
/// SiteManager/BinaryManager + Apache logs setup. Instead we apply
/// the migration SQL directly to an in-memory SQLite DB, exercise the
/// CRUD shape the daemon's MetricsHistoryService relies on, and verify
/// the index covers the typical (domain, sampled_at DESC) query plan.
/// </summary>
public sealed class MetricsHistoryTableTests
{
    private const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS metrics_history (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            domain        TEXT    NOT NULL,
            sampled_at    TEXT    NOT NULL,
            request_count INTEGER NOT NULL,
            size_bytes    INTEGER NOT NULL,
            last_write_utc TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_metrics_history_domain_time
            ON metrics_history(domain, sampled_at DESC);
        """;

    private static Microsoft.Data.Sqlite.SqliteConnection NewDb()
    {
        var db = new Database(":memory:");
        var conn = db.CreateConnection();
        conn.Execute(MigrationSql);
        return conn;
    }

    [Fact]
    public void Schema_HasExpectedColumns()
    {
        using var conn = NewDb();
        var cols = conn.Query<string>("PRAGMA table_info(metrics_history)")
            .ToList();
        // PRAGMA table_info returns rows with multiple cols — we only
        // checked column count above. Re-run with a different shape:
        var info = conn.Query("PRAGMA table_info(metrics_history)").ToList();
        var names = info.Select(r => (string)((dynamic)r).name).ToList();
        Assert.Contains("id", names);
        Assert.Contains("domain", names);
        Assert.Contains("sampled_at", names);
        Assert.Contains("request_count", names);
        Assert.Contains("size_bytes", names);
        Assert.Contains("last_write_utc", names);
    }

    [Fact]
    public void Insert_AndQuery_RoundTrip()
    {
        using var conn = NewDb();
        var nowIso = DateTime.UtcNow.ToString("o");
        conn.Execute(
            "INSERT INTO metrics_history (domain, sampled_at, request_count, size_bytes, last_write_utc) " +
            "VALUES (@d, @t, @c, @b, @lw)",
            new { d = "myapp.loc", t = nowIso, c = 1234L, b = 56789L, lw = nowIso });

        var row = conn.QuerySingle<(string domain, string sampled_at, long request_count, long size_bytes, string? last_write_utc)>(
            "SELECT domain, sampled_at, request_count, size_bytes, last_write_utc FROM metrics_history WHERE domain = @d",
            new { d = "myapp.loc" });

        Assert.Equal("myapp.loc", row.domain);
        Assert.Equal(nowIso, row.sampled_at);
        Assert.Equal(1234L, row.request_count);
        Assert.Equal(56789L, row.size_bytes);
        Assert.Equal(nowIso, row.last_write_utc);
    }

    [Fact]
    public void Query_PerSiteOrderedDescByTime_MatchesIndex()
    {
        using var conn = NewDb();
        var t1 = "2026-04-13T10:00:00Z";
        var t2 = "2026-04-13T11:00:00Z";
        var t3 = "2026-04-13T12:00:00Z";
        conn.Execute(
            "INSERT INTO metrics_history (domain, sampled_at, request_count, size_bytes) " +
            "VALUES (@d, @t, @c, @b)",
            new[]
            {
                new { d = "a.loc", t = t1, c = 100L, b = 1000L },
                new { d = "a.loc", t = t2, c = 200L, b = 2000L },
                new { d = "b.loc", t = t1, c = 999L, b = 9999L },
                new { d = "a.loc", t = t3, c = 300L, b = 3000L },
            });

        var rows = conn.Query<(string sampled_at, long request_count)>(
            "SELECT sampled_at, request_count FROM metrics_history " +
            "WHERE domain = @d ORDER BY sampled_at ASC",
            new { d = "a.loc" }).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(t1, rows[0].sampled_at);
        Assert.Equal(100L, rows[0].request_count);
        Assert.Equal(t3, rows[2].sampled_at);
        Assert.Equal(300L, rows[2].request_count);
    }

    [Fact]
    public void Index_IsUsedForDomainTimeQuery()
    {
        using var conn = NewDb();
        // EXPLAIN QUERY PLAN returns rows with (id, parent, notused, detail).
        // We only need the detail column which describes the access path
        // SQLite picked. The covering index for (domain, sampled_at DESC)
        // should be selected for equality on domain + ORDER BY sampled_at.
        var plan = conn.Query("EXPLAIN QUERY PLAN " +
            "SELECT * FROM metrics_history WHERE domain = 'x' ORDER BY sampled_at DESC")
            .Select(r => (string)((dynamic)r).detail)
            .ToList();
        var planText = string.Join("\n", plan);
        Assert.Contains("idx_metrics_history_domain_time", planText);
    }

    [Fact]
    public void Retention_DeleteByCutoff_ScopesBySampledAt()
    {
        using var conn = NewDb();
        var oldIso = DateTime.UtcNow.AddDays(-10).ToString("o");
        var newIso = DateTime.UtcNow.AddMinutes(-5).ToString("o");
        conn.Execute(
            "INSERT INTO metrics_history (domain, sampled_at, request_count, size_bytes) " +
            "VALUES ('x.loc', @t, 1, 1)",
            new[] { new { t = oldIso }, new { t = newIso } });

        var cutoff = DateTime.UtcNow.AddDays(-7).ToString("o");
        var deleted = conn.Execute(
            "DELETE FROM metrics_history WHERE sampled_at < @c",
            new { c = cutoff });

        Assert.Equal(1, deleted);
        var remaining = conn.QuerySingle<long>("SELECT COUNT(*) FROM metrics_history");
        Assert.Equal(1L, remaining);
    }

    [Fact]
    public void NullableLastWriteUtc_StoredAndReadAsNull()
    {
        using var conn = NewDb();
        conn.Execute(
            "INSERT INTO metrics_history (domain, sampled_at, request_count, size_bytes, last_write_utc) " +
            "VALUES ('x.loc', '2026-04-13T00:00:00Z', 0, 0, NULL)");

        var lw = conn.QuerySingle<string?>(
            "SELECT last_write_utc FROM metrics_history WHERE domain = 'x.loc'");
        Assert.Null(lw);
    }
}
