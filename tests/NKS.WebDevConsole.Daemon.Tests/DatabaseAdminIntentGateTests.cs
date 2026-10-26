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
    }

    [Theory]
    [InlineData("mysql_root_reset")]
    [InlineData("mysql_user_drop")]
    [InlineData("postgres_root_reset")]
    public void New_kinds_are_registered_as_destructive_core_kinds(string id)
    {
        var kind = new DestructiveOperationKindsRegistry().Get(id);
        Assert.NotNull(kind);
        Assert.Equal(DangerLevel.Destructive, kind!.Danger);
        Assert.Equal("core", kind.PluginId);
    }
}
