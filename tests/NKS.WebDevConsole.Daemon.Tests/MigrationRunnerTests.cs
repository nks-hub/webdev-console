using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Tests;

public class MigrationRunnerTests
{
    private readonly MigrationRunner _runner;
    private readonly Mock<ILogger<MigrationRunner>> _loggerMock = new();

    public MigrationRunnerTests()
    {
        _runner = new MigrationRunner(_loggerMock.Object);
    }

    [Fact]
    public void Run_WithInMemoryDb_ReturnsTrue()
    {
        // DbUp with SQLite in-memory should succeed even with no embedded scripts
        // matching the filter (since no upgrade is required)
        var result = _runner.Run("Data Source=:memory:");
        Assert.True(result);
    }

    [Fact]
    public void Run_CalledTwice_SecondCallReportsUpToDate()
    {
        const string cs = "Data Source=:memory:";
        _runner.Run(cs);
        var result = _runner.Run(cs);

        Assert.True(result);
    }
}
