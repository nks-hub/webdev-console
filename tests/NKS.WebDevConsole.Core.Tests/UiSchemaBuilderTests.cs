using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Core.Tests;

public class UiSchemaBuilderTests
{
    [Fact]
    public void Build_MinimalBuilder_ReturnsDefaults()
    {
        var def = new UiSchemaBuilder("test-plugin").Build();

        Assert.Equal("test-plugin", def.PluginId);
        Assert.Equal("Services", def.Category);
        Assert.Equal("el-icon-setting", def.Icon);
        Assert.Empty(def.Panels);
    }

    [Fact]
    public void Category_OverridesDefault()
    {
        var def = new UiSchemaBuilder("p")
            .Category("Databases")
            .Build();

        Assert.Equal("Databases", def.Category);
    }

    [Fact]
    public void Icon_OverridesDefault()
    {
        var def = new UiSchemaBuilder("p")
            .Icon("el-icon-database")
            .Build();

        Assert.Equal("el-icon-database", def.Icon);
    }

    [Fact]
    public void AddServiceCard_CreatesCorrectPanel()
    {
        var def = new UiSchemaBuilder("apache")
            .AddServiceCard("httpd")
            .Build();

        Assert.Single(def.Panels);
        Assert.Equal("service-status-card", def.Panels[0].Type);
        Assert.Equal("httpd", def.Panels[0].Props["serviceId"]);
    }

    [Fact]
    public void AddLogViewer_CreatesCorrectPanel()
    {
        var def = new UiSchemaBuilder("mysql")
            .AddLogViewer("mysqld")
            .Build();

        Assert.Single(def.Panels);
        Assert.Equal("log-viewer", def.Panels[0].Type);
        Assert.Equal("mysqld", def.Panels[0].Props["serviceId"]);
    }

    [Fact]
    public void AddConfigEditor_CreatesCorrectPanel()
    {
        var def = new UiSchemaBuilder("nginx")
            .AddConfigEditor("nginx-main")
            .Build();

        Assert.Single(def.Panels);
        Assert.Equal("config-editor", def.Panels[0].Type);
        Assert.Equal("nginx-main", def.Panels[0].Props["serviceId"]);
    }

    [Fact]
    public void AddVersionSwitcher_CreatesCorrectPanel()
    {
        var def = new UiSchemaBuilder("php")
            .AddVersionSwitcher("php-fpm")
            .Build();

        Assert.Single(def.Panels);
        Assert.Equal("version-switcher", def.Panels[0].Type);
        Assert.Equal("php-fpm", def.Panels[0].Props["serviceId"]);
    }

    [Fact]
    public void AddMetricsChart_CreatesCorrectPanel()
    {
        var def = new UiSchemaBuilder("redis")
            .AddMetricsChart("redis-server")
            .Build();

        Assert.Single(def.Panels);
        Assert.Equal("metrics-chart", def.Panels[0].Type);
        Assert.Equal("redis-server", def.Panels[0].Props["serviceId"]);
    }

    [Fact]
    public void FluentChaining_MultiplePanels()
    {
        var def = new UiSchemaBuilder("apache")
            .Category("Web Servers")
            .Icon("el-icon-server")
            .AddServiceCard("httpd")
            .AddLogViewer("httpd")
            .AddConfigEditor("httpd")
            .AddMetricsChart("httpd")
            .Build();

        Assert.Equal("Web Servers", def.Category);
        Assert.Equal("el-icon-server", def.Icon);
        Assert.Equal(4, def.Panels.Length);
        Assert.Equal("service-status-card", def.Panels[0].Type);
        Assert.Equal("log-viewer", def.Panels[1].Type);
        Assert.Equal("config-editor", def.Panels[2].Type);
        Assert.Equal("metrics-chart", def.Panels[3].Type);
    }

    [Fact]
    public void AddPanel_CustomType_WithCustomProps()
    {
        var props = new Dictionary<string, object> { ["port"] = 3306, ["host"] = "localhost" };
        var def = new UiSchemaBuilder("custom")
            .AddPanel("custom-widget", props)
            .Build();

        Assert.Single(def.Panels);
        Assert.Equal("custom-widget", def.Panels[0].Type);
        Assert.Equal(3306, def.Panels[0].Props["port"]);
        Assert.Equal("localhost", def.Panels[0].Props["host"]);
    }

    [Fact]
    public void AddPanel_NullProps_DefaultsToEmptyDictionary()
    {
        var def = new UiSchemaBuilder("bare")
            .AddPanel("empty-panel")
            .Build();

        Assert.Single(def.Panels);
        Assert.Empty(def.Panels[0].Props);
    }

    [Fact]
    public void Build_Result_SerializesToJson()
    {
        var def = new UiSchemaBuilder("mysql")
            .Category("Databases")
            .Icon("el-icon-database")
            .AddServiceCard("mysqld")
            .Build();

        var json = System.Text.Json.JsonSerializer.Serialize(def);
        Assert.Contains("mysql", json);
        Assert.Contains("Databases", json);
        Assert.Contains("el-icon-database", json);
        Assert.Contains("service-status-card", json);
    }

    [Fact]
    public void Build_CalledTwice_ReturnsSameShape()
    {
        var builder = new UiSchemaBuilder("test").AddServiceCard("svc");
        var first = builder.Build();
        var second = builder.Build();
        Assert.Equal(first.PluginId, second.PluginId);
        Assert.Equal(first.Panels.Length, second.Panels.Length);
    }
}
