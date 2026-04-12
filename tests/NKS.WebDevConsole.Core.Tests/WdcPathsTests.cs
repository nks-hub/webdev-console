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

    [Fact]
    public void Root_IsAbsolutePath()
    {
        Assert.True(Path.IsPathRooted(WdcPaths.Root));
    }

    [Fact]
    public void Root_IsConsistentAcrossCalls()
    {
        var a = WdcPaths.Root;
        var b = WdcPaths.Root;
        Assert.Same(a, b);
    }

    [Fact]
    public void IsPortableMode_ReturnsBool()
    {
        // Just verify it doesn't throw — value depends on env var
        _ = WdcPaths.IsPortableMode;
    }

    [Fact]
    public void SubPaths_EndWithExpectedDirectoryName()
    {
        Assert.EndsWith("binaries", WdcPaths.BinariesRoot);
        Assert.EndsWith("data", WdcPaths.DataRoot);
        Assert.EndsWith("sites", WdcPaths.SitesRoot);
        Assert.EndsWith("logs", WdcPaths.LogsRoot);
        Assert.EndsWith("ssl", WdcPaths.SslRoot);
        Assert.EndsWith("backups", WdcPaths.BackupsRoot);
        Assert.EndsWith("cache", WdcPaths.CacheRoot);
        Assert.EndsWith("generated", WdcPaths.GeneratedRoot);
        Assert.EndsWith("caddy", WdcPaths.CaddyRoot);
        Assert.EndsWith("cloudflare", WdcPaths.CloudflareRoot);
    }

    [Fact]
    public void AllSubPathsAreDistinct()
    {
        var paths = new[]
        {
            WdcPaths.BinariesRoot, WdcPaths.DataRoot, WdcPaths.SitesRoot,
            WdcPaths.LogsRoot, WdcPaths.SslRoot, WdcPaths.BackupsRoot,
            WdcPaths.CacheRoot, WdcPaths.GeneratedRoot,
            WdcPaths.CaddyRoot, WdcPaths.CloudflareRoot,
        };
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
