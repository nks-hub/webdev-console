using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

public class DaemonCorsTests
{
    // Real Kestrel: its bare 500 for an unhandled exception is what dropped the headers.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_endpoint_that_throws_still_answers_with_CORS_headers_only_through_UseErrorsWithCors(bool withErrorHandler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(DaemonCors.IsAllowedOrigin).AllowAnyMethod().AllowAnyHeader()));
        await using var app = builder.Build();
        app.UseCors();
        if (withErrorHandler) app.UseErrorsWithCors();
        app.MapGet("/api/services", string () => throw new InvalidOperationException("No process is associated with this object."));
        await app.StartAsync();

        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, app.Urls.First() + "/api/services");
        req.Headers.Add("Origin", "app://nks-wdc");
        using var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal(withErrorHandler, res.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        if (withErrorHandler)
        {
            Assert.Equal("app://nks-wdc", Assert.Single(origins!));
            Assert.Contains("No process is associated", await res.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("app://nks-wdc", true)]
    [InlineData("http://127.0.0.1:5190", true)]
    [InlineData("http://localhost:5190", true)]
    [InlineData("http://[::1]:5191", true)]
    [InlineData("http://127.0.0.1:5199", true)]
    [InlineData("http://localhost:5173", false)]
    [InlineData("http://127.0.0.1:5200", false)]
    [InlineData("http://127.0.0.1:80", false)]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost:5190", false)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("http://localhost.evil.example.com:5190", false)]
    [InlineData("app://other-app", false)]
    [InlineData("null", false)]
    [InlineData("file://", false)]
    public void Only_the_app_and_the_vite_dev_server_origins_are_allowed(string origin, bool expected)
    {
        Assert.Equal(expected, DaemonCors.IsAllowedOrigin(origin));
    }
}
