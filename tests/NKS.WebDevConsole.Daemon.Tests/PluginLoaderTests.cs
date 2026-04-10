using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Daemon.Plugin;

namespace NKS.WebDevConsole.Daemon.Tests;

public class PluginLoaderTests
{
    private readonly PluginLoader _loader;
    private readonly Mock<ILogger<PluginLoader>> _loggerMock = new();

    public PluginLoaderTests()
    {
        _loader = new PluginLoader(_loggerMock.Object);
    }

    [Fact]
    public void NewLoader_HasNoPlugins()
    {
        Assert.Empty(_loader.Plugins);
    }

    [Fact]
    public void LoadPlugins_NonExistentDirectory_LogsWarningAndReturns()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        _loader.LoadPlugins(fakePath);

        Assert.Empty(_loader.Plugins);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("not found")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LoadPlugins_EmptyDirectory_LoadsNothing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            _loader.LoadPlugins(tempDir);
            Assert.Empty(_loader.Plugins);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadPlugins_SubdirWithoutDll_LogsWarningAndSkips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(tempDir, "FakePlugin");
        Directory.CreateDirectory(subDir);
        try
        {
            _loader.LoadPlugins(tempDir);

            Assert.Empty(_loader.Plugins);
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("No DLL found")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Plugins_IsReadOnly()
    {
        Assert.IsAssignableFrom<IReadOnlyList<LoadedPlugin>>(_loader.Plugins);
    }
}
