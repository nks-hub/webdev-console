using Dapper;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Deploy;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Round-trip tests for <see cref="DeployGroupsRepository"/> against a per-test
/// temp SQLite file seeded with the inline DDL matching migration 009.
/// </summary>
public sealed class DeployGroupsRepositoryTests
{
    // Inline DDL mirrors migration 009 exactly (without the ALTER TABLE for
    // deploy_runs, which is tested separately in DeployRunsRepositoryTests).
    private const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS deploy_groups (
            id              TEXT    NOT NULL PRIMARY KEY,
            domain          TEXT    NOT NULL,
            hosts_json      TEXT    NOT NULL,
            deploy_ids_json TEXT    NOT NULL DEFAULT '{}',
            phase           TEXT    NOT NULL DEFAULT 'initializing',
            started_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            completed_at    TEXT,
            error_message   TEXT,
            triggered_by    TEXT    NOT NULL DEFAULT 'gui',
            created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            CHECK (phase IN (
                'initializing','preflight','deploying','awaiting_all_soak',
                'all_succeeded','partial_failure','rolling_back_all',
                'rolled_back','group_failed'
            )),
            CHECK (triggered_by IN ('gui','mcp','cli'))
        );
        """;

    private static (Database Db, string DbPath) NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nks-groups-test-{Guid.NewGuid():N}.db");
        var db = new Database(path);
        using var seed = db.CreateConnection();
        seed.Execute(MigrationSql);
        return (db, path);
    }

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static DeployGroupRow MakeRow(
        string id,
        string domain = "myapp.loc",
        string phase = "initializing",
        IReadOnlyList<string>? hosts = null,
        IReadOnlyDictionary<string, string>? hostDeployIds = null,
        DateTimeOffset? startedAt = null,
        string triggeredBy = "gui")
    {
        var now = DateTimeOffset.UtcNow;
        return new DeployGroupRow(
            Id: id,
            Domain: domain,
            Hosts: hosts ?? new[] { "production", "staging" },
            HostDeployIds: hostDeployIds ?? new Dictionary<string, string>(),
            Phase: phase,
            StartedAt: startedAt ?? now,
            CompletedAt: null,
            ErrorMessage: null,
            TriggeredBy: triggeredBy,
            CreatedAt: now,
            UpdatedAt: now);
    }

    // --- Insert + GetById roundtrip ---

    [Fact]
    public async Task InsertAndGetById_RoundTrips()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var id = Guid.NewGuid().ToString("N");
            var hosts = new[] { "host-a", "host-b", "host-c" };
            var deployIds = new Dictionary<string, string> { ["host-a"] = "deploy-1" };
            await repo.InsertAsync(MakeRow(id, hosts: hosts, hostDeployIds: deployIds), CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);

            Assert.NotNull(fetched);
            Assert.Equal(id, fetched!.Id);
            Assert.Equal("myapp.loc", fetched.Domain);
            Assert.Equal("initializing", fetched.Phase);
            Assert.Equal(3, fetched.Hosts.Count);
            Assert.Contains("host-a", fetched.Hosts);
            Assert.Contains("host-b", fetched.Hosts);
            Assert.Contains("host-c", fetched.Hosts);
            Assert.True(fetched.HostDeployIds.ContainsKey("host-a"));
            Assert.Equal("deploy-1", fetched.HostDeployIds["host-a"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task GetById_ReturnsNull_ForUnknownId()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var result = await repo.GetByIdAsync(Guid.NewGuid().ToString("N"), CancellationToken.None);
            Assert.Null(result);
        }
        finally { Cleanup(path); }
    }

    // --- UpdatePhase non-terminal ---

    [Fact]
    public async Task UpdatePhase_NonTerminal_CompletedAtStaysNull()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var id = Guid.NewGuid().ToString("N");
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);

            await repo.UpdatePhaseAsync(id, "deploying", isTerminal: false, null, CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.NotNull(fetched);
            Assert.Equal("deploying", fetched!.Phase);
            Assert.Null(fetched.CompletedAt);
        }
        finally { Cleanup(path); }
    }

    // --- UpdatePhase terminal ---

    [Fact]
    public async Task UpdatePhase_Terminal_SetsCompletedAtAndErrorMessage()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var id = Guid.NewGuid().ToString("N");
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);

            await repo.UpdatePhaseAsync(id, "group_failed", isTerminal: true,
                "host-a rsync timed out", CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.NotNull(fetched);
            Assert.Equal("group_failed", fetched!.Phase);
            Assert.NotNull(fetched.CompletedAt);
            Assert.Equal("host-a rsync timed out", fetched.ErrorMessage);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task UpdatePhase_Terminal_NullErrorMessage_CompletedAtStillSet()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var id = Guid.NewGuid().ToString("N");
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);

            await repo.UpdatePhaseAsync(id, "all_succeeded", isTerminal: true,
                null, CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.NotNull(fetched);
            Assert.Equal("all_succeeded", fetched!.Phase);
            Assert.NotNull(fetched.CompletedAt);
            Assert.Null(fetched.ErrorMessage);
        }
        finally { Cleanup(path); }
    }

    // --- RecordHostDeploy ---

    [Fact]
    public async Task RecordHostDeploy_SubsequentCallsAddEntries_LatestWriteWins()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var id = Guid.NewGuid().ToString("N");
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);

            // First record
            await repo.RecordHostDeployAsync(id, "production", "deploy-001", CancellationToken.None);
            var after1 = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.Equal("deploy-001", after1!.HostDeployIds["production"]);

            // Overwrite same host — latest write wins
            await repo.RecordHostDeployAsync(id, "production", "deploy-002", CancellationToken.None);
            var after2 = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.Equal("deploy-002", after2!.HostDeployIds["production"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task RecordHostDeploy_DifferentHosts_BothPersist()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var id = Guid.NewGuid().ToString("N");
            await repo.InsertAsync(MakeRow(id), CancellationToken.None);

            await repo.RecordHostDeployAsync(id, "host-a", "deploy-a", CancellationToken.None);
            await repo.RecordHostDeployAsync(id, "host-b", "deploy-b", CancellationToken.None);

            var fetched = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.NotNull(fetched);
            Assert.Equal(2, fetched!.HostDeployIds.Count);
            Assert.Equal("deploy-a", fetched.HostDeployIds["host-a"]);
            Assert.Equal("deploy-b", fetched.HostDeployIds["host-b"]);
        }
        finally { Cleanup(path); }
    }

    // --- ListInFlight ---

    [Fact]
    public async Task ListInFlight_ReturnsActivePhaseRowsOnly()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var now = DateTimeOffset.UtcNow;

            // Active phases that should appear in ListInFlight
            var activePhases = new[]
            {
                "initializing", "preflight", "deploying",
                "awaiting_all_soak", "rolling_back_all"
            };
            // Terminal / non-active phases that must NOT appear
            var terminalPhases = new[]
            {
                "all_succeeded", "partial_failure", "rolled_back", "group_failed"
            };

            foreach (var phase in activePhases)
            {
                var id = Guid.NewGuid().ToString("N");
                await repo.InsertAsync(MakeRow(id, phase: phase, startedAt: now), CancellationToken.None);
                if (phase != "initializing")
                {
                    // UpdatePhase to the target (initializing is the insert default)
                    await repo.UpdatePhaseAsync(id, phase, isTerminal: false, null, CancellationToken.None);
                }
            }
            foreach (var phase in terminalPhases)
            {
                var id = Guid.NewGuid().ToString("N");
                await repo.InsertAsync(MakeRow(id, phase: "initializing", startedAt: now), CancellationToken.None);
                await repo.UpdatePhaseAsync(id, phase, isTerminal: true, null, CancellationToken.None);
            }

            var inFlight = await repo.ListInFlightAsync(CancellationToken.None);

            Assert.Equal(activePhases.Length, inFlight.Count);
            var returnedPhases = inFlight.Select(r => r.Phase).ToHashSet();
            foreach (var p in activePhases)
                Assert.Contains(p, returnedPhases);
            foreach (var p in terminalPhases)
                Assert.DoesNotContain(p, returnedPhases);
        }
        finally { Cleanup(path); }
    }

    // --- ListForDomain ---

    [Fact]
    public async Task ListForDomain_OrdersByStartedAtDesc()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var base_ = DateTimeOffset.UtcNow;

            var oldest = MakeRow(Guid.NewGuid().ToString("N"), startedAt: base_.AddHours(-3));
            var middle = MakeRow(Guid.NewGuid().ToString("N"), startedAt: base_.AddHours(-1));
            var newest = MakeRow(Guid.NewGuid().ToString("N"), startedAt: base_);

            // Insert out of order deliberately
            await repo.InsertAsync(middle, CancellationToken.None);
            await repo.InsertAsync(oldest, CancellationToken.None);
            await repo.InsertAsync(newest, CancellationToken.None);

            var list = await repo.ListForDomainAsync("myapp.loc", limit: 10, CancellationToken.None);

            Assert.Equal(3, list.Count);
            Assert.Equal(newest.Id, list[0].Id);
            Assert.Equal(middle.Id, list[1].Id);
            Assert.Equal(oldest.Id, list[2].Id);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ListForDomain_RespectsLimit()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 5; i++)
            {
                await repo.InsertAsync(
                    MakeRow(Guid.NewGuid().ToString("N"), startedAt: now.AddMinutes(-i)),
                    CancellationToken.None);
            }

            var list = await repo.ListForDomainAsync("myapp.loc", limit: 3, CancellationToken.None);

            Assert.Equal(3, list.Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ListForDomain_FiltersToRequestedDomainOnly()
    {
        var (db, path) = NewDb();
        try
        {
            var repo = new DeployGroupsRepository(db);
            await repo.InsertAsync(
                MakeRow(Guid.NewGuid().ToString("N"), domain: "app-a.loc"), CancellationToken.None);
            await repo.InsertAsync(
                MakeRow(Guid.NewGuid().ToString("N"), domain: "app-b.loc"), CancellationToken.None);

            var list = await repo.ListForDomainAsync("app-a.loc", limit: 10, CancellationToken.None);

            Assert.Single(list);
            Assert.Equal("app-a.loc", list[0].Domain);
        }
        finally { Cleanup(path); }
    }
}
