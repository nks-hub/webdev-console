using NKS.WebDevConsole.Daemon.Plugin;
using Xunit;

namespace NKS.WebDevConsole.Daemon.Tests;

public class PluginPlatformTests
{
    [Theory]
    [InlineData(null, "windows", true)]
    [InlineData(new string[0], "windows", true)]
    [InlineData(new[] { "macos", "linux" }, "windows", false)]
    [InlineData(new[] { "MacOS", "linux" }, "macos", true)]
    [InlineData(new[] { "windows" }, "windows", true)]
    public void Supported_only_when_listed_or_unrestricted(string[]? platforms, string current, bool expected) =>
        Assert.Equal(expected, PluginPlatform.IsSupported(platforms, current));
}
