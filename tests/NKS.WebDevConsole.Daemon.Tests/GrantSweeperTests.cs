using Dapper;
using Microsoft.Data.Sqlite;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Phase 7.5+++ unit tests for <see cref="GrantSweeperService.SweepAsync"/>.
/// Targets the pure SQL helper so each test seeds a fresh
/// <c>mcp_session_grants</c> table with synthetic timestamps + a fixed
/// "now" without spinning the BackgroundService timer.
/// </summary>
public sealed class GrantSweeperTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly string _connectionString;

    public GrantSweeperTests()
    {
        // Use a file-backed DB. SQLite ":memory:" closes between connections,
        // and we want a single durable schema across the test's seed + sweep.
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"wdc-grant-sweep-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_tempDbPath}";
        SeedSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { /* best effort */ }
        try
        {
            var wal = _tempDbPath + "-wal";
            if (File.Exists(wal)) File.Delete(wal);
            var shm = _tempDbPath + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch { /* best effort */ }
    }

    private void SeedSchema()
    {
        // Minimal subset of migration 012 — enough for the sweep to target.
        // Real migration adds CHECK constraints + an index; not needed here.
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute(
            "CREATE TABLE mcp_session_grants (" +
            " id TEXT PRIMARY KEY," +
            " scope_type TEXT NOT NULL," +
            " scope_value TEXT," +
            " kind_pattern TEXT NOT NULL," +
            " target_pattern TEXT NOT NULL," +
            " granted_at TEXT NOT NULL," +
            " expires_at TEXT," +
            " revoked_at TEXT," +
            " granted_by TEXT," +
            " note TEXT" +
            ")");
    }

    private void Insert(string id, DateTimeOffset? expiresAt, DateTimeOffset? revokedAt)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute(
            "INSERT INTO mcp_session_grants (id, scope_type, kind_pattern, target_pattern, granted_at, expires_at, revoked_at) " +
            "VALUES (@Id, 'session', '*', '*', @GrantedAt, @ExpiresAt, @RevokedAt)",
            new
            {
                Id = id,
                GrantedAt = DateTimeOffset.UtcNow.AddDays(-100).ToString("o"),
                ExpiresAt = expiresAt?.ToString("o"),
                RevokedAt = revokedAt?.ToString("o"),
            });
    }

    private int Count()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn.QuerySingle<int>("SELECT COUNT(*) FROM mcp_session_grants");
    }

    private async Task<int> Sweep(DateTimeOffset now, TimeSpan expiredRet, TimeSpan revokedRet)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        return await GrantSweeperService.SweepAsync(conn, now, expiredRet, revokedRet);
    }

    [Fact]
    public async Task Sweep_DeletesNothing_WhenAllGrantsActive()
    {
        var now = DateTimeOffset.UtcNow;
        Insert("active1", now.AddHours(1), null); // not yet expired
        Insert("permanent", null, null);          // permanent
        var deleted = await Sweep(now, TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        Assert.Equal(0, deleted);
        Assert.Equal(2, Count());
    }

    [Fact]
    public async Task Sweep_DeletesExpiredGrant_PastGracePeriod()
    {
        var now = DateTimeOffset.UtcNow;
        Insert("expired", now.AddDays(-2), null); // expired >1 day ago
        var deleted = await Sweep(now, TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        Assert.Equal(1, deleted);
        Assert.Equal(0, Count());
    }

    [Fact]
    public async Task Sweep_KeepsExpiredGrant_WithinGracePeriod()
    {
        var now = DateTimeOffset.UtcNow;
        // Expired 12 hours ago — inside the 1-day grace window.
        Insert("expired-recent", now.AddHours(-12), null);
        var deleted = await Sweep(now, TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        Assert.Equal(0, deleted);
        Assert.Equal(1, Count());
    }

    [Fact]
    public async Task Sweep_NeverDeletesPermanentGrant_WithoutRevoke()
    {
        var now = DateTimeOffset.UtcNow;
        Insert("permanent", null, null); // expires_at IS NULL → never expires
        var deleted = await Sweep(now, TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        Assert.Equal(0, deleted);
        Assert.Equal(1, Count());
    }

    [Fact]
    public async Task Sweep_DeletesRevokedGrant_PastAuditWindow()
    {
        var now = DateTimeOffset.UtcNow;
        Insert("old-revoked", now.AddDays(-100), now.AddDays(-31)); // revoked >30 days ago
        var deleted = await Sweep(now, TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        Assert.Equal(1, deleted);
        Assert.Equal(0, Count());
    }

    [Fact]
    public async Task Sweep_KeepsRevokedGrant_WithinAuditWindow()
    {
        var now = DateTimeOffset.UtcNow;
        Insert("recent-revoked", now.AddDays(-100), now.AddDays(-15)); // revoked 15 days ago
        var deleted = await Sweep(now, TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        Assert.Equal(0, deleted);
        Assert.Equal(1, Count());
    }

    [Fact]
    public async Task Sweep_DeletesPermanentGrant_OnceRevokedPastAuditWindow()
    {
        var now = DateTimeOffset.UtcNow;
        // Permanent grant explicitly revoked 31 days ago — passes the
        // revoked branch (expires_at IS NULL doesn't block this branch).
        Insert("perm-revoked", null, now.AddDays(-31));
        var deleted = await Sweep(now, TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        Assert.Equal(1, deleted);
        Assert.Equal(0, Count());
    }

    [Fact]
    public async Task Sweep_OnlyDeletesMatchingRows_InMixedTable()
    {
        var now = DateTimeOffset.UtcNow;
        Insert("active",         now.AddHours(1), null);
        Insert("permanent",      null, null);
        Insert("expired-recent", now.AddHours(-1), null);     // grace
        Insert("expired-old",    now.AddDays(-2), null);       // sweep
        Insert("revoked-recent", now.AddDays(-100), now.AddDays(-15));
        Insert("revoked-old",    now.AddDays(-100), now.AddDays(-31)); // sweep

        var deleted = await Sweep(now, TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        Assert.Equal(2, deleted);
        Assert.Equal(4, Count());
    }

    [Fact]
    public async Task Sweep_ReturnsZero_WhenTableMissing()
    {
        // Drop the table so the sweep query fails with SQLITE_ERROR.
        // The static helper should swallow it (fresh DB compat) and
        // report 0 deletions rather than throwing.
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            conn.Execute("DROP TABLE mcp_session_grants");
        }
        var deleted = await Sweep(DateTimeOffset.UtcNow,
            TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        Assert.Equal(0, deleted);
    }
}
