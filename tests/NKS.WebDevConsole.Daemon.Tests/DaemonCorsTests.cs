using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

public class DaemonCorsTests
{
    [Theory]
    [InlineData("app://nks-wdc", true)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://127.0.0.1:5173", true)]
    [InlineData("http://[::1]:5173", true)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("http://localhost.evil.example.com", false)]
    [InlineData("app://other-app", false)]
    [InlineData("null", false)]
    [InlineData("file://", false)]
    public void Only_the_app_and_loopback_dev_origins_are_allowed(string origin, bool expected)
    {
        Assert.Equal(expected, DaemonCors.IsAllowedOrigin(origin));
    }
}
