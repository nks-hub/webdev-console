using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NKS.WebDevConsole.Daemon.Plugin;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="PluginContext"/>. The init-phase context intentionally
/// throws on any service resolution — plugins calling ServiceProvider.GetService
/// during Initialize() will get a clear error rather than a silent null.
/// Without this guard, plugins would fail at runtime with confusing NREs when
/// they try to use services that aren't yet registered.
/// </summary>
public sealed class PluginContextTests
{
    [Fact]
    public void Constructor_ExposesServiceProvider()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var factory = NullLoggerFactory.Instance;

        var ctx = new PluginContext(sp, factory);

        Assert.Same(sp, ctx.ServiceProvider);
    }

    [Fact]
    public void GetLogger_DelegatesToLoggerFactory()
    {
        var factory = new Mock<ILoggerFactory>();
        var logger = new Mock<ILogger<PluginContextTests>>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(logger.Object);

        var ctx = new PluginContext(new ServiceCollection().BuildServiceProvider(), factory.Object);
        var result = ctx.GetLogger<PluginContextTests>();

        Assert.NotNull(result);
        factory.Verify(f => f.CreateLogger(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ForInitPhase_CreatesContextWithThrowingServiceProvider()
    {
        var ctx = PluginContext.ForInitPhase(NullLoggerFactory.Instance);

        Assert.NotNull(ctx);
        Assert.NotNull(ctx.ServiceProvider);
    }

    [Fact]
    public void ForInitPhase_ServiceProvider_ThrowsOnGetService()
    {
        var ctx = PluginContext.ForInitPhase(NullLoggerFactory.Instance);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ctx.ServiceProvider.GetService(typeof(ILoggerFactory)));

        Assert.Contains("initialization", ex.Message);
        Assert.Contains("StartAsync", ex.Message);
    }

    [Fact]
    public void ForInitPhase_ServiceProvider_ErrorMessageIncludesTypeName()
    {
        var ctx = PluginContext.ForInitPhase(NullLoggerFactory.Instance);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ctx.ServiceProvider.GetService(typeof(IDisposable)));

        Assert.Contains("IDisposable", ex.Message);
    }

    [Fact]
    public void ForInitPhase_GetLogger_StillWorks()
    {
        // Init-phase plugins can log even though they can't resolve other services
        var ctx = PluginContext.ForInitPhase(NullLoggerFactory.Instance);
        var logger = ctx.GetLogger<PluginContextTests>();
        Assert.NotNull(logger);
    }
}
