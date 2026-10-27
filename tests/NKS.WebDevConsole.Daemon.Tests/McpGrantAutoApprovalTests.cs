using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// End-to-end grant matching for MCP-token callers: real migrations, the
/// real <see cref="McpSessionGrantsRepository"/>, the real validator, and
/// caller identity populated from request headers exactly as the daemon's
/// pipeline does (<see cref="McpCallerContext.ApplyIdentityHeaders"/> plus
/// the auth middleware marking the request as MCP-token authenticated).
/// </summary>
public sealed class McpGrantAutoApprovalTests : IDisposable
{
    private static readonly IntentSigner Signer = new();

    private readonly string _dbPath;
    private readonly Database _db;
    private readonly McpSessionGrantsRepository _grants;
    private readonly HashSet<string> _alwaysConfirmKinds = new(StringComparer.OrdinalIgnoreCase);

    public McpGrantAutoApprovalTests()
    {
        McpCallerContext.Clear();
        _dbPath = Path.Combine(Path.GetTempPath(), $"wdc-grant-auto-{Guid.NewGuid():N}.db");
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

    private DeployIntentValidator NewValidator() => new(
        _db, Signer, _grants, new DestructiveOperationKindsRegistry(), () => false,
        () => _alwaysConfirmKinds);

    /// <summary>Simulates an /api request: X-Mcp-* headers + which token authenticated.</summary>
    private static void SimulateRequest(bool mcpToken, string? sessionId = null, string? instanceId = null, string? apiKeyId = null)
    {
        var ctx = new DefaultHttpContext();
        if (sessionId is not null) ctx.Request.Headers["X-Mcp-Session-Id"] = sessionId;
        if (instanceId is not null) ctx.Request.Headers["X-Mcp-Instance-Id"] = instanceId;
        if (apiKeyId is not null) ctx.Request.Headers["X-Mcp-Api-Key-Id"] = apiKeyId;
        McpCallerContext.ApplyIdentityHeaders(ctx.Request.Headers);
        McpCallerContext.IsMcpToken = mcpToken;
    }

    private string InsertPendingIntent(string kind, string domain, string host, out string intentId)
    {
        intentId = Guid.NewGuid().ToString("D");
        var nonce = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var signature = Signer.Sign(IntentSigner.Canonicalize(intentId, domain, host, kind, nonce, expiresAt, null));
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO deploy_intents (id, domain, host, release_id, kind, nonce, expires_at, hmac_signature) " +
            "VALUES (@Id, @Domain, @Host, NULL, @Kind, @Nonce, @ExpiresAt, @Sig)",
            new { Id = intentId, Domain = domain, Host = host, Kind = kind, Nonce = nonce, ExpiresAt = expiresAt.ToString("o"), Sig = signature });
        return $"{intentId}.{nonce}.{signature}";
    }

    private Task<string> AddGrant(string scopeType, string? scopeValue, string kind = "*", string target = "*") =>
        _grants.InsertAsync(new McpSessionGrantRow(
            Id: null, ScopeType: scopeType, ScopeValue: scopeValue,
            KindPattern: kind, TargetPattern: target,
            GrantedAt: "", ExpiresAt: null, GrantedBy: "test", RevokedAt: null, Note: null),
            CancellationToken.None);

    private (string? ConfirmedAt, string? MatchedGrantId, string? UsedAt) ReadIntent(string intentId)
    {
        using var conn = _db.CreateConnection();
        var row = conn.QuerySingle<(string?, string?, string?)>(
            "SELECT confirmed_at, matched_grant_id, used_at FROM deploy_intents WHERE id = @Id", new { Id = intentId });
        return row;
    }

    private int MatchCount(string grantId)
    {
        using var conn = _db.CreateConnection();
        return conn.QuerySingle<int>("SELECT match_count FROM mcp_session_grants WHERE id = @Id", new { Id = grantId });
    }

    [Fact]
    public async Task AlwaysGrant_AutoApproves_McpTokenRequest_WithoutIdentityHeaders()
    {
        var grantId = await AddGrant("always", null, kind: "service_restart", target: "apache");
        var token = InsertPendingIntent("service_restart", "apache", "*svc*", out var intentId);

        SimulateRequest(mcpToken: true);
        var result = await NewValidator().ValidateAndConsumeAsync(
            token, "service_restart", "apache", "*svc*", allowUnconfirmed: false, CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        var row = ReadIntent(intentId);
        Assert.NotNull(row.ConfirmedAt);
        Assert.Equal(grantId, row.MatchedGrantId);
        Assert.NotNull(row.UsedAt);
        Assert.Equal(1, MatchCount(grantId));
    }

    [Fact]
    public async Task SessionGrant_AutoApproves_McpTokenRequest_WithMatchingSessionHeader()
    {
        var grantId = await AddGrant("session", "agent-run-1", kind: "database_drop");
        var token = InsertPendingIntent("database_drop", "shop_dev", "*db*", out var intentId);

        SimulateRequest(mcpToken: true, sessionId: "agent-run-1", instanceId: "install-1", apiKeyId: "client:claude-code");
        var result = await NewValidator().ValidateAndConsumeAsync(
            token, "database_drop", "shop_dev", "*db*", allowUnconfirmed: false, CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(grantId, ReadIntent(intentId).MatchedGrantId);
    }

    [Fact]
    public async Task SessionGrant_DoesNotApply_ToAnotherSession()
    {
        await AddGrant("session", "agent-run-1");
        var token = InsertPendingIntent("database_drop", "shop_dev", "*db*", out var intentId);

        SimulateRequest(mcpToken: true, sessionId: "agent-run-2");
        var result = await NewValidator().ValidateAndConsumeAsync(
            token, "database_drop", "shop_dev", "*db*", allowUnconfirmed: false, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("pending_confirmation", result.Reason);
        Assert.Null(ReadIntent(intentId).ConfirmedAt);
    }

    [Fact]
    public async Task AlwaysGrant_IsNotConsulted_ForGuiTokenCallerWithoutIdentity()
    {
        await AddGrant("always", null);
        var token = InsertPendingIntent("service_restart", "apache", "*svc*", out _);

        SimulateRequest(mcpToken: false);
        var result = await NewValidator().ValidateAndConsumeAsync(
            token, "service_restart", "apache", "*svc*", allowUnconfirmed: false, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("pending_confirmation", result.Reason);
    }

    [Fact]
    public async Task AlwaysConfirmKind_OverridesMatchingGrant()
    {
        await AddGrant("always", null);
        _alwaysConfirmKinds.Add("backup_restore");
        var token = InsertPendingIntent("backup_restore", "*backup*", "*backup*", out _);

        SimulateRequest(mcpToken: true);
        var result = await NewValidator().ValidateAndConsumeAsync(
            token, "backup_restore", "*backup*", "*backup*", allowUnconfirmed: false, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("pending_confirmation", result.Reason);
        Assert.Equal("always_confirm", result.Detail);
    }

    [Fact]
    public async Task ConfirmRequest_AutoApprovesUpFront_AndConsumeDoesNotMatchTwice()
    {
        var grantId = await AddGrant("always", null, kind: "service_restart");
        var token = InsertPendingIntent("service_restart", "mysql", "*svc*", out var intentId);
        var validator = NewValidator();

        SimulateRequest(mcpToken: true, sessionId: "agent-run-1");
        var approval = await validator.TryAutoApproveAsync(intentId, CancellationToken.None);

        Assert.True(approval.Confirmed);
        Assert.True(approval.AutoApproved);
        Assert.Equal(grantId, approval.MatchedGrantId);
        var row = ReadIntent(intentId);
        Assert.NotNull(row.ConfirmedAt);
        Assert.Null(row.UsedAt);
        Assert.Equal(grantId, row.MatchedGrantId);

        var result = await validator.ValidateAndConsumeAsync(
            token, "service_restart", "mysql", "*svc*", allowUnconfirmed: false, CancellationToken.None);
        Assert.True(result.Ok, result.Reason);
        Assert.Equal(1, MatchCount(grantId));
    }

    [Fact]
    public async Task ConfirmRequest_WithoutMatchingGrant_NeedsOperator()
    {
        await AddGrant("always", null, kind: "service_restart");
        InsertPendingIntent("database_drop", "shop", "*db*", out var intentId);

        SimulateRequest(mcpToken: true);
        var approval = await NewValidator().TryAutoApproveAsync(intentId, CancellationToken.None);

        Assert.False(approval.Confirmed);
        Assert.False(approval.AutoApproved);
        Assert.Null(approval.Reason);
        Assert.Null(ReadIntent(intentId).ConfirmedAt);
    }

    [Fact]
    public async Task ConfirmRequest_UnknownIntent_ReportsNotFound()
    {
        SimulateRequest(mcpToken: true);
        var approval = await NewValidator().TryAutoApproveAsync(Guid.NewGuid().ToString("D"), CancellationToken.None);
        Assert.False(approval.Confirmed);
        Assert.Equal("not_found", approval.Reason);
    }

    [Fact]
    public void IdentityHeaders_AreTrimmed_AndOverlongValuesIgnored()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Mcp-Session-Id"] = "  abc  ";
        ctx.Request.Headers["X-Mcp-Instance-Id"] = new string('x', McpCallerContext.MaxIdentityLength + 1);
        McpCallerContext.ApplyIdentityHeaders(ctx.Request.Headers);

        Assert.Equal("abc", McpCallerContext.SessionId);
        Assert.Null(McpCallerContext.InstanceId);
        Assert.Null(McpCallerContext.ApiKeyId);
        Assert.True(McpCallerContext.HasAnyIdentity);
    }
}
