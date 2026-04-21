using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Covers IPortMetadata contract and the SDK helper types added in task 25a.
/// </summary>
public sealed class PortMetadataTests
{
    // ── ApacheHttpPort defaults ────────────────────────────────────────────

    [Fact]
    public void ApacheHttpPort_Defaults()
    {
        var port = new ApacheHttpPort();

        Assert.Equal("apache.http", port.Key);
        Assert.Equal("HTTP", port.Label);
        Assert.Equal(80, port.DefaultPort);
        Assert.Equal(80, port.CurrentPort);
        Assert.False(port.IsActive);
        Assert.Equal("nks.wdc.apache", port.PluginId);
    }

    [Fact]
    public void ApacheHttpsPort_Defaults()
    {
        var port = new ApacheHttpsPort();

        Assert.Equal("apache.https", port.Key);
        Assert.Equal("HTTPS", port.Label);
        Assert.Equal(443, port.DefaultPort);
        Assert.Equal(443, port.CurrentPort);
        Assert.False(port.IsActive);
        Assert.Equal("nks.wdc.apache", port.PluginId);
    }

    // ── Delegate overrides ─────────────────────────────────────────────────

    [Fact]
    public void ApacheHttpPort_CurrentPort_UsesDelegate()
    {
        var port = new ApacheHttpPort(currentPort: () => 8080);

        Assert.Equal(80, port.DefaultPort);
        Assert.Equal(8080, port.CurrentPort);
    }

    [Fact]
    public void ApacheHttpPort_IsActive_UsesDelegate()
    {
        var port = new ApacheHttpPort(isActive: () => true);

        Assert.True(port.IsActive);
    }

    [Fact]
    public void ApacheHttpsPort_CurrentPort_UsesDelegate()
    {
        var port = new ApacheHttpsPort(currentPort: () => 8443);

        Assert.Equal(443, port.DefaultPort);
        Assert.Equal(8443, port.CurrentPort);
    }

    // ── IPortMetadata interface assignability ──────────────────────────────

    [Fact]
    public void ApacheHttpPort_ImplementsIPortMetadata()
    {
        Assert.IsAssignableFrom<IPortMetadata>(new ApacheHttpPort());
    }

    [Fact]
    public void ApacheHttpsPort_ImplementsIPortMetadata()
    {
        Assert.IsAssignableFrom<IPortMetadata>(new ApacheHttpsPort());
    }

    // ── Custom PortMetadataBase subclass ───────────────────────────────────

    private sealed class TestPort : PortMetadataBase
    {
        public override string Key => "test.port";
        public override string Label => "Test";
        public override int DefaultPort => 9999;
        public override string PluginId => "nks.wdc.test";
    }

    [Fact]
    public void PortMetadataBase_DefaultCurrentPortEqualsDefaultPort()
    {
        IPortMetadata port = new TestPort();

        Assert.Equal(port.DefaultPort, port.CurrentPort);
    }

    [Fact]
    public void PortMetadataBase_DefaultIsActiveFalse()
    {
        IPortMetadata port = new TestPort();

        Assert.False(port.IsActive);
    }
}
