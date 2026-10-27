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

    [Fact]
    public void Changed_IgnoresValuesThatMatchTheStoredOnes()
    {
        var stored = new Dictionary<string, string> { ["ports.http"] = "80", ["ports.https"] = "443" };
        var incoming = new Dictionary<string, string>
        {
            ["ports.http"] = "80",        // re-sent, unchanged
            ["ports.https"] = "8443",     // changed
            ["ports.redis"] = "6379",     // first save
        };

        var changed = SettingsKeys.Changed(stored, incoming);

        Assert.Equal(new[] { "ports.https", "ports.redis" }, changed.OrderBy(k => k));
    }

    [Theory]
    [InlineData("sync.accountToken")]
    [InlineData("SYNC.ACCOUNTTOKEN")]
    public void Secret_HidesTheCatalogToken(string name)
    {
        Assert.Contains(name, SettingsKeys.Secret);
    }

    [Fact]
    public void Secret_KeepsDeviceIdentityReadable()
    {
        // The renderer and CLI read these back; they are local-only, not secret.
        Assert.DoesNotContain("sync.deviceId", SettingsKeys.Secret);
        Assert.DoesNotContain("sync.accountEmail", SettingsKeys.Secret);
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
