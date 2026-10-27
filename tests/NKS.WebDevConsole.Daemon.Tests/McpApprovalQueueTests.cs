using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Daemon side of the GUI approval queue: the columns that let the GUI
/// reload pending requests, wildcard grant targets used by the local-dev
/// onboarding preset, and the "consumed" event that closes stale banners.
/// Runs against the real migrations.
/// </summary>
public sealed class McpApprovalQueueTests : IDisposable
{
    private static readonly IntentSigner Signer = new();

    private readonly string _dbPath;
    private readonly Database _db;
    private readonly McpSessionGrantsRepository _grants;

    public McpApprovalQueueTests()
    {
        McpCallerContext.Clear();
        _dbPath = Path.Combine(Path.GetTempPath(), $"wdc-approval-queue-{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        Assert.True(new MigrationRunner(NullLogger<MigrationRunner>.Instance).Run(_db.ConnectionString));
        _grants = new McpSessionGrantsRepository(_db);
    }

    public void Dispose()
    {
        McpCallerContext.Clear();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }
    }

    private Task<string> AddAlwaysGrant(string kind, string target) =>
        _grants.InsertAsync(new McpSessionGrantRow(
            Id: null, ScopeType: "always", ScopeValue: null,
            KindPattern: kind, TargetPattern: target,
            GrantedAt: "", ExpiresAt: null, GrantedBy: "test", RevokedAt: null, Note: null),
            CancellationToken.None);

    private Task<McpSessionGrantRow?> Find(string kind, string target) =>
        _grants.FindMatchingActiveAsync(null, null, null, kind, target, CancellationToken.None);

    [Fact]
    public void Migration_AddsConfirmRequestColumns()
    {
        using var conn = _db.CreateConnection();
        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('deploy_intents')").ToList();
        Assert.Contains("confirm_requested_at", columns);
        Assert.Contains("prompt", columns);
    }

    [Theory]
    [InlineData("shop_dev", true)]
    [InlineData("SHOP_DEV", true)]
    [InlineData("_dev", true)]
    [InlineData("shop_prod", false)]
    [InlineData("shop_dev_backup", false)]
    public async Task WildcardTargetPattern_MatchesSuffix(string database, bool expected)
    {
        await AddAlwaysGrant("database_drop", "*_dev");
        var grant = await Find("database_drop", database);
        Assert.Equal(expected, grant is not null);
    }

    [Fact]
    public async Task WildcardTargetPattern_TreatsGlobMetacharactersLiterally()
    {
        await AddAlwaysGrant("database_drop", "a?[b]*");
        Assert.NotNull(await Find("database_drop", "a?[b]_dev"));
        Assert.Null(await Find("database_drop", "ax[b]_dev"));
        Assert.Null(await Find("database_drop", "a?b_dev"));
    }

    [Fact]
    public async Task ExactTargetPattern_StillRequiresExactMatch()
    {
        await AddAlwaysGrant("database_drop", "shop_dev");
        Assert.NotNull(await Find("database_drop", "shop_dev"));
        Assert.Null(await Find("database_drop", "blog_dev"));
    }

    [Fact]
    public async Task ConsumedIntent_IsAnnouncedSoBannersClose()
    {
        var intentId = Guid.NewGuid().ToString("D");
        var nonce = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var signature = Signer.Sign(IntentSigner.Canonicalize(intentId, "apache", "*svc*", "service_restart", nonce, expiresAt, null));
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO deploy_intents (id, domain, host, kind, nonce, expires_at, hmac_signature, confirmed_at) " +
                "VALUES (@Id, 'apache', '*svc*', 'service_restart', @Nonce, @Exp, @Sig, @Now)",
                new { Id = intentId, Nonce = nonce, Exp = expiresAt.ToString("o"), Sig = signature, Now = DateTimeOffset.UtcNow.ToString("o") });
        }

        var events = new Mock<IDeployEventBroadcaster>();
        var validator = new DeployIntentValidator(_db, Signer, _grants);
        validator.AttachEvents(events.Object);

        var result = await validator.ValidateAndConsumeAsync(
            $"{intentId}.{nonce}.{signature}", "service_restart", "apache", "*svc*",
            allowUnconfirmed: false, CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        events.Verify(e => e.BroadcastAsync("mcp:intent-changed",
            It.Is<object>(p => p.ToString()!.Contains("consumed") && p.ToString()!.Contains(intentId))), Times.Once);
    }
}
