using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="SiteManager.ValidateDomain"/>.
///
/// Two goals:
///   1. Security — every code path that builds a filesystem path from a
///      domain (site TOML, generated vhost, SSL cert dir) relies on
///      ValidateDomain to reject traversal / shell-meta / NUL / whitespace
///      input. A regression here would re-open file-write-outside-sandbox
///      classes of vulnerability.
///   2. RFC compliance — per-label rules (length ≤ 63, no leading/trailing
///      hyphen, no empty labels). The previous version had a bug where the
///      whole-string regex allowed labels like "-bar" (hyphen-prefixed),
///      which Apache and mkcert technically accept but some resolvers drop,
///      producing diagnostically-confusing "site created but doesn't
///      resolve" reports. Fixed 2026-04-11.
/// </summary>
public class DomainValidationTests
{
    [Theory]
    [InlineData("myapp.loc")]
    [InlineData("a")]
    [InlineData("localhost")]
    [InlineData("foo.bar.baz.example.com")]
    [InlineData("site1.loc")]
    [InlineData("shop-prod.myapp.loc")]
    public void Accepts_ValidDomains(string domain)
    {
        // Should not throw
        SiteManager.ValidateDomain(domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Rejects_EmptyOrWhitespace(string domain)
    {
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateDomain(domain));
    }

    [Theory]
    [InlineData("foo bar.com")]     // space
    [InlineData("foo`bar.com")]     // backtick
    [InlineData("foo|bar.com")]     // pipe
    [InlineData("foo;bar.com")]     // semicolon
    [InlineData("foo$bar.com")]     // dollar
    [InlineData("foo\"bar.com")]    // quote
    [InlineData("foo'bar.com")]     // apostrophe
    [InlineData("foo<bar.com")]     // lt
    [InlineData("foo>bar.com")]     // gt
    [InlineData("foo&bar.com")]     // amp
    [InlineData("foo%bar.com")]     // percent
    [InlineData("foo\0bar.com")]    // NUL
    public void Rejects_ShellMetaCharacters(string domain)
    {
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateDomain(domain));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("foo..bar.com")]
    [InlineData("..myapp.loc")]
    [InlineData("myapp..")]
    public void Rejects_PathTraversal(string domain)
    {
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateDomain(domain));
    }

    [Theory]
    [InlineData("-foo.com")]       // leading hyphen on whole domain (regex)
    [InlineData("foo.-bar.com")]   // leading hyphen on middle label (NEW CHECK)
    [InlineData("foo.bar-.com")]   // trailing hyphen on middle label (NEW CHECK)
    [InlineData("-label.example")] // leading hyphen on first label (regex)
    [InlineData("label-.example")] // trailing hyphen on first label (NEW CHECK)
    public void Rejects_HyphenAtLabelBoundary(string domain)
    {
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateDomain(domain));
    }

    [Fact]
    public void Rejects_LabelLongerThan63Chars()
    {
        // 64 'a's in one label → invalid per RFC 1035
        var bigLabel = new string('a', 64);
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateDomain($"{bigLabel}.com"));
    }

    [Fact]
    public void Rejects_DomainLongerThan253Chars()
    {
        // 254 chars total → invalid
        var labels = string.Join(".", Enumerable.Repeat(new string('a', 30), 10));
        var big = labels + ".example.com"; // > 253
        if (big.Length <= 253) big += new string('x', 254 - big.Length + 1);
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateDomain(big));
    }

    [Theory]
    [InlineData("*.example.loc")]   // wildcard prefix is an alias concept, not a primary domain
    [InlineData("*example.loc")]
    [InlineData("foo.*.loc")]
    public void Rejects_WildcardInPrimaryDomain(string domain)
    {
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateDomain(domain));
    }

    [Theory]
    [InlineData("a.b")]             // minimal 2-label
    [InlineData("site.loc")]
    [InlineData("www.example.com")]
    [InlineData("api-v2.example.co.uk")]
    public void Accepts_CommonValidDomains(string domain)
    {
        SiteManager.ValidateDomain(domain);
    }
}
