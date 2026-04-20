using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Daemon.Plugin;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="PluginCatalogClient"/>. The HTTP shape mirrors the
/// <c>plugins_catalog.py</c> generator contract in wdc-catalog-api:
/// <c>{ schema, plugin_count, plugins: [{ id, releases: [{ version, downloads: [{ url }] }] }] }</c>.
/// We verify parsing, base-URL provider resolution, and graceful failure on
/// non-200 responses (cache stays empty, no exception propagates).
/// </summary>
public sealed class PluginCatalogClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }
        public string? LastRequestUri { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static PluginCatalogClient MakeClient(
        HttpStatusCode status,
        string? body,
        Func<string>? baseUrlProvider,
        out StubHandler handler)
    {
        handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? "")
        });
        var factory = new StubFactory(handler);
        var logger = new Mock<ILogger<PluginCatalogClient>>();
        return new PluginCatalogClient(factory, logger.Object, baseUrlProvider);
    }

    [Fact]
    public async Task RefreshAsync_ParsesCatalogJson()
    {
        const string body = """
        {
          "schema": "wdc-plugins/v1",
          "plugin_count": 2,
          "plugins": [
            {
              "id": "nks.wdc.apache",
              "releases": [
                { "version": "1.0.0", "major_minor": "1.0", "downloads": [
                  { "url": "https://example/apache-1.0.0.zip", "os": "any", "arch": "any", "archive_type": "zip" }
                ] }
              ]
            },
            {
              "id": "nks.wdc.nginx",
              "releases": [
                { "version": "0.1.0", "downloads": [
                  { "url": "https://example/nginx-0.1.0.zip", "archive_type": "zip" }
                ] }
              ]
            }
          ]
        }
        """;
        var client = MakeClient(HttpStatusCode.OK, body, null, out _);
        var count = await client.RefreshAsync();
        Assert.Equal(2, count);
        Assert.Equal(2, client.Cached.Count);
        Assert.Contains(client.Cached, p => p.Id == "nks.wdc.apache");
        Assert.Contains(client.Cached, p => p.Id == "nks.wdc.nginx");
    }

    [Fact]
    public async Task RefreshAsync_ServerError_LeavesCacheEmpty_NoThrow()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError, null, null, out _);
        var count = await client.RefreshAsync();
        Assert.Equal(0, count);
        Assert.Empty(client.Cached);
    }

    [Fact]
    public async Task LatestRelease_ReturnsFirstReleaseForMatchingId()
    {
        const string body = """
        {
          "plugins": [
            { "id": "nks.wdc.apache", "releases": [
              { "version": "1.2.0", "downloads": [{ "url": "u", "archive_type": "zip" }] },
              { "version": "1.1.0", "downloads": [{ "url": "u", "archive_type": "zip" }] }
            ]}
          ]
        }
        """;
        var client = MakeClient(HttpStatusCode.OK, body, null, out _);
        await client.RefreshAsync();
        var latest = client.LatestRelease("nks.wdc.apache");
        Assert.NotNull(latest);
        Assert.Equal("1.2.0", latest!.Version);
    }

    [Fact]
    public async Task LatestRelease_UnknownPluginId_ReturnsNull()
    {
        var client = MakeClient(HttpStatusCode.OK, """{"plugins":[]}""", null, out _);
        await client.RefreshAsync();
        Assert.Null(client.LatestRelease("does.not.exist"));
    }

    [Fact]
    public async Task RefreshAsync_UsesBaseUrlProviderOnEveryCall()
    {
        var currentUrl = "https://first.example";
        var client = MakeClient(HttpStatusCode.OK, """{"plugins":[]}""", () => currentUrl, out var handler);
        await client.RefreshAsync();
        Assert.Equal("https://first.example/api/v1/plugins/catalog", handler.LastRequestUri);

        currentUrl = "https://second.example";
        await client.RefreshAsync();
        Assert.Equal("https://second.example/api/v1/plugins/catalog", handler.LastRequestUri);
    }

    [Fact]
    public async Task RefreshAsync_MalformedJson_CatchesAndLeavesCacheEmpty()
    {
        var client = MakeClient(HttpStatusCode.OK, "not json at all", null, out _);
        var count = await client.RefreshAsync();
        Assert.Equal(0, count);
        Assert.Empty(client.Cached);
    }
}
