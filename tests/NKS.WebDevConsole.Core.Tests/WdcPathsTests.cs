using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Protects <see cref="WdcPaths"/> resolution logic. The root is
/// lazily resolved once at startup — a regression here could silently
/// redirect every service's data to the wrong directory.
/// </summary>
public sealed class WdcPathsAdditionalTests
{
    [Fact]
    public void SubPaths_AreUnderRoot()
    {
        var root = WdcPaths.Root;
        Assert.StartsWith(root, WdcPaths.BinariesRoot);
        Assert.StartsWith(root, WdcPaths.DataRoot);
        Assert.StartsWith(root, WdcPaths.SitesRoot);
        Assert.StartsWith(root, WdcPaths.LogsRoot);
        Assert.StartsWith(root, WdcPaths.SslRoot);
        Assert.StartsWith(root, WdcPaths.CacheRoot);
        Assert.StartsWith(root, WdcPaths.BackupsRoot);
        Assert.StartsWith(root, WdcPaths.GeneratedRoot);
        Assert.StartsWith(root, WdcPaths.CaddyRoot);
        Assert.StartsWith(root, WdcPaths.CloudflareRoot);
    }
}
