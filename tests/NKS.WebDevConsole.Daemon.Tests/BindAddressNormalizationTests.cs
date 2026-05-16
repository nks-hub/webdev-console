using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for the bind-address pipeline that decides which IP addresses each
/// generated Apache vhost listens on. Covers the public surface that the
/// HTTP layer and Apache plugin both consume:
/// <c>SiteManager.ValidateBindAddresses</c> for input validation and
/// <c>SiteManager.EffectiveApacheBindAddresses</c> for the listener
/// projection (including the localhost-only loopback expansion).
/// </summary>
public class BindAddressNormalizationTests
{
    [Fact]
    public void ValidateBindAddresses_NullInput_DoesNotThrow()
    {
        SiteManager.ValidateBindAddresses(null);
    }

    [Fact]
    public void ValidateBindAddresses_EmptyArray_DoesNotThrow()
    {
        SiteManager.ValidateBindAddresses(Array.Empty<string>());
    }

    [Fact]
    public void ValidateBindAddresses_WildcardOnly_IsAllowed()
    {
        SiteManager.ValidateBindAddresses(new[] { "*" });
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("fe80::1")]
    public void ValidateBindAddresses_ValidIpAddresses_DoNotThrow(string ip)
    {
        SiteManager.ValidateBindAddresses(new[] { ip });
    }

    [Fact]
    public void ValidateBindAddresses_MultipleConcreteIps_DoNotThrow()
    {
        SiteManager.ValidateBindAddresses(new[] { "192.168.1.10", "10.0.0.1", "::1" });
    }

    [Fact]
    public void ValidateBindAddresses_WildcardWithConcreteIp_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SiteManager.ValidateBindAddresses(new[] { "*", "192.168.1.10" }));
        Assert.Contains("must be selected by itself", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    [InlineData("hello world")]
    [InlineData("evil; rm -rf /")]
    public void ValidateBindAddresses_GarbageInput_Throws(string garbage)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            SiteManager.ValidateBindAddresses(new[] { garbage }));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("|")]
    [InlineData("`")]
    [InlineData(";")]
    public void ValidateBindAddresses_ShellInjectionChars_Throws(string injection)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            SiteManager.ValidateBindAddresses(new[] { "127.0.0.1" + injection }));
    }

    [Fact]
    public void EffectiveApacheBindAddresses_RegularSiteWildcard_RendersWildcard()
    {
        var site = new SiteConfig
        {
            Domain = "blog.loc",
            DocumentRoot = @"C:\sites\blog",
            BindAddresses = new[] { "*" },
        };

        var listeners = SiteManager.EffectiveApacheBindAddresses(site);

        Assert.Equal(new[] { "*" }, listeners);
    }

    [Fact]
    public void EffectiveApacheBindAddresses_RegularSiteSingleIp_RendersSingleIp()
    {
        var site = new SiteConfig
        {
            Domain = "intranet.loc",
            DocumentRoot = @"C:\sites\intranet",
            BindAddresses = new[] { "192.168.1.10" },
        };

        var listeners = SiteManager.EffectiveApacheBindAddresses(site);

        Assert.Equal(new[] { "192.168.1.10" }, listeners);
    }

    [Fact]
    public void EffectiveApacheBindAddresses_IPv6_GetsBracketWrapped()
    {
        var site = new SiteConfig
        {
            Domain = "v6.loc",
            DocumentRoot = @"C:\sites\v6",
            BindAddresses = new[] { "fe80::1" },
        };

        var listeners = SiteManager.EffectiveApacheBindAddresses(site);

        Assert.Single(listeners);
        Assert.StartsWith("[", listeners[0]);
        Assert.EndsWith("]", listeners[0]);
    }

    [Fact]
    public void EffectiveApacheBindAddresses_MultipleConcreteIps_RendersAllInOrder()
    {
        var site = new SiteConfig
        {
            Domain = "lan.loc",
            DocumentRoot = @"C:\sites\lan",
            BindAddresses = new[] { "192.168.1.10", "10.0.0.5" },
        };

        var listeners = SiteManager.EffectiveApacheBindAddresses(site);

        Assert.Equal(new[] { "192.168.1.10", "10.0.0.5" }, listeners);
    }

    [Fact]
    public void EffectiveApacheBindAddresses_LocalhostWildcard_StaysWildcard()
    {
        var site = new SiteConfig
        {
            Domain = "localhost",
            DocumentRoot = @"C:\sites\default",
            BindAddresses = new[] { "*" },
        };

        var listeners = SiteManager.EffectiveApacheBindAddresses(site);

        Assert.Equal(new[] { "*" }, listeners);
    }

    [Fact]
    public void EffectiveApacheBindAddresses_LocalhostWithConcreteIp_AppendsLoopback()
    {
        var site = new SiteConfig
        {
            Domain = "localhost",
            DocumentRoot = @"C:\sites\default",
            BindAddresses = new[] { "192.168.1.10" },
        };

        var listeners = SiteManager.EffectiveApacheBindAddresses(site);

        Assert.Contains("192.168.1.10", listeners);
        Assert.Contains("127.0.0.1", listeners);
        Assert.Contains("[::1]", listeners);
    }

    [Fact]
    public void EffectiveApacheBindAddresses_LegacyBindAddressFallback_Honored()
    {
        var site = new SiteConfig
        {
            Domain = "api.loc",
            DocumentRoot = @"C:\sites\api",
            BindAddress = "10.0.0.50",
            BindAddresses = Array.Empty<string>(),
        };

        var listeners = SiteManager.EffectiveApacheBindAddresses(site);

        Assert.Equal(new[] { "10.0.0.50" }, listeners);
    }

    [Fact]
    public void EffectiveApacheBindAddresses_EmptyConfig_FallsBackToWildcard()
    {
        var site = new SiteConfig
        {
            Domain = "fresh.loc",
            DocumentRoot = @"C:\sites\fresh",
            BindAddress = "",
            BindAddresses = Array.Empty<string>(),
        };

        var listeners = SiteManager.EffectiveApacheBindAddresses(site);

        Assert.Equal(new[] { "*" }, listeners);
    }

    // ── CollectBindAddressWarnings ─────────────────────────────────────────
    // Soft sanity check: detect bind IPs that aren't currently assigned to
    // any Up NIC. The check skips wildcard and the universally-present
    // loopback addresses (which are always available even on offline hosts)
    // so it only flags real misconfigurations, not transient network state.

    [Fact]
    public void CollectBindAddressWarnings_Wildcard_NoWarnings()
    {
        var site = new SiteConfig
        {
            Domain = "wild.loc",
            DocumentRoot = @"C:\sites\wild",
            BindAddresses = new[] { "*" },
        };

        var warnings = SiteManager.CollectBindAddressWarnings(site);

        Assert.Empty(warnings);
    }

    [Fact]
    public void CollectBindAddressWarnings_LoopbackV4_NoWarnings()
    {
        var site = new SiteConfig
        {
            Domain = "loop.loc",
            DocumentRoot = @"C:\sites\loop",
            BindAddresses = new[] { "127.0.0.1" },
        };

        var warnings = SiteManager.CollectBindAddressWarnings(site);

        Assert.Empty(warnings);
    }

    [Fact]
    public void CollectBindAddressWarnings_LoopbackV6_NoWarnings()
    {
        var site = new SiteConfig
        {
            Domain = "loop6.loc",
            DocumentRoot = @"C:\sites\loop6",
            BindAddresses = new[] { "::1" },
        };

        var warnings = SiteManager.CollectBindAddressWarnings(site);

        Assert.Empty(warnings);
    }

    [Fact]
    public void CollectBindAddressWarnings_BogusIpNotOnAnyNic_ReturnsWarning()
    {
        // 198.51.100.99 is in TEST-NET-2 (RFC 5737) — guaranteed not assigned
        // to any real NIC, so this is a stable cross-environment failure case.
        var site = new SiteConfig
        {
            Domain = "bogus.loc",
            DocumentRoot = @"C:\sites\bogus",
            BindAddresses = new[] { "198.51.100.99" },
        };

        var warnings = SiteManager.CollectBindAddressWarnings(site);

        Assert.Single(warnings);
        Assert.Contains("198.51.100.99", warnings[0]);
        Assert.Contains("not assigned", warnings[0]);
    }

    [Fact]
    public void CollectBindAddressWarnings_NullSite_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SiteManager.CollectBindAddressWarnings(null!));
    }
}
