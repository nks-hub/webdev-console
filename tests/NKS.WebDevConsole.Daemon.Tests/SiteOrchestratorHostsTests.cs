using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class SiteOrchestratorHostsTests
{
    [Fact]
    public void RewriteManagedHostsContent_AppendsManagedBlock_WhenMissing()
    {
        var original = "127.0.0.1\tlocalhost\r\n# custom\r\n10.0.0.5\tintranet\r\n";

        var rewritten = SiteOrchestrator.RewriteManagedHostsContent(original, ["app.loc", "api.app.loc"]);

        Assert.Contains("127.0.0.1\tlocalhost", rewritten);
        Assert.Contains("10.0.0.5\tintranet", rewritten);
        Assert.Contains(SiteOrchestrator.HostsBlockBegin, rewritten);
        Assert.Contains("127.0.0.1\tapp.loc", rewritten);
        Assert.Contains("127.0.0.1\tapi.app.loc", rewritten);
        Assert.Contains(SiteOrchestrator.HostsBlockEnd, rewritten);
    }

    [Fact]
    public void RewriteManagedHostsContent_ReplacesExistingManagedBlock_WithoutTouchingOtherLines()
    {
        var original = """
127.0.0.1	localhost
# BEGIN NKS WebDev Console
127.0.0.1	old.loc
# END NKS WebDev Console
192.168.1.10	router.loc
""";

        var rewritten = SiteOrchestrator.RewriteManagedHostsContent(original, ["new.loc"]);

        Assert.DoesNotContain("old.loc", rewritten);
        Assert.Contains("127.0.0.1\tnew.loc", rewritten);
        Assert.Contains("192.168.1.10\trouter.loc", rewritten);
    }

    [Fact]
    public void RewriteManagedHostsContent_RemovesManagedBlock_WhenDomainSetEmpty()
    {
        var original = """
127.0.0.1	localhost

# BEGIN NKS WebDev Console
127.0.0.1	old.loc
# END NKS WebDev Console

192.168.1.10	router.loc
""";

        var rewritten = SiteOrchestrator.RewriteManagedHostsContent(original, Array.Empty<string>());

        Assert.DoesNotContain(SiteOrchestrator.HostsBlockBegin, rewritten);
        Assert.DoesNotContain("old.loc", rewritten);
        Assert.Contains("127.0.0.1\tlocalhost", rewritten);
        Assert.Contains("192.168.1.10\trouter.loc", rewritten);
    }

    [Fact]
    public void RewriteManagedHostsContent_EmptyOriginal_CreatesBlock()
    {
        var rewritten = SiteOrchestrator.RewriteManagedHostsContent("", ["test.loc"]);
        Assert.Contains(SiteOrchestrator.HostsBlockBegin, rewritten);
        Assert.Contains("127.0.0.1\ttest.loc", rewritten);
    }

    [Fact]
    public void RewriteManagedHostsContent_MultipleDomains_AllPresent()
    {
        var rewritten = SiteOrchestrator.RewriteManagedHostsContent("", ["a.loc", "b.loc", "c.loc"]);
        Assert.Contains("a.loc", rewritten);
        Assert.Contains("b.loc", rewritten);
        Assert.Contains("c.loc", rewritten);
    }

    [Fact]
    public void RewriteManagedHostsContent_PreservesComments()
    {
        var original = "# My custom hosts\n127.0.0.1\tlocalhost\n";
        var rewritten = SiteOrchestrator.RewriteManagedHostsContent(original, ["x.loc"]);
        Assert.Contains("# My custom hosts", rewritten);
    }

    [Fact]
    public void RewriteManagedHostsContent_SafetyAbort_OnLineLoss()
    {
        var original = "127.0.0.1\tlocalhost\n10.0.0.1\timportant-server\n";
        var rewritten = SiteOrchestrator.RewriteManagedHostsContent(original, ["site.loc"]);
        Assert.Contains("important-server", rewritten);
    }

    [Fact]
    public void HostsBlockBegin_IsExpectedString()
    {
        Assert.Equal("# BEGIN NKS WebDev Console", SiteOrchestrator.HostsBlockBegin);
    }

    [Fact]
    public void HostsBlockEnd_IsExpectedString()
    {
        Assert.Equal("# END NKS WebDev Console", SiteOrchestrator.HostsBlockEnd);
    }
}
