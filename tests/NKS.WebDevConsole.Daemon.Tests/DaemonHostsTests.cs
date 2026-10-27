using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// DNS-rebinding defence: the daemon answers only Host localhost, 127.0.0.1
/// and [::1] (any port), even with AllowedHosts "*" in configuration.
/// </summary>
public class DaemonHostsTests
{
    [Theory]
    [InlineData("localhost:17280", HttpStatusCode.OK)]
    [InlineData("127.0.0.1:17281", HttpStatusCode.OK)]
    [InlineData("[::1]:17280", HttpStatusCode.OK)]
    [InlineData("LOCALHOST", HttpStatusCode.OK)]
    [InlineData("evil.example.com", HttpStatusCode.BadRequest)]
    [InlineData("rebind.attacker.test:17280", HttpStatusCode.BadRequest)]
    [InlineData("127.0.0.1.nip.io:17280", HttpStatusCode.BadRequest)]
    [InlineData("192.168.1.10:17280", HttpStatusCode.BadRequest)]
    public async Task Only_loopback_host_names_are_answered(string host, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration["AllowedHosts"] = "*";
        builder.Services.AddDaemonHostFiltering();
        await using var app = builder.Build();
        app.MapGet("/healthz", () => "ok");
        await app.StartAsync();

        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, app.Urls.First() + "/healthz");
        req.Headers.Host = host;
        using var res = await client.SendAsync(req);
        Assert.Equal(expected, res.StatusCode);
    }
}
