using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.SDK;

public class UiSchemaBuilder
{
    private readonly string _pluginId;
    private string _category = "Services";
    private string _icon = "el-icon-setting";
    private readonly List<PanelDef> _panels = [];
    private readonly List<NavContribution> _nav = [];
    // F91.2: generic UI surfaces owned by this plugin. Gets merged with
    // auto-derived "nav:{Route}" entries in Build() so every nav route
    // also shows up as an ownable surface without duplicate declarations.
    private readonly List<string> _surfaces = [];

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

    /// <summary>
    /// F91.2: declare ownership of an arbitrary UI surface. Surface keys are
    /// namespaced (<c>site-tab:ssl</c>, <c>dashboard-card:tunnel-status</c>,
    /// <c>header-tab:/ssl</c>, <c>sites-badge:cloudflare</c>). The frontend
    /// hides any surface whose owning plugin is disabled, so a plugin can
    /// remove its entire UI footprint by toggling off — no frontend code
    /// knowledge of which plugin owns what.
    /// </summary>
    public UiSchemaBuilder AddUiSurface(string surfaceKey)
    {
        if (!string.IsNullOrWhiteSpace(surfaceKey)) _surfaces.Add(surfaceKey);
        return this;
    }

    /// <summary>F91.2 sugar — plugin owns a SiteEdit tab with the given id.</summary>
    public UiSchemaBuilder AddSiteTab(string tabId) => AddUiSurface($"site-tab:{tabId}");

    /// <summary>F91.2 sugar — plugin owns a dashboard card identified by <paramref name="cardId"/>.</summary>
    public UiSchemaBuilder AddDashboardCard(string cardId) => AddUiSurface($"dashboard-card:{cardId}");

    /// <summary>F91.2 sugar — plugin owns a site-card badge/overlay (per-site).</summary>
    public UiSchemaBuilder AddSiteBadge(string badgeId) => AddUiSurface($"sites-badge:{badgeId}");

    public PluginUiDefinition Build()
    {
        // Auto-derive "nav:{Route}" surfaces from declared nav entries so a
        // plugin that only calls AddNavEntry still registers its route as
        // a plugin-owned surface (frontend uses this to hide the header tab).
        var surfaces = new List<string>(_surfaces);
        foreach (var n in _nav)
        {
            if (!string.IsNullOrWhiteSpace(n.Route))
                surfaces.Add($"nav:{n.Route}");
        }
        return new(_pluginId, _category, _icon, _panels.ToArray(),
            _nav.Count == 0 ? null : _nav.ToArray(),
            surfaces.Count == 0 ? null : surfaces.ToArray());
    }
}
