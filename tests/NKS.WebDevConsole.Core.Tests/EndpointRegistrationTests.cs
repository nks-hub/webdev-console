using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Core.Tests;

public class EndpointRegistrationTests
{
    [Fact]
    public void MapGet_GeneratesCorrectPath()
    {
        var reg = new EndpointRegistration("apache");
        Delegate handler = () => "ok";
        reg.MapGet("status", handler);

        Assert.Single(reg.Endpoints);
        Assert.Equal("GET", reg.Endpoints[0].Method);
        Assert.Equal("/api/apache/status", reg.Endpoints[0].Path);
        Assert.Same(handler, reg.Endpoints[0].Handler);
    }

    [Fact]
    public void MapPost_GeneratesCorrectPath()
    {
        var reg = new EndpointRegistration("mysql");
        Delegate handler = () => "created";
        reg.MapPost("databases", handler);

        Assert.Single(reg.Endpoints);
        Assert.Equal("POST", reg.Endpoints[0].Method);
        Assert.Equal("/api/mysql/databases", reg.Endpoints[0].Path);
    }

    [Fact]
    public void MapPut_GeneratesCorrectPath()
    {
        var reg = new EndpointRegistration("php");
        Delegate handler = () => "updated";
        reg.MapPut("config", handler);

        Assert.Single(reg.Endpoints);
        Assert.Equal("PUT", reg.Endpoints[0].Method);
        Assert.Equal("/api/php/config", reg.Endpoints[0].Path);
    }

    [Fact]
    public void MapDelete_GeneratesCorrectPath()
    {
        var reg = new EndpointRegistration("redis");
        Delegate handler = () => "deleted";
        reg.MapDelete("keys/all", handler);

        Assert.Single(reg.Endpoints);
        Assert.Equal("DELETE", reg.Endpoints[0].Method);
        Assert.Equal("/api/redis/keys/all", reg.Endpoints[0].Path);
    }

    [Fact]
    public void Path_LeadingSlash_IsTrimmed()
    {
        var reg = new EndpointRegistration("nginx");
        reg.MapGet("/health", () => "ok");

        Assert.Equal("/api/nginx/health", reg.Endpoints[0].Path);
    }

    [Fact]
    public void MultipleEndpoints_AreAccumulated()
    {
        var reg = new EndpointRegistration("apache");
        reg.MapGet("status", () => "ok")
           .MapPost("start", () => "started")
           .MapPost("stop", () => "stopped")
           .MapDelete("cache", () => "cleared");

        Assert.Equal(4, reg.Endpoints.Count);
        Assert.Equal("GET", reg.Endpoints[0].Method);
        Assert.Equal("POST", reg.Endpoints[1].Method);
        Assert.Equal("POST", reg.Endpoints[2].Method);
        Assert.Equal("DELETE", reg.Endpoints[3].Method);
    }

    [Fact]
    public void Endpoints_IsReadOnly()
    {
        var reg = new EndpointRegistration("test");
        Assert.IsAssignableFrom<IReadOnlyList<PluginEndpoint>>(reg.Endpoints);
    }

    [Fact]
    public void NewRegistration_HasNoEndpoints()
    {
        var reg = new EndpointRegistration("empty");
        Assert.Empty(reg.Endpoints);
    }

    [Fact]
    public void MapGet_EmptyPath_UsesPluginRoot()
    {
        var reg = new EndpointRegistration("redis");
        reg.MapGet("", () => "ok");
        Assert.Equal("/api/redis/", reg.Endpoints[0].Path);
    }

    [Fact]
    public void MapGet_NestedPath_ComposesCorrectly()
    {
        var reg = new EndpointRegistration("mysql");
        reg.MapGet("databases/{name}/tables", () => "ok");
        Assert.Equal("/api/mysql/databases/{name}/tables", reg.Endpoints[0].Path);
    }

    [Fact]
    public void FluentChaining_ReturnsRegistration()
    {
        var reg = new EndpointRegistration("test");
        var result = reg.MapGet("a", () => "ok").MapPost("b", () => "ok");
        Assert.Same(reg, result);
    }
}
