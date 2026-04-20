using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.SDK;

public class UiSchemaBuilder
{
    private readonly string _pluginId;
    private string _category = "Services";
    private string _icon = "el-icon-setting";
    private readonly List<PanelDef> _panels = [];
    private readonly List<NavContribution> _nav = [];

    public UiSchemaBuilder(string pluginId)
    {
        _pluginId = pluginId;
    }

    public UiSchemaBuilder Category(string category) { _category = category; return this; }
    public UiSchemaBuilder Icon(string icon) { _icon = icon; return this; }

    /// <summary>
    /// Adds a sidebar nav entry contributed by this plugin. Hidden by the
    /// frontend when the plugin is disabled. See <see cref="NavContribution"/>
    /// for field semantics.
    /// </summary>
    public UiSchemaBuilder AddNavEntry(string id, string label, string route, string icon = "", int order = 100)
    {
        _nav.Add(new NavContribution(id, label, icon, route, order));
        return this;
    }

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

    public PluginUiDefinition Build() =>
        new(_pluginId, _category, _icon, _panels.ToArray(),
            _nav.Count == 0 ? null : _nav.ToArray());
}
