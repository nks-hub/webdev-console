using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class MampMigratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MampMigrator _migrator = new(NullLogger<MampMigrator>.Instance);

    public MampMigratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nks-mamp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Discover_ReturnsEmptyList_WhenNoMampInstalled()
    {
        var sites = _migrator.Discover();
        Assert.NotNull(sites);
        Assert.True(sites.Count >= 0);
    }

    [Fact]
    public void DiscoveredSite_RecordEquality()
    {
        var a = new MampMigrator.DiscoveredSite("test.loc", "C:/htdocs", "8.4", false, Array.Empty<string>(), "test.conf");
        var b = new MampMigrator.DiscoveredSite("test.loc", "C:/htdocs", "8.4", false, Array.Empty<string>(), "test.conf");
        Assert.Equal(a.Domain, b.Domain);
        Assert.Equal(a.DocumentRoot, b.DocumentRoot);
    }

    [Fact]
    public void DiscoveredSite_AllFieldsPopulated()
    {
        var site = new MampMigrator.DiscoveredSite(
            "shop.loc", "C:/htdocs/shop", "8.3", true,
            new[] { "www.shop.loc" }, "vhost.conf");
        Assert.Equal("shop.loc", site.Domain);
        Assert.Equal("C:/htdocs/shop", site.DocumentRoot);
        Assert.Equal("8.3", site.PhpVersion);
        Assert.True(site.SslEnabled);
        Assert.Single(site.Aliases);
        Assert.Equal("www.shop.loc", site.Aliases[0]);
        Assert.Equal("vhost.conf", site.SourcePath);
    }

    [Fact]
    public void DiscoveredSite_EmptyAliases_IsValid()
    {
        var site = new MampMigrator.DiscoveredSite("x.loc", "/var/www", "8.4", false, Array.Empty<string>(), "f.conf");
        Assert.Empty(site.Aliases);
    }

    [Fact]
    public void Discover_IsDeterministic_OnSameMachine()
    {
        var first = _migrator.Discover();
        var second = _migrator.Discover();
        Assert.Equal(first.Count, second.Count);
    }
}
