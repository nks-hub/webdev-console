using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// <see cref="McpIntentGate"/> guards POST /api/services/{id}/config (kind
/// service_config_write): the MCP token always needs an approved intent, the
/// GUI token only when it sends one.
/// </summary>
public sealed class McpIntentGateTests : IDisposable
{
    private sealed class StubValidator(IntentValidationResult verdict) : IDeployIntentValidator
    {
        public List<(string Token, string Kind, string Domain, string Host, bool AllowUnconfirmed)> Calls { get; } = [];

        public Task<IntentValidationResult> ValidateAndConsumeAsync(
            string intentToken, string kind, string domain, string host, bool allowUnconfirmed, CancellationToken ct)
        {
            Calls.Add((intentToken, kind, domain, host, allowUnconfirmed));
            return Task.FromResult(verdict);
        }
    }

    private readonly bool _wasMcp = McpCallerContext.IsMcpToken;

    public void Dispose() => McpCallerContext.IsMcpToken = _wasMcp;

    private static DefaultHttpContext Ctx(string? token = null, string? allowUnconfirmed = null)
    {
        var ctx = new DefaultHttpContext();
        if (token is not null) ctx.Request.Headers["X-Intent-Token"] = token;
        if (allowUnconfirmed is not null) ctx.Request.Headers["X-Allow-Unconfirmed"] = allowUnconfirmed;
        return ctx;
    }

    private static Task<IResult?> Check(HttpContext ctx, IDeployIntentValidator v) =>
        McpIntentGate.CheckAsync(ctx, v, "service_config_write", "apache", "*svc*", CancellationToken.None);

    [Fact]
    public async Task Gui_token_without_header_skips_the_gate()
    {
        McpCallerContext.IsMcpToken = false;
        var v = new StubValidator(IntentValidationResult.Deny("missing_token"));
        Assert.Null(await Check(Ctx(), v));
        Assert.Empty(v.Calls);
    }

    [Fact]
    public async Task Mcp_token_without_header_is_refused_with_missing_token()
    {
        McpCallerContext.IsMcpToken = true;
        var v = new StubValidator(IntentValidationResult.Deny("missing_token"));
        var result = await Check(Ctx(), v);
        var json = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(403, json.StatusCode);
        Assert.Equal(("", "service_config_write", "apache", "*svc*", false), Assert.Single(v.Calls));
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        Assert.Equal("intent_rejected", value.GetType().GetProperty("error")!.GetValue(value));
        Assert.Equal("missing_token", value.GetType().GetProperty("reason")!.GetValue(value));
    }

    [Fact]
    public async Task Unapproved_intent_answers_425()
    {
        McpCallerContext.IsMcpToken = true;
        var v = new StubValidator(IntentValidationResult.Deny("pending_confirmation"));
        var result = await Check(Ctx("a.b.c"), v);
        Assert.Equal(425, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("a.b.c", v.Calls[0].Token);
    }

    [Fact]
    public async Task Approved_intent_lets_the_request_through()
    {
        McpCallerContext.IsMcpToken = true;
        var v = new StubValidator(IntentValidationResult.Allow());
        Assert.Null(await Check(Ctx("a.b.c"), v));
    }

    [Fact]
    public async Task Gui_token_with_header_is_validated_and_passes_allow_unconfirmed()
    {
        McpCallerContext.IsMcpToken = false;
        var v = new StubValidator(IntentValidationResult.Allow());
        Assert.Null(await Check(Ctx("a.b.c", "true"), v));
        Assert.True(v.Calls[0].AllowUnconfirmed);
    }

    [Fact]
    public void Service_config_write_is_a_registered_destructive_kind()
    {
        var kind = new DestructiveOperationKindsRegistry().Get("service_config_write");
        Assert.NotNull(kind);
        Assert.Equal(DangerLevel.Destructive, kind!.Danger);
    }
}
