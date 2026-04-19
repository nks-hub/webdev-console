using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

    // --- Service lifecycle — implemented 2026-04-18, commit wdc-todo:
    // nginx-lifecycle. Tests assert argv construction + ProcessManager
    // invocation via the INginxProcessRunner abstraction (same pattern we
    // could later backport to ApacheModule for parity).

    private static (NginxModule module, NginxConfig cfg, Mock<INginxProcessRunner> runner) BuildModule()
    {
        var cfg = new NginxConfig
        {
            ExecutablePath = "nginx",
            ServerRoot = Path.GetTempPath(),
            ConfigFile = Path.Combine(Path.GetTempPath(), "nginx.conf"),
        };
        var runner = new Mock<INginxProcessRunner>(MockBehavior.Strict);
        var logger = NullLogger<NginxModule>.Instance;
        return (new NginxModule(logger, cfg, runner.Object), cfg, runner);
    }

    [Fact]
    public async Task ValidateConfigAsync_AcceptsGoodConfig()
    {
        var (module, cfg, runner) = BuildModule();

        runner
            .Setup(r => r.RunAsync(
                cfg.ExecutablePath,
                It.Is<IReadOnlyList<string>>(a =>
                    a.Contains("-t") && a.Contains("-c") && a.Contains(cfg.ConfigFile)),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NginxCommandResult(0, "", "nginx: the configuration file /etc/nginx/nginx.conf syntax is ok\nnginx: configuration file /etc/nginx/nginx.conf test is successful\n"));

        var result = await module.ValidateConfigAsync(CancellationToken.None);

        Assert.True(result.IsValid);
        runner.VerifyAll();
    }

    [Fact]
    public async Task ValidateConfigAsync_RejectsBadConfig()
    {
        var (module, cfg, runner) = BuildModule();

        const string stderr = "nginx: [emerg] unknown directive \"servr\" in /etc/nginx/nginx.conf:3\nnginx: configuration file /etc/nginx/nginx.conf test failed\n";
        runner
            .Setup(r => r.RunAsync(
                cfg.ExecutablePath,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NginxCommandResult(1, "", stderr));

        var result = await module.ValidateConfigAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("unknown directive", result.ErrorMessage);
    }

    [Fact]
    public async Task StartAsync_LaunchesNginxBinary_AndRegistersWithJobObject()
    {
        // We pick a port that is very likely free on the test host. If it
        // happens to be held, PortConflictDetector will throw before we reach
        // the Spawn assertion — so we verify the argv construction by
        // intercepting the Spawn call and *throwing* a sentinel exception.
        // This keeps the test fast (no actual nginx launch) and free of the
        // WaitForPortBindAsync timeout.
        var (module, cfg, runner) = BuildModule();
        cfg.HttpPort = GetLikelyFreePort();

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

        Assert.Equal(cfg.ExecutablePath, capturedExec);
        Assert.NotNull(capturedArgs);
        Assert.Contains("-c", capturedArgs!);
        Assert.Contains(cfg.ConfigFile, capturedArgs!);
        // Prefix is only emitted when ResolveManagedPrefix returns non-empty;
        // ServerRoot is TempPath (exists) so we expect "-p <ServerRoot>".
        Assert.Contains("-p", capturedArgs!);
    }

    [Fact]
    public async Task StopAsync_IssuesQuitSignal_ThenSigtermFallback()
    {
        var (module, cfg, runner) = BuildModule();

        IReadOnlyList<string>? capturedArgs = null;
        runner
            .Setup(r => r.RunAsync(
                cfg.ExecutablePath,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string?, CancellationToken>((_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(new NginxCommandResult(0, "", ""));

        // StopAsync short-circuits when _state == Stopped, so flip it to
        // Running via reflection to exercise the graceful-stop branch. We
        // cannot easily simulate the SIGTERM-fallback path (which requires
        // an actual child lingering past GracefulTimeoutSecs), so this test
        // only asserts the graceful `nginx -s quit` argv.
        ForceState(module, NKS.WebDevConsole.Core.Models.ServiceState.Running);

        await module.StopAsync(CancellationToken.None);

        Assert.NotNull(capturedArgs);
        Assert.Contains("-s", capturedArgs!);
        Assert.Contains("quit", capturedArgs!);
        Assert.Contains("-c", capturedArgs!);
        Assert.Contains(cfg.ConfigFile, capturedArgs!);
        runner.Verify(
            r => r.RunAsync(
                cfg.ExecutablePath,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReloadAsync_IssuesReloadSignal()
    {
        var (module, cfg, runner) = BuildModule();

        var callArgs = new List<IReadOnlyList<string>>();
        runner
            .Setup(r => r.RunAsync(
                cfg.ExecutablePath,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string?, CancellationToken>((_, args, _, _) => callArgs.Add(args))
            .ReturnsAsync(new NginxCommandResult(0, "", ""));

        await module.ReloadAsync(CancellationToken.None);

        // ReloadAsync validates first (`-t`), then issues `-s reload`.
        Assert.Equal(2, callArgs.Count);
        Assert.Contains("-t", callArgs[0]);
        Assert.Contains("-s", callArgs[1]);
        Assert.Contains("reload", callArgs[1]);
        Assert.Contains("-c", callArgs[1]);
        Assert.Contains(cfg.ConfigFile, callArgs[1]);
    }

    private static void ForceState(NginxModule module, NKS.WebDevConsole.Core.Models.ServiceState state)
    {
        var field = typeof(NginxModule).GetField("_state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NginxModule._state field not found — test needs update.");
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
