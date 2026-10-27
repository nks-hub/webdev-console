using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

public class SiteApplyResultTests
{
    [Fact]
    public void Success_IsFalseOnlyWhenAStepFailed()
    {
        var r = new SiteApplyResult("a.loc");
        r.Ok("vhost");
        r.Skip("reload", "Apache is not running");
        Assert.True(r.Success);

        r.Fail("hosts", "elevation declined");
        Assert.False(r.Success);
        Assert.Equal(new[] { "vhost", "reload", "hosts" }, r.Steps.Select(s => s.Name));
    }
}
