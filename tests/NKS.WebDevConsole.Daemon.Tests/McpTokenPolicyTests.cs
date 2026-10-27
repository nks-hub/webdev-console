using System.Text;
using Microsoft.AspNetCore.Http;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

public class McpTokenPolicyTests
{
    private static HttpContext Request(string method, string path, string? jsonBody = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (jsonBody is not null)
        {
            ctx.Request.ContentType = "application/json";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));
        }
        return ctx;
    }

    [Theory]
    [InlineData("POST", "/api/mcp/intents/abc/confirm")]
    [InlineData("POST", "/api/mcp/grants")]
    [InlineData("PATCH", "/api/mcp/grants/g1")]
    [InlineData("DELETE", "/api/mcp/grants/g1")]
    [InlineData("POST", "/api/admin/restart")]
    public async Task Self_approval_and_admin_routes_are_refused(string method, string path)
    {
        Assert.NotNull(await McpTokenPolicy.DenyReasonAsync(Request(method, path)));
    }

    [Theory]
    [InlineData("POST", "/api/mcp/intents")]
    [InlineData("POST", "/api/mcp/intents/confirm-request")]
    [InlineData("GET", "/api/mcp/grants")]
    [InlineData("GET", "/api/sites")]
    [InlineData("DELETE", "/api/sites/blog.test")]
    public async Task Requesting_intents_and_ordinary_routes_pass_the_policy(string method, string path)
    {
        Assert.Null(await McpTokenPolicy.DenyReasonAsync(Request(method, path)));
    }

    [Fact]
    public async Task Protected_settings_are_refused_and_the_body_stays_readable()
    {
        var ctx = Request("PUT", "/api/settings", """{"ports.http":"8080","daemon.catalogUrl":"https://evil.example.com"}""");
        var reason = await McpTokenPolicy.DenyReasonAsync(ctx);
        Assert.NotNull(reason);
        Assert.Contains("daemon.catalogUrl", reason);
    }

    [Fact]
    public async Task Ordinary_settings_pass_and_the_body_is_rewound()
    {
        var ctx = Request("PUT", "/api/settings", """{"ports.http":"8080"}""");
        Assert.Null(await McpTokenPolicy.DenyReasonAsync(ctx));
        using var reader = new StreamReader(ctx.Request.Body);
        Assert.Equal("""{"ports.http":"8080"}""", await reader.ReadToEndAsync());
    }
}
