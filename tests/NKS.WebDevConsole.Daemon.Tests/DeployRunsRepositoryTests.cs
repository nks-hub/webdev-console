using Dapper;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Deploy;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Round-trip tests for <see cref="DeployRunsRepository"/> against an in-memory
/// SQLite DB seeded with the same DDL as migration 006. Keeps the tests
/// hermetic — no shared file, no concurrent dev DB pollution.
/// </summary>
public sealed class DeployRunsRepositoryTests
{
    // Mirrors migrations 006 + 009 (group_id col) + 010 (pre-deploy backup
    // cols) as a single inline DDL — the repo's BaseSelect now references
    // every column added by 010 so the test fixture must keep up.
    private const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS deploy_runs (
            id              TEXT NOT NULL PRIMARY KEY,
            domain          TEXT NOT NULL,
            host            TEXT NOT NULL,
            release_id      TEXT,
            branch          TEXT,
            commit_sha      TEXT,
            status          TEXT NOT NULL DEFAULT 'queued',
            is_past_ponr    INTEGER NOT NULL DEFAULT 0,
            started_at      TEXT NOT NULL,
            completed_at    TEXT,
            exit_code       INTEGER,
            error_message   TEXT,
            duration_ms     INTEGER,
            triggered_by    TEXT NOT NULL DEFAULT 'gui',
            backend_id      TEXT NOT NULL DEFAULT 'nks-deploy',
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL,
            group_id        TEXT,
            pre_deploy_backup_path        TEXT,
            pre_deploy_backup_size_bytes  INTEGER,
            CHECK (status IN (
                'queued', 'running', 'awaiting_soak', 'completed',
                'failed', 'cancelled', 'rolling_back', 'rolled_back'
            )),
            CHECK (is_past_ponr IN (0, 1))
        );
        """;

    private static (Database Db, string DbPath) NewDb()
    {
        // Per-test temp file so connection pooling can hand back fresh
        // connections that all see the same schema. ":memory:" closes when
        // the first connection drops, which breaks the repo's create-and-
        // dispose pattern.
        var path = Path.Combine(Path.GetTempPath(), $"nksdeploy-test-{Guid.NewGuid():N}.db");
        var db = new Database(path);
        using (var seed = db.CreateConnection())
        {
            seed.Open();
            seed.Execute(MigrationSql);
        }
        return (db, path);
    }

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch { /* connection pool may still hold a handle on Windows; harmless. */ }
    }

    private static DeployRunRow MakeRow(string id, string status = "running")
    {
        var now = DateTimeOffset.UtcNow;
        return new DeployRunRow(
            Id: id,
            Domain: "myapp.loc",
            Host: "production",
            ReleaseId: "20260426_073000",
            Branch: "main",
            CommitSha: "abc1234",
            Status: status,
            IsPastPonr: false,
            StartedAt: now,
            CompletedAt: null,
            ExitCode: null,
            ErrorMessage: null,
            DurationMs: null,
            TriggeredBy: "gui",
            BackendId: "nks-deploy",
            CreatedAt: now,
            UpdatedAt: now);
    }

    [Fact]
    public async Task InsertAndGetById_RoundTrips()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var id = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);

            Assert.NotNull(fetched);
            Assert.Equal(id, fetched!.Id);
            Assert.Equal("myapp.loc", fetched.Domain);
            Assert.Equal("running", fetched.Status);
            Assert.False(fetched.IsPastPonr);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task GetById_ReturnsNullForUnknownId()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var fetched = await repo.GetByIdAsync(Guid.NewGuid().ToString(), CancellationToken.None);
            Assert.Null(fetched);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task UpdateStatus_TransitionsRow()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var id = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(id, "running"), CancellationToken.None);
            await repo.UpdateStatusAsync(id, "awaiting_soak", CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.Equal("awaiting_soak", fetched!.Status);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task MarkPastPonr_FlipsFlag()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var id = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);
            await repo.MarkPastPonrAsync(id, CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.True(fetched!.IsPastPonr);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task MarkCompleted_Success_StoresStatusAndDuration()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var id = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);
            await repo.MarkCompletedAsync(id, success: true, exitCode: 0, errorMessage: null, durationMs: 12345, CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.Equal("completed", fetched!.Status);
            Assert.Equal(0, fetched.ExitCode);
            Assert.Equal(12345L, fetched.DurationMs);
            Assert.NotNull(fetched.CompletedAt);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task MarkCompleted_Failure_StoresStatusAndError()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var id = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);
            await repo.MarkCompletedAsync(id, success: false, exitCode: 1, errorMessage: "rsync failed", durationMs: 2345, CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.Equal("failed", fetched!.Status);
            Assert.Equal(1, fetched.ExitCode);
            Assert.Equal("rsync failed", fetched.ErrorMessage);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ListForDomain_ReturnsNewestFirst()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var older = MakeRow(Guid.NewGuid().ToString()) with { StartedAt = DateTimeOffset.UtcNow.AddHours(-2) };
            var newer = MakeRow(Guid.NewGuid().ToString()) with { StartedAt = DateTimeOffset.UtcNow };
            await repo.InsertAsync(older, CancellationToken.None);
            await repo.InsertAsync(newer, CancellationToken.None);

            var list = await repo.ListForDomainAsync("myapp.loc", limit: 10, CancellationToken.None);
            Assert.Equal(2, list.Count);
            Assert.Equal(newer.Id, list[0].Id);
            Assert.Equal(older.Id, list[1].Id);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ListInFlight_ReturnsRunningRowsOnly()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            await repo.InsertAsync(MakeRow(Guid.NewGuid().ToString(), "running"), CancellationToken.None);
            await repo.InsertAsync(MakeRow(Guid.NewGuid().ToString(), "awaiting_soak"), CancellationToken.None);
            await repo.InsertAsync(MakeRow(Guid.NewGuid().ToString(), "completed"), CancellationToken.None);
            await repo.InsertAsync(MakeRow(Guid.NewGuid().ToString(), "rolled_back"), CancellationToken.None);

            var inFlight = await repo.ListInFlightAsync(CancellationToken.None);
            Assert.Equal(2, inFlight.Count);
            Assert.All(inFlight, r => Assert.Contains(r.Status, new[] { "running", "awaiting_soak", "rolling_back" }));
        }
        finally { Cleanup(path); }
    }

    // ────────────────── Phase 6.15b — group_id FK methods ──────────────────

    [Fact]
    public async Task SetGroupIdAsync_StampsGroupIdOnRunRow()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var id = Guid.NewGuid().ToString();
            var groupId = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);

            await repo.SetGroupIdAsync(id, groupId, CancellationToken.None);

            // Verify via direct SQL — the group_id field isn't surfaced
            // on DeployRunRow so we read it back over the connection.
            using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var stored = await Dapper.SqlMapper.QuerySingleAsync<string?>(conn,
                "SELECT group_id FROM deploy_runs WHERE id = @Id",
                new { Id = id });
            Assert.Equal(groupId, stored);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task SetGroupIdAsync_IsIdempotent_OverwritesPriorValue()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var id = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);

            await repo.SetGroupIdAsync(id, "group-1", CancellationToken.None);
            await repo.SetGroupIdAsync(id, "group-2", CancellationToken.None);

            using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var stored = await Dapper.SqlMapper.QuerySingleAsync<string?>(conn,
                "SELECT group_id FROM deploy_runs WHERE id = @Id",
                new { Id = id });
            Assert.Equal("group-2", stored);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task SetGroupIdAsync_DoesNotThrow_ForUnknownDeployId()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            // No row inserted — UPDATE with no match is a no-op, not an error.
            // The coordinator's best-effort try/catch wraps it anyway, but we
            // still want the repo itself to behave gracefully.
            await repo.SetGroupIdAsync("unknown-deploy-id", "group-x", CancellationToken.None);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ListByGroupAsync_ReturnsEmpty_ForUnknownGroupId()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var rows = await repo.ListByGroupAsync("does-not-exist", CancellationToken.None);
            Assert.Empty(rows);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ListByGroupAsync_ReturnsOnlyRowsWithMatchingGroupId()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var groupId = Guid.NewGuid().ToString();

            // Two rows in our group
            var inGroupA = Guid.NewGuid().ToString();
            var inGroupB = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(inGroupA), CancellationToken.None);
            await repo.InsertAsync(MakeRow(inGroupB), CancellationToken.None);
            await repo.SetGroupIdAsync(inGroupA, groupId, CancellationToken.None);
            await repo.SetGroupIdAsync(inGroupB, groupId, CancellationToken.None);

            // One row with a different group_id (should be excluded)
            var otherGroup = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(otherGroup), CancellationToken.None);
            await repo.SetGroupIdAsync(otherGroup, "different-group", CancellationToken.None);

            // One row with NO group_id (should be excluded)
            var standalone = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(standalone), CancellationToken.None);

            var rows = await repo.ListByGroupAsync(groupId, CancellationToken.None);

            Assert.Equal(2, rows.Count);
            var ids = rows.Select(r => r.Id).ToHashSet();
            Assert.Contains(inGroupA, ids);
            Assert.Contains(inGroupB, ids);
            Assert.DoesNotContain(otherGroup, ids);
            Assert.DoesNotContain(standalone, ids);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ListByGroupAsync_ProjectsAllColumns()
    {
        // Verify the BaseSelect projection (including group_id-related joins)
        // round-trips through ToRecord cleanly — catches schema drift where
        // a new column is added without updating RawRow.
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployRunsRepository(db);
            var groupId = Guid.NewGuid().ToString();
            var id = Guid.NewGuid().ToString();
            await repo.InsertAsync(MakeRow(id, status: "completed"), CancellationToken.None);
            await repo.SetGroupIdAsync(id, groupId, CancellationToken.None);

            var rows = await repo.ListByGroupAsync(groupId, CancellationToken.None);

            Assert.Single(rows);
            var row = rows[0];
            Assert.Equal(id, row.Id);
            Assert.Equal("myapp.loc", row.Domain);
            Assert.Equal("production", row.Host);
            Assert.Equal("completed", row.Status);
            Assert.Equal("nks-deploy", row.BackendId);
        }
        finally { Cleanup(path); }
    }
}
