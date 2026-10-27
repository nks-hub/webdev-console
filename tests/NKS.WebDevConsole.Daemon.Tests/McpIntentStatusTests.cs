using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// <see cref="McpIntentStatus"/> backs <c>GET /api/mcp/intents/{id}</c>, which
/// the MCP agent polls while the user decides on an approval request. The
/// schema comes from the real migrations so a renamed column fails here.
/// </summary>
public sealed class McpIntentStatusTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-06-01T12:00:00Z");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nks-intent-status-{Guid.NewGuid():N}.db");
    private readonly Database _db;

    public McpIntentStatusTests()
    {
        Assert.True(new MigrationRunner(NullLogger<MigrationRunner>.Instance).Run($"Data Source={_dbPath}"));
        _db = new Database(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private string Insert(string expiresAt, string? confirmedAt = null, string? usedAt = null, string? revokedAt = null)
    {
        var id = Guid.NewGuid().ToString("D");
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO deploy_intents (id, domain, host, kind, nonce, expires_at, hmac_signature, confirmed_at, used_at, revoked_at) " +
            "VALUES (@Id, 'shop.loc', '*site*', 'site_delete', @Nonce, @ExpiresAt, 'sig', @ConfirmedAt, @UsedAt, @RevokedAt)",
            new { Id = id, Nonce = Guid.NewGuid().ToString("N"), ExpiresAt = expiresAt, ConfirmedAt = confirmedAt, UsedAt = usedAt, RevokedAt = revokedAt });
        return id;
    }

    private async Task<McpIntentStatus?> Load(string id)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync();
        return await McpIntentStatus.LoadAsync(conn, id, Now);
    }

    [Fact]
    public async Task Unapproved_intent_is_pending_confirmation()
    {
        var status = await Load(Insert("2030-06-01T12:10:00Z"));
        Assert.NotNull(status);
        Assert.Equal("pending_confirmation", status!.State);
        Assert.Equal("site_delete", status.Kind);
        Assert.Equal("shop.loc", status.Domain);
        Assert.Equal("*site*", status.Host);
        Assert.Equal("2030-06-01T12:10:00Z", status.ExpiresAt);
    }

    [Fact]
    public async Task Approved_intent_is_ready()
    {
        var status = await Load(Insert("2030-06-01T12:10:00Z", confirmedAt: "2030-06-01T11:59:00Z"));
        Assert.Equal("ready", status!.State);
        Assert.Equal("2030-06-01T11:59:00Z", status.ConfirmedAt);
    }

    [Fact]
    public async Task Used_intent_is_consumed()
    {
        var status = await Load(Insert("2030-06-01T12:10:00Z", confirmedAt: "2030-06-01T11:59:00Z", usedAt: "2030-06-01T11:59:30Z"));
        Assert.Equal("consumed", status!.State);
    }

    [Fact]
    public async Task Revoked_intent_is_revoked_even_though_used_at_is_set()
    {
        var status = await Load(Insert("2030-06-01T12:10:00Z", usedAt: "2030-06-01T11:58:00Z", revokedAt: "2030-06-01T11:58:00Z"));
        Assert.Equal("revoked", status!.State);
        Assert.Equal("2030-06-01T11:58:00Z", status.RevokedAt);
    }

    [Fact]
    public async Task Intent_past_its_expiry_is_expired()
    {
        var status = await Load(Insert("2030-06-01T11:00:00Z", confirmedAt: "2030-06-01T10:59:00Z"));
        Assert.Equal("expired", status!.State);
    }

    [Fact]
    public async Task Unknown_id_returns_null()
    {
        Assert.Null(await Load(Guid.NewGuid().ToString("D")));
    }

    [Theory]
    [InlineData(null, null, null, "2030-06-01T12:10:00Z", "pending_confirmation")]
    [InlineData(null, null, "x", "2030-06-01T12:10:00Z", "ready")]
    [InlineData("x", null, "x", "2030-06-01T12:10:00Z", "consumed")]
    [InlineData("x", "x", null, "2030-06-01T12:10:00Z", "revoked")]
    [InlineData("x", "x", null, "2030-06-01T11:00:00Z", "revoked")]
    [InlineData(null, null, null, "2030-06-01T11:00:00Z", "expired")]
    public void ComputeState_orders_revoked_before_consumed_before_expired(
        string? usedAt, string? revokedAt, string? confirmedAt, string expiresAt, string expected)
    {
        Assert.Equal(expected, McpIntentStatus.ComputeState(usedAt, revokedAt, confirmedAt, expiresAt, Now));
    }
}
