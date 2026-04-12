using NKS.WebDevConsole.Daemon.Binaries;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class BinaryManagerValidationTests
{
    [Theory]
    [InlineData("apache", "2.4.62")]
    [InlineData("php", "8.4.2")]
    [InlineData("node", "22.15.0")]
    [InlineData("mysql", "8.0.35")]
    [InlineData("redis-windows", "7.4.1")]
    [InlineData("a1", "1.0")]
    public void ValidateAppVersion_AcceptsValidIdentifiers(string app, string version)
    {
        BinaryManager.ValidateAppVersion(app, version);
    }

    [Theory]
    [InlineData("../etc", "1.0")]
    [InlineData("..\\passwd", "1.0")]
    [InlineData("", "1.0")]
    [InlineData("app", "")]
    [InlineData("app with spaces", "1.0")]
    [InlineData(".hidden", "1.0")]
    [InlineData("app", "../../../etc/passwd")]
    [InlineData("app", "1.0; rm -rf /")]
    public void ValidateAppVersion_RejectsTraversalAndInvalid(string app, string version)
    {
        Assert.Throws<ArgumentException>(() => BinaryManager.ValidateAppVersion(app, version));
    }
}
