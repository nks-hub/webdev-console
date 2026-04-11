using System.Reflection;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <c>SiteOrchestrator.AllDomainsAlreadyMapped</c> — the pre-flight
/// check that decides whether a UAC prompt is necessary when applying a site.
///
/// This method gates the UAC elevation in <c>UpdateHostsFileAsync</c>: returning
/// true means "no write needed, skip elevation". A false positive therefore
/// means UAC is skipped even though the required mapping is missing (resulting
/// in a silently broken site). A false negative means an unnecessary UAC
/// prompt (annoyance, not correctness).
///
/// Historical bug fixed 2026-04-11: the original implementation only stripped
/// inline <c>#</c> comments on a per-token basis, so a line like
/// <c>127.0.0.1 foo.com #disabled bar.com</c> would incorrectly mark
/// <c>bar.com</c> as mapped. The fix strips the comment at the line level
/// before tokenising.
/// </summary>
public class AllDomainsAlreadyMappedTests
{
    private static readonly MethodInfo Method = typeof(SiteOrchestrator)
        .GetMethod("AllDomainsAlreadyMapped", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AllDomainsAlreadyMapped not found via reflection");

    private static bool Invoke(string hostsContent, params string[] domains) =>
        (bool)Method.Invoke(null, new object[]
        {
            hostsContent,
            new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase)
        })!;

    [Fact]
    public void EmptyRequiredSet_IsAlwaysMapped()
    {
        Assert.True(Invoke(""));
        Assert.True(Invoke("127.0.0.1 whatever.loc"));
    }

    [Fact]
    public void AllRequiredDomainsPresent_ReturnsTrue()
    {
        const string hosts =
            "127.0.0.1 localhost\n" +
            "127.0.0.1 foo.loc\n" +
            "127.0.0.1 bar.loc baz.loc";
        Assert.True(Invoke(hosts, "foo.loc", "bar.loc", "baz.loc"));
    }

    [Fact]
    public void MissingDomain_ReturnsFalse()
    {
        const string hosts = "127.0.0.1 foo.loc\n";
        Assert.False(Invoke(hosts, "foo.loc", "bar.loc"));
    }

    [Fact]
    public void CommentedOutMapping_IsNotCounted()
    {
        const string hosts = "# 127.0.0.1 foo.loc\n";
        Assert.False(Invoke(hosts, "foo.loc"));
    }

    [Fact]
    public void NonLoopbackMapping_IsNotCounted()
    {
        const string hosts = "192.168.1.10 foo.loc\n";
        Assert.False(Invoke(hosts, "foo.loc"));
    }

    [Fact]
    public void Ipv6LoopbackMapping_IsCounted()
    {
        const string hosts = "::1 foo.loc\n";
        Assert.True(Invoke(hosts, "foo.loc"));
    }

    [Fact]
    public void InlineCommentAfterDomain_DoesNotCountCommentedTokens()
    {
        // THIS IS THE REGRESSION: previously returned true, treating bar.loc as mapped.
        const string hosts = "127.0.0.1 foo.loc # disabled bar.loc\n";
        Assert.True(Invoke(hosts, "foo.loc"));
        Assert.False(Invoke(hosts, "bar.loc"));
        Assert.False(Invoke(hosts, "foo.loc", "bar.loc"));
    }

    [Fact]
    public void MixedTabsAndSpaces_Parses()
    {
        const string hosts = "127.0.0.1\tfoo.loc   bar.loc\t\tbaz.loc\n";
        Assert.True(Invoke(hosts, "foo.loc", "bar.loc", "baz.loc"));
    }

    [Fact]
    public void CrlfLineEndings_Parses()
    {
        const string hosts = "127.0.0.1 foo.loc\r\n127.0.0.1 bar.loc\r\n";
        Assert.True(Invoke(hosts, "foo.loc", "bar.loc"));
    }

    [Fact]
    public void CaseInsensitiveDomainMatch()
    {
        const string hosts = "127.0.0.1 Foo.Loc\n";
        Assert.True(Invoke(hosts, "foo.loc"));
        Assert.True(Invoke(hosts, "FOO.LOC"));
    }

    [Fact]
    public void BlankLinesAndCommentLines_Skipped()
    {
        const string hosts =
            "\n" +
            "# This is a comment\n" +
            "\n" +
            "127.0.0.1 foo.loc\n";
        Assert.True(Invoke(hosts, "foo.loc"));
    }
}
