using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

public class DaemonCorsTests
{
    [Theory]
    [InlineData("app://nks-wdc", true)]
    [InlineData("http://127.0.0.1:5190", true)]
    [InlineData("http://localhost:5190", true)]
    [InlineData("http://[::1]:5191", true)]
    [InlineData("http://127.0.0.1:5199", true)]
    [InlineData("http://localhost:5173", false)]
    [InlineData("http://127.0.0.1:5200", false)]
    [InlineData("http://127.0.0.1:80", false)]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost:5190", false)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("http://localhost.evil.example.com:5190", false)]
    [InlineData("app://other-app", false)]
    [InlineData("null", false)]
    [InlineData("file://", false)]
    public void Only_the_app_and_the_vite_dev_server_origins_are_allowed(string origin, bool expected)
    {
        Assert.Equal(expected, DaemonCors.IsAllowedOrigin(origin));
    }
}
