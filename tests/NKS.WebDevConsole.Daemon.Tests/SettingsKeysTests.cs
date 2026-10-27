using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class SettingsKeysTests
{
    [Theory]
    [InlineData("backup.scheduleHours", "backup", "scheduleHours")]
    [InlineData("backup.content.ssl", "backup", "content.ssl")]
    public void TrySplit_SplitsOnFirstDot(string composite, string category, string key)
    {
        Assert.True(SettingsKeys.TrySplit(composite, out var c, out var k));
        Assert.Equal(category, c);
        Assert.Equal(key, k);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("value")]
    [InlineData(".key")]
    [InlineData("backup.")]
    [InlineData("")]
    public void TrySplit_RejectsNamesWithoutCategoryOrKey(string composite)
    {
        Assert.False(SettingsKeys.TrySplit(composite, out _, out _));
    }
}
