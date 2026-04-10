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
}
