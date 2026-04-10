using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.SDK;

public class UiSchemaBuilder
{
    private readonly string _pluginId;
    private string _category = "Services";
    private string _icon = "el-icon-setting";
    private readonly List<PanelDef> _panels = [];

    public UiSchemaBuilder(string pluginId)
    {
        _pluginId = pluginId;
    }

    public UiSchemaBuilder Category(string category) { _category = category; return this; }
    public UiSchemaBuilder Icon(string icon) { _icon = icon; return this; }

    public UiSchemaBuilder AddPanel(string type, Dictionary<string, object>? props = null)
    {
        _panels.Add(new PanelDef(type, props ?? []));
        return this;
    }

    public UiSchemaBuilder AddServiceCard(string serviceId)
        => AddPanel("service-status-card", new() { ["serviceId"] = serviceId });

    public UiSchemaBuilder AddLogViewer(string serviceId)
        => AddPanel("log-viewer", new() { ["serviceId"] = serviceId });

    public UiSchemaBuilder AddConfigEditor(string serviceId)
        => AddPanel("config-editor", new() { ["serviceId"] = serviceId });

    public UiSchemaBuilder AddVersionSwitcher(string serviceId)
        => AddPanel("version-switcher", new() { ["serviceId"] = serviceId });

    public UiSchemaBuilder AddMetricsChart(string serviceId)
        => AddPanel("metrics-chart", new() { ["serviceId"] = serviceId });

    public PluginUiDefinition Build() => new(_pluginId, _category, _icon, _panels.ToArray());
}
