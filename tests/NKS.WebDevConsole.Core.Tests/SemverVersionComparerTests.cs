using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Direct Core-level coverage for <see cref="SemverVersionComparer"/>.
/// The Daemon.Tests project also has tests for the comparer (because it
/// was born there), but the type now lives in Core so it deserves direct
/// tests without a plugin dependency.
///
/// The comparer is used by every binary-detection path in every plugin
/// (Apache, Caddy, Cloudflare, Mailpit, MySQL, Node, Redis). A regression
/// here would cause plugins to boot stale old versions when the user has
/// a newer major-version install — see commit 9920c9c / audit-2026-04-12-4.
/// </summary>
public sealed class SemverVersionComparerTests
{
    [Theory]
    // The classic failure cases: ordinal-sort ranked the wrong version higher.
    [InlineData("20.5.0", "9.0.0", 1)]
    [InlineData("10.0.0", "9.99.0", 1)]
    [InlineData("9.0.0", "20.5.0", -1)]
    // Normal patch-level ordering.
    [InlineData("18.17.0", "18.16.0", 1)]
    [InlineData("18.17.0", "18.17.1", -1)]
    [InlineData("20.0.0", "20.0.0", 0)]
    // Uneven segment counts — missing segments treated as 0.
    [InlineData("20.1", "20.1.0", 0)]
    [InlineData("20.1.0", "20.1", 0)]
    [InlineData("20", "19.99.99", 1)]
    // Pre-release handling — stable outranks same-main pre-release.
    [InlineData("1.0.0", "1.0.0-beta.1", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    public void CompareAscending_HandlesVersionPairs(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(SemverVersionComparer.CompareAscending(a, b)));
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData(null, "1.0.0", -1)]
    [InlineData("1.0.0", null, 1)]
    public void CompareAscending_HandlesNullInputs(string? a, string? b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(SemverVersionComparer.CompareAscending(a, b)));
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        // Static readonly singleton — two references must be identical so
        // LINQ usages (OrderByDescending) compare against the same object.
        Assert.Same(SemverVersionComparer.Instance, SemverVersionComparer.Instance);
    }

    [Fact]
    public void OrderByDescending_SortsNodeVersions_LatestFirst()
    {
        // Simulates the ~/.wdc/binaries/node/ directory listing.
        var versions = new[] { "9.0.0", "20.5.0", "18.17.0", "16.20.0", "10.24.1" };
        var sorted = versions
            .OrderByDescending(v => v, SemverVersionComparer.Instance)
            .ToArray();

        Assert.Equal(new[] { "20.5.0", "18.17.0", "16.20.0", "10.24.1", "9.0.0" }, sorted);
    }

    [Fact]
    public void OrderByDescending_SortsMySqlVersions_LatestFirst()
    {
        // MySQL 8.x coexists with stragglers like 5.7 on many dev machines;
        // ordinal sort would flip these.
        var versions = new[] { "5.7.44", "8.4.0", "8.0.35", "8.3.0" };
        var sorted = versions
            .OrderByDescending(v => v, SemverVersionComparer.Instance)
            .ToArray();

        Assert.Equal(new[] { "8.4.0", "8.3.0", "8.0.35", "5.7.44" }, sorted);
    }

    [Fact]
    public void OrderByDescending_PrefersStableOverPrerelease()
    {
        var versions = new[] { "20.5.0-rc.1", "20.5.0", "20.4.0", "21.0.0-beta" };
        var sorted = versions
            .OrderByDescending(v => v, SemverVersionComparer.Instance)
            .ToArray();

        // 21.0.0-beta beats 20.5.0 (higher main) but loses to any 21.0.0 stable (none here).
        // 20.5.0 (stable) beats 20.5.0-rc.1.
        Assert.Equal(new[] { "21.0.0-beta", "20.5.0", "20.5.0-rc.1", "20.4.0" }, sorted);
    }

    [Theory]
    [InlineData("2.4.62", "2.4.62-alpine", 1)]
    [InlineData("10.11.2-MariaDB", "10.11.1-MariaDB", 1)]
    [InlineData("abc", "def", -1)]
    public void CompareAscending_HandlesNonNumericSegments(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(SemverVersionComparer.CompareAscending(a, b)));
    }

    [Fact]
    public void OrderByDescending_HandlesApacheVersions()
    {
        var versions = new[] { "2.4.58", "2.4.62", "2.4.59" };
        var sorted = versions
            .OrderByDescending(v => v, SemverVersionComparer.Instance)
            .ToArray();
        Assert.Equal(new[] { "2.4.62", "2.4.59", "2.4.58" }, sorted);
    }
}
