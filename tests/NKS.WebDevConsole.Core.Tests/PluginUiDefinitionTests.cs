using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Core.Tests;

public class PluginUiDefinitionTests
{
    [Fact]
    public void PluginUiDefinition_ConstructsCorrectly()
    {
        var panels = new[]
        {
            new PanelDef("service-status-card", new Dictionary<string, object> { ["serviceId"] = "apache" })
        };
        var def = new PluginUiDefinition("apache-plugin", "Web Servers", "icon-apache", panels);

        Assert.Equal("apache-plugin", def.PluginId);
        Assert.Equal("Web Servers", def.Category);
        Assert.Equal("icon-apache", def.Icon);
        Assert.Single(def.Panels);
        Assert.Equal("service-status-card", def.Panels[0].Type);
    }

    [Fact]
    public void PanelDef_Props_ContainExpectedKeys()
    {
        var props = new Dictionary<string, object> { ["serviceId"] = "mysql", ["maxLines"] = 500 };
        var panel = new PanelDef("log-viewer", props);

        Assert.Equal("log-viewer", panel.Type);
        Assert.Equal("mysql", panel.Props["serviceId"]);
        Assert.Equal(500, panel.Props["maxLines"]);
    }

    [Fact]
    public void PluginUiDefinition_EmptyPanels_IsValid()
    {
        var def = new PluginUiDefinition("empty-plugin", "Other", "icon-default", []);

        Assert.Empty(def.Panels);
    }

    [Fact]
    public void PluginUiDefinition_JsonRoundtrip_PreservesFields()
    {
        var panels = new[]
        {
            new PanelDef("service-status-card", new Dictionary<string, object> { ["serviceId"] = "apache" }),
            new PanelDef("log-viewer", new Dictionary<string, object> { ["serviceId"] = "apache", ["lines"] = 100 }),
        };
        var def = new PluginUiDefinition("nks.wdc.apache", "Web Servers", "icon-apache", panels);

        var json = System.Text.Json.JsonSerializer.Serialize(def);
        Assert.Contains("nks.wdc.apache", json);
        Assert.Contains("Web Servers", json);
        Assert.Contains("service-status-card", json);
        Assert.Contains("log-viewer", json);
    }

    [Fact]
    public void PanelDef_EmptyProps_SerializesCleanly()
    {
        var panel = new PanelDef("custom-widget", new Dictionary<string, object>());
        var json = System.Text.Json.JsonSerializer.Serialize(panel);
        Assert.Contains("custom-widget", json);
    }
}
