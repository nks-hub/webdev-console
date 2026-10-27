using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// With mcp.enabled off every /api/mcp/* route answers 404 mcp_disabled.
/// The body carries a hint so the agent (and the person reading its error)
/// learns how to turn MCP on instead of seeing a bare error code.
/// </summary>
public sealed class McpDisabledHintTests
{
    [Fact]
    public async Task McpDisabled_Returns404_WithErrorCodeAndEnableHint()
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        ctx.Response.Body = new MemoryStream();

        await McpEndpoints.McpDisabled().ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal("mcp_disabled", doc.RootElement.GetProperty("error").GetString());
        var hint = doc.RootElement.GetProperty("hint").GetString();
        Assert.Equal(McpEndpoints.McpDisabledHint, hint);
        Assert.Contains("Settings", hint);
        Assert.Contains("MCP", hint);
    }

    [Fact]
    public void EveryMcpDisabledResponse_UsesTheHintHelper()
    {
        // Guard against a new route reintroducing the bare 404 body.
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "daemon", "NKS.WebDevConsole.Daemon", "Mcp", "McpEndpoints.cs"));
        Assert.False(source.Contains("new { error = \"mcp_disabled\" }"),
            "A /api/mcp route returns a bare mcp_disabled body; use `return McpDisabled();` so it carries the enable hint.");
        Assert.Contains("return McpDisabled();", source);
    }
}
