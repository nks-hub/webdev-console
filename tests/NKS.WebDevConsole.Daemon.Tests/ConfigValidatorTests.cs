using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

public class ConfigValidatorTests
{
    private readonly Mock<ILogger<ConfigValidator>> _loggerMock = new();
    private readonly ConfigValidator _validator;

    public ConfigValidatorTests()
    {
        _validator = new ConfigValidator(_loggerMock.Object);
    }

    [Fact]
    public async Task ValidateApacheConfig_NonexistentHttpdPath_ReturnsFalseWithErrorMessage()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), "nonexistent-httpd-" + Guid.NewGuid().ToString("N") + ".exe");

        var (isValid, output) = await _validator.ValidateApacheConfig(fakePath, "dummy.conf");

        Assert.False(isValid);
        Assert.False(string.IsNullOrEmpty(output));
    }

    [Fact]
    public async Task ValidateApacheConfig_NonexistentPath_LogsError()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), "nonexistent-httpd-" + Guid.NewGuid().ToString("N") + ".exe");

        await _validator.ValidateApacheConfig(fakePath, "dummy.conf");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Failed to validate")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ValidatePhpIni_NonexistentPath_ReturnsFalse()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), "nonexistent-php-" + Guid.NewGuid().ToString("N") + ".exe");
        var (isValid, output) = await _validator.ValidatePhpIni(fakePath, "dummy.ini");
        Assert.False(isValid);
        Assert.False(string.IsNullOrEmpty(output));
    }

    [Fact]
    public async Task ValidateMyCnf_NonexistentPath_ReturnsFalse()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), "nonexistent-mysqld-" + Guid.NewGuid().ToString("N") + ".exe");
        var (isValid, output) = await _validator.ValidateMyCnf(fakePath, "dummy.cnf");
        Assert.False(isValid);
        Assert.False(string.IsNullOrEmpty(output));
    }

    [Fact]
    public async Task ValidateRedisConf_NonexistentPath_ReturnsFalse()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), "nonexistent-redis-" + Guid.NewGuid().ToString("N") + ".exe");
        var (isValid, output) = await _validator.ValidateRedisConf(fakePath, "dummy.conf");
        Assert.False(isValid);
        Assert.False(string.IsNullOrEmpty(output));
    }
}
