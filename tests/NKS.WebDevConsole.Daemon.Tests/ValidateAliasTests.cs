using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="SiteManager.ValidateAlias"/> — the security gate for
/// ServerAlias entries. Unlike the primary domain, aliases may contain leading
/// wildcards (<c>*.myapp.loc</c>) which Apache and mkcert both support.
/// </summary>
public class ValidateAliasTests
{
    [Theory]
    [InlineData("www.myapp.loc")]
    [InlineData("api.myapp.loc")]
    [InlineData("*.myapp.loc")]
    [InlineData("a")]
    [InlineData("sub.deep.myapp.loc")]
    public void Accepts_ValidAliases(string alias)
    {
        SiteManager.ValidateAlias(alias);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_EmptyOrNull(string? alias)
    {
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateAlias(alias!));
    }

    [Theory]
    [InlineData("foo bar.com")]
    [InlineData("foo\tbar.com")]
    [InlineData("foo\nbar.com")]
    [InlineData("foo\rbar.com")]
    [InlineData("foo\0bar.com")]
    [InlineData("foo;bar.com")]
    [InlineData("foo|bar.com")]
    [InlineData("foo&bar.com")]
    [InlineData("foo$bar.com")]
    [InlineData("foo`bar.com")]
    [InlineData("foo>bar.com")]
    [InlineData("foo<bar.com")]
    [InlineData("foo\"bar.com")]
    [InlineData("foo'bar.com")]
    public void Rejects_ForbiddenCharacters(string alias)
    {
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateAlias(alias));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("foo..\\bar")]
    public void Rejects_PathTraversal(string alias)
    {
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateAlias(alias));
    }

    [Fact]
    public void Rejects_AliasLongerThan253()
    {
        var big = new string('a', 254);
        Assert.Throws<ArgumentException>(() => SiteManager.ValidateAlias(big));
    }

    [Fact]
    public void Accepts_WildcardPrefix()
    {
        SiteManager.ValidateAlias("*.example.loc");
    }

    [Fact]
    public void Accepts_QuestionMarkWildcard()
    {
        SiteManager.ValidateAlias("app?.example.loc");
    }

    [Fact]
    public void Accepts_MultiLevelSubdomain()
    {
        SiteManager.ValidateAlias("deep.sub.example.loc");
    }

    [Fact]
    public void Accepts_HyphenatedAlias()
    {
        SiteManager.ValidateAlias("my-cool-app.example.loc");
    }

    [Fact]
    public void Accepts_MaxLength253()
    {
        var alias = string.Join(".", Enumerable.Repeat("a", 50)) + ".loc";
        if (alias.Length <= 253)
            SiteManager.ValidateAlias(alias);
    }
}
