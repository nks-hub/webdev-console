using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Plugin.Nginx;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Regression tests for <see cref="NginxConfig"/> default paths.
///
/// Same invariant as <see cref="ApacheConfigDefaultsTests"/>: writable path
/// defaults MUST be absolute and rooted under ~/.wdc, or the daemon will
/// leak vhost fragments into its cwd if nginx is not installed yet and
/// SiteOrchestrator.ApplyAsync runs before InitializeAsync. Apache hit this
/// bug in the wild (2026-04-11); the same class of leak is prevented here.
/// </summary>
public class NginxConfigDefaultsTests
{
    [Fact]
    public void VhostsDirectory_Default_IsAbsolutePath()
    {
        var cfg = new NginxConfig();

        Assert.True(
            Path.IsPathRooted(cfg.VhostsDirectory),
            $"NginxConfig.VhostsDirectory must default to an absolute path, but was '{cfg.VhostsDirectory}'. "
                + "A relative default would resolve against the daemon cwd and leak files outside ~/.wdc.");
    }

    [Fact]
    public void LogDirectory_Default_IsAbsolutePath()
    {
        var cfg = new NginxConfig();

        Assert.True(
            Path.IsPathRooted(cfg.LogDirectory),
            $"NginxConfig.LogDirectory must default to an absolute path, but was '{cfg.LogDirectory}'.");
    }

    [Fact]
    public void BinariesRoot_Default_IsAbsolutePath()
    {
        var cfg = new NginxConfig();

        Assert.True(
            Path.IsPathRooted(cfg.BinariesRoot),
            $"NginxConfig.BinariesRoot must default to an absolute path, but was '{cfg.BinariesRoot}'.");
    }

    [Fact]
    public void VhostsDirectory_Default_IsRootedUnderWdcHome()
    {
        var cfg = new NginxConfig();

        // Normalize separators so the prefix compare is OS-agnostic.
        var generatedRoot = Path.GetFullPath(WdcPaths.GeneratedRoot);
        var vhostsDir = Path.GetFullPath(cfg.VhostsDirectory);

        Assert.StartsWith(
            generatedRoot,
            vhostsDir,
            StringComparison.OrdinalIgnoreCase);
    }

    // --- Service lifecycle — stubbed in the scaffold commit (2026-04-19,
    // commit 76538ef). Re-enable these once NginxModule.{Validate,Start,
    // Stop,Reload}Async are implemented (see wdc-todo:nginx-lifecycle in
    // MCP memory for acceptance criteria). ---------------------------

    [Fact(Skip = "todo — NginxModule.ValidateConfigAsync throws NotImplementedException in scaffold; implement `nginx -t -c` shell-out, then enable.")]
    public void ValidateConfigAsync_AcceptsGoodConfig() { }

    [Fact(Skip = "todo — NginxModule.ValidateConfigAsync not yet implemented; syntax-error case pending.")]
    public void ValidateConfigAsync_RejectsBadConfig() { }

    [Fact(Skip = "todo — NginxModule.StartAsync not yet implemented; ProcessManager invocation + log tailing pending.")]
    public void StartAsync_LaunchesNginxBinary_AndRegistersWithJobObject() { }

    [Fact(Skip = "todo — NginxModule.StopAsync not yet implemented; `nginx -s quit` graceful path pending.")]
    public void StopAsync_IssuesQuitSignal_ThenSigtermFallback() { }

    [Fact(Skip = "todo — NginxModule.ReloadAsync not yet implemented; `nginx -s reload` unified cross-platform pending.")]
    public void ReloadAsync_IssuesReloadSignal() { }
}
