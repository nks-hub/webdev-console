using Dapper;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Round-trip tests for <see cref="McpToolCallsRepository"/> (Phase 8 audit
/// log). Mirrors the DDL of migration 017 inline so the test fixture stays
/// hermetic — no shared dev DB, no migration runner dependency.
/// </summary>
public sealed class McpToolCallsRepositoryTests
{
    private const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS mcp_tool_calls (
            id              TEXT    PRIMARY KEY,
            called_at       TEXT    NOT NULL,
            session_id      TEXT,
            caller          TEXT    NOT NULL DEFAULT 'unknown',
            tool_name       TEXT    NOT NULL,
            args_summary    TEXT,
            args_hash       TEXT,
            duration_ms     INTEGER NOT NULL DEFAULT 0,
            result_code     TEXT    NOT NULL DEFAULT 'ok',
            error_message   TEXT,
            danger_level    TEXT    NOT NULL DEFAULT 'read',
            intent_id       TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_mcp_tool_calls_called_at
            ON mcp_tool_calls (called_at DESC);
        CREATE INDEX IF NOT EXISTS idx_mcp_tool_calls_session
            ON mcp_tool_calls (session_id, called_at);
        CREATE INDEX IF NOT EXISTS idx_mcp_tool_calls_danger
            ON mcp_tool_calls (danger_level, called_at DESC);
        CREATE INDEX IF NOT EXISTS idx_mcp_tool_calls_tool
            ON mcp_tool_calls (tool_name, called_at DESC);
        """;

    private static (Database db, string path) NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcp-tool-calls-test-{Guid.NewGuid():N}.db");
        var db = new Database(path);
        using (var seed = db.CreateConnection())
        {
            seed.Open();
            seed.Execute(MigrationSql);
        }
        return (db, path);
    }

    [Fact]
    public async Task Insert_RoundTrips_AllFields()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            var id = await repo.InsertAsync(new McpToolCallRow
            {
                SessionId = "sess-1",
                Caller = "claude-code",
                ToolName = "wdc_get_status",
                ArgsSummary = "{}",
                ArgsHash = "abc123",
                DurationMs = 42,
                ResultCode = "ok",
                DangerLevel = "read",
            }, CancellationToken.None);

            Assert.NotEmpty(id);

            var rows = await repo.ListAsync(10, 0, null, null, null, CancellationToken.None);
            Assert.Single(rows);
            Assert.Equal("wdc_get_status", rows[0].ToolName);
            Assert.Equal("claude-code", rows[0].Caller);
            Assert.Equal("sess-1", rows[0].SessionId);
            Assert.Equal(42, rows[0].DurationMs);
            Assert.Equal("read", rows[0].DangerLevel);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task Insert_DefaultsCallerAndResultCode_WhenEmpty()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            await repo.InsertAsync(new McpToolCallRow
            {
                ToolName = "wdc_smoke",
                Caller = "",       // should default to "unknown"
                ResultCode = "",   // should default to "ok"
                DangerLevel = "",  // should default to "read"
            }, CancellationToken.None);

            var rows = await repo.ListAsync(10, 0, null, null, null, CancellationToken.None);
            Assert.Equal("unknown", rows[0].Caller);
            Assert.Equal("ok", rows[0].ResultCode);
            Assert.Equal("read", rows[0].DangerLevel);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task List_FiltersByDangerLevel()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            for (var i = 0; i < 3; i++)
                await repo.InsertAsync(new McpToolCallRow { ToolName = $"r{i}", DangerLevel = "read" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "m1", DangerLevel = "mutate" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "d1", DangerLevel = "destructive" }, CancellationToken.None);

            var reads = await repo.ListAsync(50, 0, "read", null, null, CancellationToken.None);
            Assert.Equal(3, reads.Count);
            var mutates = await repo.ListAsync(50, 0, "mutate", null, null, CancellationToken.None);
            Assert.Single(mutates);
            var destructives = await repo.ListAsync(50, 0, "destructive", null, null, CancellationToken.None);
            Assert.Single(destructives);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task List_FiltersByToolNameAndSession()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "wdc_a", SessionId = "s1" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "wdc_a", SessionId = "s2" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "wdc_b", SessionId = "s1" }, CancellationToken.None);

            var byTool = await repo.ListAsync(50, 0, null, "wdc_a", null, CancellationToken.None);
            Assert.Equal(2, byTool.Count);
            var bySession = await repo.ListAsync(50, 0, null, null, "s1", CancellationToken.None);
            Assert.Equal(2, bySession.Count);
            var both = await repo.ListAsync(50, 0, null, "wdc_a", "s1", CancellationToken.None);
            Assert.Single(both);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task List_OrdersByCalledAtDesc()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            await repo.InsertAsync(new McpToolCallRow
            {
                ToolName = "first", CalledAt = "2026-04-28T10:00:00.000Z",
            }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow
            {
                ToolName = "second", CalledAt = "2026-04-28T11:00:00.000Z",
            }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow
            {
                ToolName = "third", CalledAt = "2026-04-28T12:00:00.000Z",
            }, CancellationToken.None);

            var rows = await repo.ListAsync(10, 0, null, null, null, CancellationToken.None);
            Assert.Equal(new[] { "third", "second", "first" },
                rows.Select(r => r.ToolName).ToArray());
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task List_PaginatesViaLimitOffset()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            for (var i = 0; i < 7; i++)
                await repo.InsertAsync(new McpToolCallRow { ToolName = $"t{i}" }, CancellationToken.None);

            var page1 = await repo.ListAsync(3, 0, null, null, null, CancellationToken.None);
            Assert.Equal(3, page1.Count);
            var page2 = await repo.ListAsync(3, 3, null, null, null, CancellationToken.None);
            Assert.Equal(3, page2.Count);
            var page3 = await repo.ListAsync(3, 6, null, null, null, CancellationToken.None);
            Assert.Single(page3);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task Count_RespectsFilters()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            for (var i = 0; i < 5; i++)
                await repo.InsertAsync(new McpToolCallRow { ToolName = "x", DangerLevel = "read" }, CancellationToken.None);
            for (var i = 0; i < 2; i++)
                await repo.InsertAsync(new McpToolCallRow { ToolName = "y", DangerLevel = "destructive" }, CancellationToken.None);

            Assert.Equal(7, await repo.CountAsync(null, null, null, CancellationToken.None));
            Assert.Equal(5, await repo.CountAsync("read", null, null, CancellationToken.None));
            Assert.Equal(2, await repo.CountAsync(null, "y", null, CancellationToken.None));
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task GetStats_ComputesAllAggregates()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            // 3 reads, 1 mutate, 1 destructive, 1 error
            await repo.InsertAsync(new McpToolCallRow { ToolName = "a", DangerLevel = "read", DurationMs = 10, SessionId = "s1" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "b", DangerLevel = "read", DurationMs = 20, SessionId = "s1" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "c", DangerLevel = "read", DurationMs = 30, SessionId = "s2" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "d", DangerLevel = "mutate", DurationMs = 40, SessionId = "s2" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "e", DangerLevel = "destructive", DurationMs = 100, ResultCode = "error", SessionId = "s2" }, CancellationToken.None);

            var stats = await repo.GetStatsAsync(60 /*1h*/, CancellationToken.None);
            Assert.Equal(5, stats.Total);
            Assert.Equal(3, stats.Reads);
            Assert.Equal(1, stats.Mutates);
            Assert.Equal(1, stats.Destructives);
            Assert.Equal(1, stats.Errors);
            Assert.Equal(2, stats.DistinctSessions);
            // p50 of [10,20,30,40,100] = idx 2 = 30
            Assert.Equal(30, stats.P50DurationMs);
            // p95 of 5 items = idx floor(0.95*4) = 3 = 40
            Assert.Equal(40, stats.P95DurationMs);
            Assert.Equal(20, stats.ErrorRatePercent);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task PruneAsync_RemovesOnlyOldRows()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            await repo.InsertAsync(new McpToolCallRow
            {
                ToolName = "old",
                CalledAt = DateTime.UtcNow.AddDays(-40).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "fresh" }, CancellationToken.None);

            var deleted = await repo.PruneAsync(30, CancellationToken.None);
            Assert.Equal(1, deleted);

            var remaining = await repo.ListAsync(10, 0, null, null, null, CancellationToken.None);
            Assert.Single(remaining);
            Assert.Equal("fresh", remaining[0].ToolName);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task GetByTool_OrdersByCountDesc()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            for (var i = 0; i < 5; i++)
                await repo.InsertAsync(new McpToolCallRow { ToolName = "popular", DurationMs = 10 }, CancellationToken.None);
            for (var i = 0; i < 2; i++)
                await repo.InsertAsync(new McpToolCallRow { ToolName = "rare", DurationMs = 20 }, CancellationToken.None);

            var byTool = await repo.GetByToolAsync(24, 10, CancellationToken.None);
            Assert.Equal(2, byTool.Count);
            Assert.Equal("popular", byTool[0].ToolName);
            Assert.Equal(5, byTool[0].Count);
            Assert.Equal("rare", byTool[1].ToolName);
            Assert.Equal(2, byTool[1].Count);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task SearchQuery_MatchesToolNameOrArgs()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "wdc_list_sites", ArgsSummary = "{}" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "wdc_get_site", ArgsSummary = "{\"domain\":\"blog.loc\"}" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "wdc_create_database", ArgsSummary = "{\"name\":\"shop\"}" }, CancellationToken.None);

            // Match by tool name (case-sensitive LIKE %site%).
            var siteSearch = await repo.ListAsync(50, 0, null, null, null, CancellationToken.None, "site");
            Assert.Equal(2, siteSearch.Count);

            // Match by args content.
            var blogSearch = await repo.ListAsync(50, 0, null, null, null, CancellationToken.None, "blog");
            Assert.Single(blogSearch);
            Assert.Equal("wdc_get_site", blogSearch[0].ToolName);

            // No match.
            var noMatch = await repo.ListAsync(50, 0, null, null, null, CancellationToken.None, "xyz999");
            Assert.Empty(noMatch);

            // Count respects search.
            var count = await repo.CountAsync(null, null, null, CancellationToken.None, "site");
            Assert.Equal(2, count);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task SearchQuery_TrimsWhitespace()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "wdc_list_sites" }, CancellationToken.None);

            // Surrounding whitespace shouldn't break the LIKE.
            var trimmed = await repo.ListAsync(50, 0, null, null, null, CancellationToken.None, "  list  ");
            Assert.Single(trimmed);

            // Empty/whitespace-only string treated as no filter.
            var empty = await repo.ListAsync(50, 0, null, null, null, CancellationToken.None, "   ");
            Assert.Single(empty);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }

    [Fact]
    public async Task GetTimeline_BucketsByHour()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new McpToolCallsRepository(db);
            // Two calls in the current hour (use UTC now).
            var nowHour = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:00:00.000Z");
            await repo.InsertAsync(new McpToolCallRow { ToolName = "a", CalledAt = nowHour, DangerLevel = "read" }, CancellationToken.None);
            await repo.InsertAsync(new McpToolCallRow { ToolName = "b", CalledAt = nowHour, DangerLevel = "destructive", ResultCode = "error" }, CancellationToken.None);

            var timeline = await repo.GetTimelineAsync(24, CancellationToken.None);
            Assert.Single(timeline);
            Assert.Equal(2, timeline[0].Total);
            Assert.Equal(1, timeline[0].Reads);
            Assert.Equal(1, timeline[0].Destructives);
            Assert.Equal(1, timeline[0].Errors);
        }
        finally { try { File.Delete(path); } catch { /* SQLite pool */ } }
    }
}
