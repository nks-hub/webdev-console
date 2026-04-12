using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Plugin.Apache;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Regression tests for <see cref="ApacheConfig"/> default paths.
///
/// Historical bug (2026-04-11): VhostsDirectory defaulted to the relative
/// string "conf/sites-enabled". When Apache was not installed, ApacheModule
/// hit the "No Apache installed" warning path and left VhostsDirectory as
/// the relative default. SiteOrchestrator.ApplyAsync then called
/// GenerateVhostAsync via reflection, which did Directory.CreateDirectory
/// + File.WriteAllTextAsync on the relative path — these resolve against
/// the daemon's current working directory, so vhost fragments leaked out of
/// ~/.wdc and into whatever folder the user had run `dotnet run` from (the
/// repo source tree, in the e2e case). Two .conf files actually ended up
/// committed-ready in src/daemon/NKS.WebDevConsole.Daemon/conf/sites-enabled/.
///
/// These tests lock in the invariant "all writable path defaults are absolute".
/// </summary>
public class ApacheConfigDefaultsTests
{
    [Fact]
    public void VhostsDirectory_Default_IsAbsolutePath()
    {
        var cfg = new ApacheConfig();

        Assert.True(
            Path.IsPathRooted(cfg.VhostsDirectory),
            $"ApacheConfig.VhostsDirectory must default to an absolute path, but was '{cfg.VhostsDirectory}'. " +
            "A relative default would resolve against the daemon cwd and leak files outside ~/.wdc.");
    }

    [Fact]
    public void VhostsDirectory_Default_IsRootedUnderWdcHome()
    {
        var cfg = new ApacheConfig();

        Assert.StartsWith(
            WdcPaths.Root,
            cfg.VhostsDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogDirectory_Default_IsAbsolutePath()
    {
        var cfg = new ApacheConfig();

        Assert.True(
            Path.IsPathRooted(cfg.LogDirectory),
            $"ApacheConfig.LogDirectory must default to an absolute path, but was '{cfg.LogDirectory}'.");
    }

    [Fact]
    public void LogDirectory_Default_IsRootedUnderWdcHome()
    {
        var cfg = new ApacheConfig();

        Assert.StartsWith(
            WdcPaths.Root,
            cfg.LogDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BinariesRoot_Default_IsRootedUnderWdcHome()
    {
        var cfg = new ApacheConfig();

        Assert.True(Path.IsPathRooted(cfg.BinariesRoot));
        Assert.StartsWith(WdcPaths.Root, cfg.BinariesRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultPorts_MatchWellKnownValues()
    {
        var cfg = new ApacheConfig();
        Assert.Equal(80, cfg.HttpPort);
        Assert.Equal(443, cfg.HttpsPort);
    }

    [Fact]
    public void VhostsDirectory_EndsWithExpectedLeaf()
    {
        var cfg = new ApacheConfig();
        Assert.EndsWith("sites-enabled", cfg.VhostsDirectory);
    }
}
