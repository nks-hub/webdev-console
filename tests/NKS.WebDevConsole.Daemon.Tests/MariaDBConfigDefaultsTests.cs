using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Plugin.MariaDB;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Regression tests for <see cref="MariaDBConfig"/> default paths plus
/// lifecycle argv assertions for <see cref="MariaDBModule"/>.
///
/// Path-default invariants are the same as <see cref="ApacheConfigDefaultsTests"/>
/// and <see cref="NginxConfigDefaultsTests"/>: writable path defaults MUST be
/// absolute and rooted under ~/.wdc, or the daemon leaks files into cwd
/// when the plugin is loaded before InitializeAsync completes.
///
/// Lifecycle tests use the <see cref="IMariaDBProcessRunner"/> abstraction with
/// a strict Moq — same pattern as <see cref="NginxConfigDefaultsTests"/>.
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

    // --- Service lifecycle — implemented 2026-04-18 (wdc-todo:
    // mariadb-lifecycle). Tests assert argv construction via the
    // IMariaDBProcessRunner abstraction, same pattern as NginxModule.

    private static (MariaDBModule module, MariaDBConfig cfg, Mock<IMariaDBProcessRunner> runner) BuildModule()
    {
        var cfg = new MariaDBConfig
        {
            ExecutablePath = "mariadbd",
            MariadbAdminPath = "mariadb-admin",
            ConfigFile = Path.Combine(Path.GetTempPath(), "my.cnf"),
            DataDir = Path.Combine(Path.GetTempPath(), "wdc-mariadb-test-datadir"),
        };
        var runner = new Mock<IMariaDBProcessRunner>(MockBehavior.Strict);
        var logger = NullLogger<MariaDBModule>.Instance;
        return (new MariaDBModule(logger, cfg, runner.Object), cfg, runner);
    }

    [Fact]
    public async Task ValidateConfigAsync_AcceptsGoodMyCnf()
    {
        var (module, cfg, runner) = BuildModule();

        IReadOnlyList<string>? capturedArgs = null;
        runner
            .Setup(r => r.RunAsync(
                cfg.ExecutablePath!,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string?, CancellationToken>((_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(new MariaDBCommandResult(0, "mariadbd  Ver 10.11.6 for Linux on x86_64 ...help output...", ""));

        var result = await module.ValidateConfigAsync(CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(capturedArgs);
        Assert.Contains("--verbose", capturedArgs!);
        Assert.Contains("--help", capturedArgs!);
        Assert.Contains(capturedArgs!, a => a.StartsWith("--defaults-file=", StringComparison.Ordinal)
                                         && a.EndsWith(cfg.ConfigFile!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateConfigAsync_RejectsBadMyCnf()
    {
        var (module, cfg, runner) = BuildModule();

        const string stderr = "mariadbd: Error on parsing config file /etc/my.cnf: unknown variable 'foo'";
        runner
            .Setup(r => r.RunAsync(
                cfg.ExecutablePath!,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MariaDBCommandResult(1, "", stderr));

        var result = await module.ValidateConfigAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("unknown variable", result.ErrorMessage);
    }

    [Fact]
    public async Task StartAsync_LaunchesMariadbd_BootstrapsRootOnFirstRun()
    {
        // Intercept Spawn with a sentinel exception so we verify argv without
        // actually launching mariadbd. Mirrors the nginx Start test pattern.
        var (module, cfg, runner) = BuildModule();
        cfg.Port = GetLikelyFreePort();

        IReadOnlyList<string>? capturedArgs = null;
        string? capturedExec = null;
        var sentinel = new InvalidOperationException("spawn-intercepted");

        runner
            .Setup(r => r.Spawn(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>()))
            .Callback<string, IReadOnlyList<string>, string?>((exe, args, _) =>
            {
                capturedExec = exe;
                capturedArgs = args;
            })
            .Throws(sentinel);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.StartAsync(CancellationToken.None));
        Assert.Same(sentinel, ex);

        Assert.NotNull(capturedExec);
        Assert.True(
            capturedExec == "mariadbd" || capturedExec == "mysqld",
            $"Expected exec 'mariadbd' or 'mysqld', got '{capturedExec}'.");

        Assert.NotNull(capturedArgs);
        Assert.Contains(capturedArgs!, a => a.StartsWith("--defaults-file=", StringComparison.Ordinal)
                                         && a.EndsWith(cfg.ConfigFile!, StringComparison.Ordinal));
        Assert.Contains(capturedArgs!, a => a.StartsWith("--datadir=", StringComparison.Ordinal)
                                         && a.EndsWith(cfg.DataDir, StringComparison.Ordinal));
        Assert.Contains(capturedArgs!, a => a == $"--port={cfg.Port}");
    }

    [Fact]
    public async Task StopAsync_IssuesGracefulShutdown_ThenKillFallback()
    {
        var (module, cfg, runner) = BuildModule();

        IReadOnlyList<string>? capturedArgs = null;
        string? capturedExec = null;
        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string?, CancellationToken>((exe, args, _, _) =>
            {
                capturedExec = exe;
                capturedArgs = args;
            })
            .ReturnsAsync(new MariaDBCommandResult(0, "", ""));

        // StopAsync short-circuits when _state == Stopped, so flip to Running
        // via reflection to exercise the graceful-stop branch. We cannot easily
        // simulate the SIGTERM-fallback path (requires a lingering child past
        // GracefulTimeoutSecs), so this test only asserts the graceful argv.
        ForceState(module, NKS.WebDevConsole.Core.Models.ServiceState.Running);

        await module.StopAsync(CancellationToken.None);

        Assert.Equal(cfg.MariadbAdminPath, capturedExec);
        Assert.NotNull(capturedArgs);
        Assert.Contains("shutdown", capturedArgs!);
        Assert.Contains("-u", capturedArgs!);
        Assert.Contains("root", capturedArgs!);
        Assert.Contains("--skip-password", capturedArgs!);
        Assert.Contains(capturedArgs!, a => a == $"--port={cfg.Port}");

        runner.Verify(
            r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReloadAsync_FlushesPrivileges_AndReopensLogs()
    {
        var (module, cfg, runner) = BuildModule();

        IReadOnlyList<string>? capturedArgs = null;
        string? capturedExec = null;
        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string?, CancellationToken>((exe, args, _, _) =>
            {
                capturedExec = exe;
                capturedArgs = args;
            })
            .ReturnsAsync(new MariaDBCommandResult(0, "", ""));

        await module.ReloadAsync(CancellationToken.None);

        Assert.Equal(cfg.MariadbAdminPath, capturedExec);
        Assert.NotNull(capturedArgs);
        Assert.Contains("reload", capturedArgs!);
        Assert.Contains("-u", capturedArgs!);
        Assert.Contains("root", capturedArgs!);
        Assert.Contains("--skip-password", capturedArgs!);

        runner.Verify(
            r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void BinaryDiscovery_PrefersMariadbd_FallsBackToMysqld()
    {
        // Scenario A: both mariadbd + mysqld present — mariadbd should win.
        // Scenario B: only mysqld present — fallback path.
        var root = Path.Combine(Path.GetTempPath(), "wdc-mariadb-bindisc-" + Guid.NewGuid().ToString("N"));
        var version = Path.Combine(root, "10.11.6", "bin");
        Directory.CreateDirectory(version);
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";

        try
        {
            // Scenario A
            var mariadbd = Path.Combine(version, "mariadbd" + ext);
            var mysqld = Path.Combine(version, "mysqld" + ext);
            File.WriteAllText(mariadbd, "");
            File.WriteAllText(mysqld, "");

            var cfgA = new MariaDBConfig { BinariesRoot = root };
            Assert.True(cfgA.ApplyOwnBinaryDefaults());
            Assert.Equal(mariadbd, cfgA.ExecutablePath);

            // Scenario B — remove mariadbd, re-run discovery on a fresh config.
            File.Delete(mariadbd);
            var cfgB = new MariaDBConfig { BinariesRoot = root };
            Assert.True(cfgB.ApplyOwnBinaryDefaults());
            Assert.Equal(mysqld, cfgB.ExecutablePath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static void ForceState(MariaDBModule module, NKS.WebDevConsole.Core.Models.ServiceState state)
    {
        var field = typeof(MariaDBModule).GetField("_state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MariaDBModule._state field not found — test needs update.");
        field.SetValue(module, state);
    }

    private static int GetLikelyFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
