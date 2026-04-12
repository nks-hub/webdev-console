using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NKS.WebDevConsole.Daemon.Binaries;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="CatalogClient"/>. We don't own or control the external
/// catalog API, so the tests focus on two contracts:
///   1. The built-in fallback list is always merged into the cache so the UI
///      never goes dark — even when the external server is unreachable,
///      cloudflared must remain installable via the marketplace.
///   2. The query helpers (<c>ForApp</c>, <c>Find</c>, <c>FindLatest</c>) apply
///      the correct OS/arch/version filters against the cache.
///
/// We simulate unreachable / empty catalog by returning 500 from the mocked
/// <see cref="HttpMessageHandler"/> — RefreshAsync catches the exception and
/// merges the built-in fallback anyway.
/// </summary>
public sealed class CatalogClientTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static CatalogClient MakeClient(HttpStatusCode status, string? body = null, string baseUrl = "http://127.0.0.1:9999")
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? ""),
        });
        var factory = new StubHttpClientFactory(handler);
        var logger = new Mock<ILogger<CatalogClient>>();
        return new CatalogClient(factory, logger.Object, new CatalogClientOptions { BaseUrl = baseUrl });
    }

    [Fact]
    public async Task RefreshAsync_ServerError_StillPopulatesBuiltInFallback()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);

        var count = await client.RefreshAsync();

        Assert.True(count >= 3); // at least the 3 cloudflared fallback entries
        Assert.NotEmpty(client.CachedReleases);
        Assert.NotEqual(DateTime.MinValue, client.LastFetch);
    }

    [Fact]
    public async Task RefreshAsync_BuiltInFallback_IncludesCloudflaredForAllPlatforms()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        var windows = client.ForApp("cloudflared", os: "windows", arch: "x64");
        var linux = client.ForApp("cloudflared", os: "linux", arch: "x64");
        var macos = client.ForApp("cloudflared", os: "macos", arch: "x64");

        Assert.Single(windows);
        Assert.Single(linux);
        Assert.Single(macos);
    }

    [Fact]
    public async Task ForApp_FiltersByAppCaseInsensitive()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        // App name match is case-insensitive
        var upper = client.ForApp("CLOUDFLARED", os: "windows");
        Assert.Single(upper);
    }

    [Fact]
    public async Task ForApp_UnknownApp_ReturnsEmpty()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        Assert.Empty(client.ForApp("nonexistent-app"));
    }

    [Fact]
    public async Task ForApp_WrongOs_ReturnsEmpty()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        // cloudflared is in fallback for windows/linux/macos x64 — there's no arm64 entry
        Assert.Empty(client.ForApp("cloudflared", os: "windows", arch: "arm64"));
    }

    [Fact]
    public async Task Find_ByExactVersion_ReturnsMatch()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        var match = client.Find("cloudflared", "2026.3.0");
        Assert.NotNull(match);
        Assert.Equal("cloudflared", match!.App);
        Assert.Equal("2026.3.0", match.Version);
    }

    [Fact]
    public async Task Find_UnknownVersion_ReturnsNull()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        Assert.Null(client.Find("cloudflared", "9999.0.0"));
    }

    [Fact]
    public async Task FindLatest_WithoutMajorMinor_ReturnsFirstMatch()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        var latest = client.FindLatest("cloudflared");
        Assert.NotNull(latest);
        Assert.Equal("cloudflared", latest!.App);
    }

    [Fact]
    public async Task FindLatest_WithMajorMinor_FiltersCorrectly()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        var match = client.FindLatest("cloudflared", majorMinor: "2026.3");
        Assert.NotNull(match);
        Assert.Equal("2026.3", match!.MajorMinor);
    }

    [Fact]
    public async Task FindLatest_WithWrongMajorMinor_ReturnsNull()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        Assert.Null(client.FindLatest("cloudflared", majorMinor: "1.0"));
    }

    [Fact]
    public async Task CachedReleases_IsSnapshot_NotBackingList()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        await client.RefreshAsync();

        var snap = client.CachedReleases;
        var snap2 = client.CachedReleases;

        // Both are independent copies — mutating one shouldn't affect the other
        Assert.NotSame(snap, snap2);
        Assert.Equal(snap.Count, snap2.Count);
    }

    [Fact]
    public async Task LastFetch_StartsAtMinValue_UpdatedAfterRefresh()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        Assert.Equal(DateTime.MinValue, client.LastFetch);

        await client.RefreshAsync();

        Assert.True((DateTime.UtcNow - client.LastFetch).TotalMinutes < 1);
    }

    [Fact]
    public async Task RefreshAsync_BaseUrlProvider_Used_WhenSet()
    {
        var captured = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            captured.Add(req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var factory = new StubHttpClientFactory(handler);
        var logger = new Mock<ILogger<CatalogClient>>();
        var client = new CatalogClient(
            factory,
            logger.Object,
            new CatalogClientOptions { BaseUrl = "http://seed:1" },
            baseUrlProvider: () => "http://override:2");

        await client.RefreshAsync();

        Assert.Single(captured);
        Assert.Contains("override:2", captured[0]);
    }

    [Fact]
    public async Task RefreshAsync_BaseUrlProviderThrows_FallsBackToSeedUrl()
    {
        var captured = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            captured.Add(req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var factory = new StubHttpClientFactory(handler);
        var logger = new Mock<ILogger<CatalogClient>>();
        var client = new CatalogClient(
            factory,
            logger.Object,
            new CatalogClientOptions { BaseUrl = "http://seed-url:1234" },
            baseUrlProvider: () => throw new InvalidOperationException("settings store broke"));

        await client.RefreshAsync();

        Assert.Single(captured);
        Assert.Contains("seed-url:1234", captured[0]);
    }

    [Fact]
    public async Task RefreshAsync_ValidCatalogJson_FlattensReleases()
    {
        // Minimal valid catalog document with one PHP release + one download
        const string json = """
{
  "schema_version": "1",
  "apps": {
    "php": {
      "name": "php",
      "display_name": "PHP",
      "category": "runtime",
      "releases": [
        {
          "version": "8.3.10",
          "major_minor": "8.3",
          "channel": "stable",
          "downloads": [
            {
              "url": "https://windows.php.net/downloads/releases/php-8.3.10-Win32-vs16-x64.zip",
              "os": "windows",
              "arch": "x64",
              "archive_type": "zip",
              "source": "windows.php.net"
            }
          ]
        }
      ]
    }
  }
}
""";
        var client = MakeClient(HttpStatusCode.OK, json);
        var count = await client.RefreshAsync();

        Assert.True(count >= 1);
        var php = client.Find("php", "8.3.10");
        Assert.NotNull(php);
        Assert.Equal("8.3", php!.MajorMinor);
        Assert.Equal("windows", php.Os);
        Assert.Equal("zip", php.ArchiveType);
    }
}
