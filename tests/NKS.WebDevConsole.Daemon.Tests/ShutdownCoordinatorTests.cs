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
}
