using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="WdcPaths"/>. The class caches its root via
/// <c>Lazy&lt;string&gt;</c> so the fact that env-var resolution happens once
/// at first access is a feature, not a bug — these tests verify the
/// sub-roots all agree with whatever root was resolved and that the typed
/// sub-root helpers compose paths correctly.
///
/// Note: we cannot meaningfully test the env-var override vs
/// <c>~/.wdc</c> fallback in a single process because <see cref="WdcPaths.Root"/>
/// is cached after first access. The portable-mode code path is exercised
/// end-to-end by the manual portable mode probe in the 2026-04-11 audit
/// cycle (documented in Phase 7c of revised-architecture-plan.md).
/// </summary>
public class WdcPathsTests
{
    [Fact]
    public void Root_IsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(WdcPaths.Root));
    }

    [Fact]
    public void Root_IsAbsolutePath()
    {
        Assert.True(Path.IsPathRooted(WdcPaths.Root));
    }

    [Fact]
    public void SubRoots_AreAllChildrenOfRoot()
    {
        var root = Path.GetFullPath(WdcPaths.Root);
        var children = new[]
        {
            WdcPaths.BinariesRoot,
            WdcPaths.DataRoot,
            WdcPaths.SitesRoot,
            WdcPaths.GeneratedRoot,
            WdcPaths.SslRoot,
            WdcPaths.LogsRoot,
            WdcPaths.CacheRoot,
            WdcPaths.BackupsRoot,
            WdcPaths.CaddyRoot,
        };
        foreach (var child in children)
        {
            var resolved = Path.GetFullPath(child);
            Assert.StartsWith(root, resolved, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SubRoots_HaveExpectedLeafNames()
    {
        Assert.Equal("binaries", Path.GetFileName(WdcPaths.BinariesRoot));
        Assert.Equal("data", Path.GetFileName(WdcPaths.DataRoot));
        Assert.Equal("sites", Path.GetFileName(WdcPaths.SitesRoot));
        Assert.Equal("generated", Path.GetFileName(WdcPaths.GeneratedRoot));
        Assert.Equal("ssl", Path.GetFileName(WdcPaths.SslRoot));
        Assert.Equal("logs", Path.GetFileName(WdcPaths.LogsRoot));
        Assert.Equal("cache", Path.GetFileName(WdcPaths.CacheRoot));
        Assert.Equal("backups", Path.GetFileName(WdcPaths.BackupsRoot));
        Assert.Equal("caddy", Path.GetFileName(WdcPaths.CaddyRoot));
    }

    [Fact]
    public void IsPortableMode_ReturnsBoolean()
    {
        // Just verify the property is read-safe and returns a bool.
        // Exact value depends on whether WDC_DATA_DIR is set in the
        // environment running the tests; both true and false are valid.
        _ = WdcPaths.IsPortableMode;
    }
}
