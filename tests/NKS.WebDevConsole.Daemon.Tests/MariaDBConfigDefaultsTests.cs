using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Plugin.MariaDB;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Regression tests for <see cref="MariaDBConfig"/> default paths.
///
/// Same invariant family as <see cref="ApacheConfigDefaultsTests"/> and
/// <see cref="NginxConfigDefaultsTests"/>: writable path defaults MUST be
/// absolute and rooted under ~/.wdc, or the daemon leaks files into cwd
/// when the plugin is loaded before InitializeAsync completes.
/// </summary>
public class MariaDBConfigDefaultsTests
{
    [Fact]
    public void BinariesRoot_Default_IsAbsolutePath()
    {
        var cfg = new MariaDBConfig();

        Assert.True(
            Path.IsPathRooted(cfg.BinariesRoot),
            $"MariaDBConfig.BinariesRoot must default to an absolute path, but was '{cfg.BinariesRoot}'.");
    }

    [Fact]
    public void DataDir_Default_IsAbsolutePath()
    {
        var cfg = new MariaDBConfig();

        Assert.True(
            Path.IsPathRooted(cfg.DataDir),
            $"MariaDBConfig.DataDir must default to an absolute path, but was '{cfg.DataDir}'. "
                + "Relative default would resolve against daemon cwd and scatter InnoDB files outside ~/.wdc.");
    }

    [Fact]
    public void LogDirectory_Default_IsAbsolutePath()
    {
        var cfg = new MariaDBConfig();

        Assert.True(
            Path.IsPathRooted(cfg.LogDirectory),
            $"MariaDBConfig.LogDirectory must default to an absolute path, but was '{cfg.LogDirectory}'.");
    }

    [Fact]
    public void Port_Default_Is3306()
    {
        var cfg = new MariaDBConfig();

        Assert.Equal(3306, cfg.Port);
    }

    [Fact]
    public void BinariesRoot_Default_IsRootedUnderWdcHome()
    {
        var cfg = new MariaDBConfig();

        var binariesRoot = Path.GetFullPath(WdcPaths.BinariesRoot);
        var cfgRoot = Path.GetFullPath(cfg.BinariesRoot);

        Assert.StartsWith(
            binariesRoot,
            cfgRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    // --- Service lifecycle — stubbed in scaffold commit 19c041d
    // (2026-04-19). Re-enable once MariaDBModule.{Validate,Start,Stop,
    // Reload}Async are implemented (see wdc-todo:mariadb-lifecycle in
    // MCP memory). ----------------------------------------------------

    [Fact(Skip = "todo — MariaDBModule.ValidateConfigAsync not yet implemented; `mariadbd --verbose --help --defaults-file=` wrapper pending.")]
    public void ValidateConfigAsync_AcceptsGoodMyCnf() { }

    [Fact(Skip = "todo — MariaDBModule.StartAsync not yet implemented; launch + port-bind wait + DPAPI root password bootstrap pending.")]
    public void StartAsync_LaunchesMariadbd_BootstrapsRootOnFirstRun() { }

    [Fact(Skip = "todo — MariaDBModule.StopAsync not yet implemented; `mariadb-admin shutdown` + SIGTERM fallback pending.")]
    public void StopAsync_IssuesGracefulShutdown_ThenKillFallback() { }

    [Fact(Skip = "todo — MariaDBModule.ReloadAsync not yet implemented; `mariadb-admin reload` (flush privileges + reopen logs) pending.")]
    public void ReloadAsync_FlushesPrivileges_AndReopensLogs() { }

    [Fact(Skip = "todo — dual-name binary discovery test; verify mariadbd preferred over mysqld when both present, and mysqld-only tarball still works.")]
    public void BinaryDiscovery_PrefersMariadbd_FallsBackToMysqld() { }
}
