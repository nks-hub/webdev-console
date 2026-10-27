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
    public void ForRead_FreshInstallShowsTheEffectiveDefaults()
    {
        var view = SettingsKeys.ForRead([]);

        Assert.Equal("80", view["ports.http"]);
        Assert.Equal("24", view["backup.scheduleHours"]);
        Assert.Equal("true", view["deploy.enabled"]);
        Assert.Equal("false", view["mcp.enabled"]);
        // Every MCP capability group appears, switched off by default.
        Assert.All(NKS.WebDevConsole.Daemon.Mcp.McpCapabilities.All,
            c => Assert.Equal("false", view[c.SettingKey]));
        // Run-time resolved values have no fixed default.
        Assert.False(view.ContainsKey("ports.mysql"));
        Assert.False(view.ContainsKey("daemon.catalogUrl"));
    }

    [Fact]
    public void ForRead_StoredValuesWinAndSecretsStayHidden()
    {
        var view = SettingsKeys.ForRead(new Dictionary<string, string>
        {
            ["ports.http"] = "8080",
            ["MCP.enabled"] = "true",
            ["sync.deviceId"] = "dev-1",
            ["sync.accountToken"] = "eyJhbGciOi",
        });

        Assert.Equal("8080", view["ports.http"]);
        Assert.Equal("true", view["mcp.enabled"]);
        Assert.Equal("dev-1", view["sync.deviceId"]);
        Assert.False(view.ContainsKey("sync.accountToken"));
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
