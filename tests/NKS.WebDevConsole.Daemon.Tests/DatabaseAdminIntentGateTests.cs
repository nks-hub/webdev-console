using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The MySQL/PostgreSQL admin routes (root password reset, account drop)
/// need a GUI-approved intent from MCP-token callers, like every other
/// destructive route. The agent's own confirm: "YES" is not consent.
/// </summary>
public class DatabaseAdminIntentGateTests
{
    private static Mock<IDeployIntentValidator> Validator(IntentValidationResult result)
    {
        var v = new Mock<IDeployIntentValidator>();
        v.Setup(x => x.ValidateAndConsumeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return v;
    }

    private static int? StatusOf(IResult? r) => (r as IStatusCodeHttpResult)?.StatusCode;

    private static async Task<T> AsMcpToken<T>(Func<Task<T>> body)
    {
        McpCallerContext.IsMcpToken = true;
        try { return await body(); }
        finally { McpCallerContext.IsMcpToken = false; }
    }

    [Fact]
    public async Task Gui_calls_without_an_intent_header_pass_without_touching_the_validator()
    {
        var v = Validator(IntentValidationResult.Deny("not_found"));
        var result = await DatabaseEndpoints.IntentGateAsync(
            new DefaultHttpContext(), v.Object, DatabaseEndpoints.MySqlRootResetKind,
            DatabaseEndpoints.MySqlRootIntentDomain, DatabaseEndpoints.MySqlIntentHost);
        Assert.Null(result);
        v.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(DatabaseEndpoints.MySqlRootResetKind, DatabaseEndpoints.MySqlRootIntentDomain, DatabaseEndpoints.MySqlIntentHost)]
    [InlineData(DatabaseEndpoints.MySqlUserDropKind, "app@localhost", DatabaseEndpoints.MySqlIntentHost)]
    [InlineData(DatabaseEndpoints.MySqlUserPasswordKind, "app@localhost", DatabaseEndpoints.MySqlIntentHost)]
    [InlineData(DatabaseEndpoints.MySqlUserAdminKind, "app@%", DatabaseEndpoints.MySqlIntentHost)]
    [InlineData(DatabaseEndpoints.PostgresRootResetKind, DatabaseEndpoints.PostgresRootIntentDomain, DatabaseEndpoints.PostgresIntentHost)]
    public async Task Mcp_token_without_an_intent_is_refused(string kind, string domain, string host)
    {
        var v = Validator(IntentValidationResult.Deny("missing_token"));
        var result = await AsMcpToken(() => DatabaseEndpoints.IntentGateAsync(new DefaultHttpContext(), v.Object, kind, domain, host));
        Assert.Equal(403, StatusOf(result));
        v.Verify(x => x.ValidateAndConsumeAsync("", kind, domain, host, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_intent_awaiting_approval_answers_425()
    {
        var v = Validator(IntentValidationResult.Deny("pending_confirmation"));
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Intent-Token"] = "intent.token.sig";
        var result = await AsMcpToken(() => DatabaseEndpoints.IntentGateAsync(
            ctx, v.Object, DatabaseEndpoints.MySqlUserDropKind, "app@localhost", DatabaseEndpoints.MySqlIntentHost));
        Assert.Equal(425, StatusOf(result));
    }

    [Fact]
    public async Task An_approved_intent_lets_the_request_through()
    {
        var v = Validator(IntentValidationResult.Allow());
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Intent-Token"] = "intent.token.sig";
        var result = await AsMcpToken(() => DatabaseEndpoints.IntentGateAsync(
            ctx, v.Object, DatabaseEndpoints.MySqlUserDropKind,
            DatabaseEndpoints.MySqlUserIntentDomain("app", "localhost"), DatabaseEndpoints.MySqlIntentHost));
        Assert.Null(result);
        v.Verify(x => x.ValidateAndConsumeAsync(
            "intent.token.sig", "mysql_user_drop", "app@localhost", "*mysql*", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Any_caller_that_sends_an_intent_header_is_checked()
    {
        var v = Validator(IntentValidationResult.Deny("signature_mismatch"));
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Intent-Token"] = "bogus.fake.signature";
        var result = await DatabaseEndpoints.IntentGateAsync(
            ctx, v.Object, DatabaseEndpoints.PostgresRootResetKind,
            DatabaseEndpoints.PostgresRootIntentDomain, DatabaseEndpoints.PostgresIntentHost);
        Assert.Equal(403, StatusOf(result));
    }

    // services/mcp-server/src/tools/confirmation.ts mints intents with these triples.
    [Fact]
    public void Intent_scopes_match_the_mcp_confirmation_tool()
    {
        Assert.Equal("mysql_root_reset", DatabaseEndpoints.MySqlRootResetKind);
        Assert.Equal("root", DatabaseEndpoints.MySqlRootIntentDomain);
        Assert.Equal("mysql_user_drop", DatabaseEndpoints.MySqlUserDropKind);
        Assert.Equal("app@localhost", DatabaseEndpoints.MySqlUserIntentDomain("app", "localhost"));
        Assert.Equal("*mysql*", DatabaseEndpoints.MySqlIntentHost);
        Assert.Equal("postgres_root_reset", DatabaseEndpoints.PostgresRootResetKind);
        Assert.Equal("mysql_user_password", DatabaseEndpoints.MySqlUserPasswordKind);
        Assert.Equal("mysql_user_admin", DatabaseEndpoints.MySqlUserAdminKind);
    }

    [Theory]
    [InlineData("mysql_root_reset")]
    [InlineData("mysql_user_drop")]
    [InlineData("postgres_root_reset")]
    [InlineData("mysql_user_password")]
    [InlineData("mysql_user_admin")]
    public void New_kinds_are_registered_as_destructive_core_kinds(string id)
    {
        var kind = new DestructiveOperationKindsRegistry().Get(id);
        Assert.NotNull(kind);
        Assert.Equal(DangerLevel.Destructive, kind!.Danger);
        Assert.Equal("core", kind.PluginId);
    }

    private static JsonElement BodyOf(IResult? r) =>
        JsonSerializer.SerializeToElement(((IValueHttpResult)r!).Value);

    private static readonly Func<string, bool> CapabilityOff = _ => false;
    private static readonly Func<string, bool> DatabaseAdminOn = id => id == McpCapabilities.DatabaseAdmin;

    [Fact]
    public async Task Database_admin_off_answers_the_capability_payload_without_touching_the_validator()
    {
        var v = Validator(IntentValidationResult.Allow());
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Intent-Token"] = "intent.token.sig";
        var result = await AsMcpToken(() => DatabaseEndpoints.DatabaseAdminGateAsync(
            ctx, CapabilityOff, v.Object, DatabaseEndpoints.MySqlUserAdminKind, "app@%", DatabaseEndpoints.MySqlIntentHost, "detail"));

        Assert.Equal(403, StatusOf(result));
        var body = BodyOf(result);
        Assert.Equal("mcp_capability_disabled", body.GetProperty("error").GetString());
        Assert.Equal("database_admin", body.GetProperty("capability").GetString());
        Assert.Equal("mcp.capabilities.database_admin", body.GetProperty("settingKey").GetString());
        v.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Database_admin_on_still_needs_an_approved_intent_and_says_which()
    {
        var v = Validator(IntentValidationResult.Deny("missing_token"));
        var result = await AsMcpToken(() => DatabaseEndpoints.DatabaseAdminGateAsync(
            new DefaultHttpContext(), DatabaseAdminOn, v.Object, DatabaseEndpoints.MySqlUserAdminKind, "app@%", DatabaseEndpoints.MySqlIntentHost, "detail"));

        Assert.Equal(403, StatusOf(result));
        var body = BodyOf(result);
        Assert.Equal("intent_rejected", body.GetProperty("error").GetString());
        Assert.Equal("mysql_user_admin", body.GetProperty("intentKind").GetString());
        Assert.Equal("app@%", body.GetProperty("intentDomain").GetString());
        Assert.Contains("wdc_request_confirmation", body.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task Database_admin_gate_does_not_apply_to_the_gui()
    {
        var v = Validator(IntentValidationResult.Deny("missing_token"));
        Assert.Null(await DatabaseEndpoints.DatabaseAdminGateAsync(
            new DefaultHttpContext(), CapabilityOff, v.Object, DatabaseEndpoints.MySqlUserAdminKind, "app@%", DatabaseEndpoints.MySqlIntentHost, "detail"));
        v.VerifyNoOtherCalls();
    }

    // H2: read / readWrite on an ordinary database for a local account stays open.
    [Theory]
    [InlineData("localhost", null)]
    [InlineData("localhost", "read")]
    [InlineData("127.0.0.1", "readWrite")]
    [InlineData("::1", "none")]
    public async Task Local_accounts_without_admin_need_no_capability_or_intent(string host, string? preset)
    {
        var v = Validator(IntentValidationResult.Deny("missing_token"));
        Assert.Null(await AsMcpToken(() => DatabaseEndpoints.MySqlAccountGateAsync(
            new DefaultHttpContext(), CapabilityOff, v.Object, "app", host, preset)));
        v.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("localhost", "admin")]
    [InlineData("%", null)]
    [InlineData("%", "read")]
    [InlineData("10.0.0.%", "readWrite")]
    public async Task Admin_preset_or_non_local_host_needs_database_admin(string host, string? preset)
    {
        var v = Validator(IntentValidationResult.Allow());
        var off = await AsMcpToken(() => DatabaseEndpoints.MySqlAccountGateAsync(
            new DefaultHttpContext(), CapabilityOff, v.Object, "app", host, preset));
        Assert.Equal(403, StatusOf(off));
        Assert.Equal("database_admin", BodyOf(off).GetProperty("capability").GetString());
        v.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Admin_account_passes_with_database_admin_and_an_approved_intent_for_that_account()
    {
        var v = Validator(IntentValidationResult.Allow());
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Intent-Token"] = "intent.token.sig";
        Assert.Null(await AsMcpToken(() => DatabaseEndpoints.MySqlAccountGateAsync(ctx, DatabaseAdminOn, v.Object, "app", "%", "admin")));
        v.Verify(x => x.ValidateAndConsumeAsync(
            "intent.token.sig", "mysql_user_admin", "app@%", "*mysql*", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // M5: the data browser splices its filter into SQL; the MCP token gets none.
    [Fact]
    public async Task Browse_filter_is_refused_for_the_mcp_token_only()
    {
        var refused = await AsMcpToken(() => Task.FromResult(DatabaseEndpoints.McpBrowseFilterRejection("1=1) UNION SELECT authentication_string FROM mysql.user")));
        Assert.Equal(403, StatusOf(refused));
        Assert.Equal("mcp_filter_not_allowed", BodyOf(refused).GetProperty("error").GetString());

        Assert.Null(await AsMcpToken(() => Task.FromResult(DatabaseEndpoints.McpBrowseFilterRejection(null))));
        Assert.Null(await AsMcpToken(() => Task.FromResult(DatabaseEndpoints.McpBrowseFilterRejection("   "))));
        Assert.Null(DatabaseEndpoints.McpBrowseFilterRejection("id > 5"));
    }
}
