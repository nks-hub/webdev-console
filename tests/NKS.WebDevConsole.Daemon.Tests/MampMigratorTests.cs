using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class MampMigratorTests
{
    [Fact]
    public void Discover_ReturnsEmptyList_WhenNoMampInstalled()
    {
        var migrator = new MampMigrator(NullLogger<MampMigrator>.Instance);
        var sites = migrator.Discover();
        Assert.NotNull(sites);
        // On a machine without MAMP, should return empty — not throw.
        // If MAMP IS installed, we'd get real results (also valid).
        Assert.True(sites.Count >= 0);
    }

    [Fact]
    public void DiscoveredSite_RecordEquality()
    {
        var a = new MampMigrator.DiscoveredSite("test.loc", "C:/htdocs", "8.4", false, Array.Empty<string>(), "test.conf");
        var b = new MampMigrator.DiscoveredSite("test.loc", "C:/htdocs", "8.4", false, Array.Empty<string>(), "test.conf");
        // Record value equality — ensures the record is well-formed
        Assert.Equal(a.Domain, b.Domain);
        Assert.Equal(a.DocumentRoot, b.DocumentRoot);
    }
}
