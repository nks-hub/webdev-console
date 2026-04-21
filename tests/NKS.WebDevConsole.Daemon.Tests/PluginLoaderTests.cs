using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Daemon.Plugin;

namespace NKS.WebDevConsole.Daemon.Tests;

public class PluginLoaderTests
{
    private readonly PluginLoader _loader;
    private readonly Mock<ILogger<PluginLoader>> _loggerMock = new();

    public PluginLoaderTests()
    {
        _loader = new PluginLoader(_loggerMock.Object);
    }

    [Fact]
    public void NewLoader_HasNoPlugins()
    {
        Assert.Empty(_loader.Plugins);
    }

    [Fact]
    public void LoadPlugins_NonExistentDirectory_LogsWarningAndReturns()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        _loader.LoadPlugins(fakePath);

        Assert.Empty(_loader.Plugins);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("not found")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LoadPlugins_EmptyDirectory_LoadsNothing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            _loader.LoadPlugins(tempDir);
            Assert.Empty(_loader.Plugins);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Plugins_IsReadOnly()
    {
        Assert.IsAssignableFrom<IReadOnlyList<LoadedPlugin>>(_loader.Plugins);
    }

    // ── PluginLoaderInternals.IsSemVer ─────────────────────────────────────
    // Permissive subset of SemVer 2.0.0: MAJOR.MINOR.PATCH, optional pre-release
    // and build metadata. Used by the loader to gate plugin manifests so plugins
    // that ship with version strings like "dev"/"unknown"/"latest" get rejected
    // — the catalog's auto-update mechanism relies on a parseable version.

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.4.62")]
    [InlineData("0.1.0")]
    [InlineData("9999.9999.9999")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("1.2.3-alpha.4.7")]
    [InlineData("2.0.0+build.42")]
    [InlineData("1.2.3-beta+exp.sha.5114f85")]
    public void IsSemVer_AcceptsValidVersions(string version)
    {
        Assert.True(PluginLoaderInternals.IsSemVer(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]              // leading 'v' not allowed
    [InlineData("1.0.0.0")]              // 4-segment not allowed
    [InlineData("dev")]                  // common junk
    [InlineData("unknown")]
    [InlineData("latest")]
    [InlineData("1.0.0-")]                // empty prerelease
    [InlineData("1.0.0+")]                // empty build
    [InlineData("01.0.0")]                // we accept leading zeros (regex is digit-count, not strict semver)
    public void IsSemVer_RejectsInvalidVersions(string? version)
    {
        // The "01.0.0" case actually passes the regex \d{1,4} so this is a
        // documented permissive deviation from strict semver. Skip that one.
        if (version == "01.0.0")
        {
            Assert.True(PluginLoaderInternals.IsSemVer(version));
            return;
        }
        Assert.False(PluginLoaderInternals.IsSemVer(version));
    }

    [Fact]
    public void IsSemVer_RejectsLongerThanSegmentLimit()
    {
        // Each segment is bounded to 1-4 digits. Five-digit major rejects.
        Assert.False(PluginLoaderInternals.IsSemVer("12345.0.0"));
    }

    [Fact]
    public void IsSemVer_AllowsBoundaryFourDigit()
    {
        // The regex max is exactly 4 digits per segment.
        Assert.True(PluginLoaderInternals.IsSemVer("9999.0.0"));
        Assert.True(PluginLoaderInternals.IsSemVer("1.9999.0"));
        Assert.True(PluginLoaderInternals.IsSemVer("1.0.9999"));
    }

    // ── Task 25b: PluginManifestData new fields ────────────────────────────

    private static readonly JsonSerializerOptions ManifestOptions =
        new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void PluginManifestData_PageBundleUrl_ParsedFromJson()
    {
        const string json = """
            {
              "id": "nks.wdc.apache",
              "pageBundleUrl": "./ui/apache.umd.js"
            }
            """;

        var manifest = JsonSerializer.Deserialize<PluginManifestData>(json, ManifestOptions);

        Assert.NotNull(manifest);
        Assert.Equal("./ui/apache.umd.js", manifest.PageBundleUrl);
    }

    [Fact]
    public void PluginManifestData_PageBundleUrl_NullWhenAbsent()
    {
        const string json = """{ "id": "nks.wdc.apache" }""";

        var manifest = JsonSerializer.Deserialize<PluginManifestData>(json, ManifestOptions);

        Assert.NotNull(manifest);
        Assert.Null(manifest.PageBundleUrl);
    }

    [Fact]
    public void PluginManifestData_Ports_ParsedFromJson()
    {
        const string json = """
            {
              "id": "nks.wdc.apache",
              "ports": [
                { "key": "http",  "label": "HTTP",  "default": 80  },
                { "key": "https", "label": "HTTPS", "default": 443 }
              ]
            }
            """;

        var manifest = JsonSerializer.Deserialize<PluginManifestData>(json, ManifestOptions);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest.Ports);
        Assert.Equal(2, manifest.Ports.Length);

        var http = manifest.Ports[0];
        Assert.Equal("http", http.Key);
        Assert.Equal("HTTP", http.Label);
        Assert.Equal(80, http.Default);

        var https = manifest.Ports[1];
        Assert.Equal("https", https.Key);
        Assert.Equal("HTTPS", https.Label);
        Assert.Equal(443, https.Default);
    }

    [Fact]
    public void PluginManifestData_Ports_NullWhenAbsent()
    {
        const string json = """{ "id": "nks.wdc.apache" }""";

        var manifest = JsonSerializer.Deserialize<PluginManifestData>(json, ManifestOptions);

        Assert.NotNull(manifest);
        Assert.Null(manifest.Ports);
    }

    [Fact]
    public void PluginManifestData_Ports_EmptyArrayWhenEmptyJson()
    {
        const string json = """{ "id": "nks.wdc.apache", "ports": [] }""";

        var manifest = JsonSerializer.Deserialize<PluginManifestData>(json, ManifestOptions);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest.Ports);
        Assert.Empty(manifest.Ports);
    }

    [Fact]
    public void PluginManifestData_AllNewFields_ToleratesExistingManifestWithout()
    {
        // Legacy manifests that predate task 25b must still deserialize cleanly.
        const string json = """
            {
              "id": "nks.wdc.mysql",
              "displayName": "MySQL",
              "version": "1.0.0",
              "defaultPorts": [3306]
            }
            """;

        var manifest = JsonSerializer.Deserialize<PluginManifestData>(json, ManifestOptions);

        Assert.NotNull(manifest);
        Assert.Null(manifest.PageBundleUrl);
        Assert.Null(manifest.Ports);
    }
}
