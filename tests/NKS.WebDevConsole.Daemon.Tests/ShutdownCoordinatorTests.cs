using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

public class ShutdownCoordinatorTests
{
    private readonly Mock<ILogger<ShutdownCoordinator>> _loggerMock = new();

    [Fact]
    public async Task StopAllAsync_StopsRunningServices_StopsPlugins_AndDeletesPortFile()
    {
        var runningModule = new Mock<IServiceModule>();
        runningModule.SetupGet(x => x.ServiceId).Returns("apache");
        runningModule.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceStatus("apache", "Apache", ServiceState.Running, 1234, 0, 0, null));

        var stoppedModule = new Mock<IServiceModule>();
        stoppedModule.SetupGet(x => x.ServiceId).Returns("mysql");
        stoppedModule.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceStatus("mysql", "MySQL", ServiceState.Stopped, null, 0, 0, null));

        var plugin = new Mock<IWdcPlugin>();
        plugin.SetupGet(x => x.Id).Returns("nks.wdc.ssl");
        plugin.SetupGet(x => x.DisplayName).Returns("SSL");
        plugin.SetupGet(x => x.Version).Returns("1.0.0");

        var portFile = Path.Combine(Path.GetTempPath(), $"nks-wdc-shutdown-{Guid.NewGuid():N}.port");
        await File.WriteAllTextAsync(portFile, "5146\ntoken");

        var sut = new ShutdownCoordinator(_loggerMock.Object);
        await sut.StopAllAsync(
            [runningModule.Object, stoppedModule.Object],
            [new LoadedPlugin(plugin.Object, typeof(ShutdownCoordinator).Assembly, AssemblyLoadContext.Default)],
            portFile,
            CancellationToken.None);

        runningModule.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        stoppedModule.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
        plugin.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(File.Exists(portFile));
    }

    [Fact]
    public async Task StopAllAsync_Continues_WhenServiceOrPluginStopFails()
    {
        var brokenModule = new Mock<IServiceModule>();
        brokenModule.SetupGet(x => x.ServiceId).Returns("redis");
        brokenModule.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceStatus("redis", "Redis", ServiceState.Running, 99, 0, 0, null));
        brokenModule.Setup(x => x.StopAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var healthyModule = new Mock<IServiceModule>();
        healthyModule.SetupGet(x => x.ServiceId).Returns("mailpit");
        healthyModule.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceStatus("mailpit", "Mailpit", ServiceState.Running, 100, 0, 0, null));

        var brokenPlugin = new Mock<IWdcPlugin>();
        brokenPlugin.SetupGet(x => x.Id).Returns("nks.wdc.redis");
        brokenPlugin.SetupGet(x => x.DisplayName).Returns("Redis");
        brokenPlugin.SetupGet(x => x.Version).Returns("1.0.0");
        brokenPlugin.Setup(x => x.StopAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("plugin boom"));

        var sut = new ShutdownCoordinator(_loggerMock.Object);
        await sut.StopAllAsync(
            [brokenModule.Object, healthyModule.Object],
            [new LoadedPlugin(brokenPlugin.Object, typeof(ShutdownCoordinator).Assembly, AssemblyLoadContext.Default)],
            Path.Combine(Path.GetTempPath(), $"nks-wdc-shutdown-{Guid.NewGuid():N}.port"),
            CancellationToken.None);

        healthyModule.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        brokenPlugin.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopAllAsync_RunsModuleStopsConcurrently()
    {
        // Regression for 15e10d7 — sequential foreach-await would make
        // three modules each taking 500 ms run in ~1.5 s wall time.
        // Task.WhenAll fans out so the total is bounded by the slowest
        // (~500 ms). A 900 ms ceiling gives ample slack for CI jitter
        // while still proving parallel execution.
        static Mock<IServiceModule> MakeSlow(string id, TimeSpan delay)
        {
            var m = new Mock<IServiceModule>();
            m.SetupGet(x => x.ServiceId).Returns(id);
            m.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ServiceStatus(id, id, ServiceState.Running, 1, 0, 0, null));
            m.Setup(x => x.StopAsync(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(async ct => await Task.Delay(delay, ct));
            return m;
        }

        var modules = new[]
        {
            MakeSlow("apache", TimeSpan.FromMilliseconds(500)),
            MakeSlow("mysql", TimeSpan.FromMilliseconds(500)),
            MakeSlow("redis", TimeSpan.FromMilliseconds(500)),
        };

        var sut = new ShutdownCoordinator(_loggerMock.Object);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.StopAllAsync(
            modules.Select(m => m.Object),
            [],
            Path.Combine(Path.GetTempPath(), $"nks-wdc-shutdown-{Guid.NewGuid():N}.port"),
            CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 900,
            $"Parallel shutdown should finish near the slowest module's 500 ms, took {sw.ElapsedMilliseconds} ms");
        foreach (var m in modules) m.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopAllAsync_Continues_WhenServiceStopTimesOut()
    {
        var hangingModule = new Mock<IServiceModule>();
        hangingModule.SetupGet(x => x.ServiceId).Returns("apache");
        hangingModule.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceStatus("apache", "Apache", ServiceState.Running, 12, 0, 0, null));
        hangingModule.Setup(x => x.StopAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => Task.Delay(TimeSpan.FromMinutes(1), ct));

        var healthyModule = new Mock<IServiceModule>();
        healthyModule.SetupGet(x => x.ServiceId).Returns("redis");
        healthyModule.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceStatus("redis", "Redis", ServiceState.Running, 13, 0, 0, null));

        var sut = new ShutdownCoordinator(_loggerMock.Object, TimeSpan.FromMilliseconds(100));
        await sut.StopAllAsync(
            [hangingModule.Object, healthyModule.Object],
            [],
            Path.Combine(Path.GetTempPath(), $"nks-wdc-shutdown-{Guid.NewGuid():N}.port"),
            CancellationToken.None);

        healthyModule.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
