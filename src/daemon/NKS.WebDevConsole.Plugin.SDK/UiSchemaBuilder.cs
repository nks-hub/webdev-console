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
    // F91.6: plugin-contributed UI fragments rendered into named slots by
    // the frontend's <PluginSlot> component. Replaces hardcoded v-if +
    // component references in the Vue shell.
    private readonly List<UiContribution> _contributions = [];

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

    /// <summary>
    /// F91.3: plugin declares which sidebar service section it belongs to
    /// (<c>web</c>, <c>lang</c>, <c>db</c>, <c>cache</c>, <c>tools</c>, <c>mail</c>).
    /// Emits both the membership surface <c>service-section:{category}</c>
    /// AND the row-identity surface <c>service-row:{serviceId}</c> so the
    /// sidebar knows "enabled plugin X owns a row under section Y with
    /// service id Z", replacing the previous hardcoded SERVICE_CATEGORIES
    /// map. Disabling the plugin drops both surfaces → row vanishes.
    /// </summary>
    public UiSchemaBuilder SetServiceCategory(string category, string serviceId)
    {
        AddUiSurface($"service-section:{category}");
        AddUiSurface($"service-row:{category}:{serviceId}");
        return this;
    }

    /// <summary>
    /// F91.6: generic contribution — plugin asks the shell to render
    /// <paramref name="componentType"/> inside slot <paramref name="slot"/>
    /// with <paramref name="props"/>. Use the typed helpers below for
    /// well-known slots to get IntelliSense + avoid typos.
    /// </summary>
    public UiSchemaBuilder Contribute(string slot, string componentType,
        Dictionary<string, object>? props = null, int order = 100)
    {
        if (!string.IsNullOrWhiteSpace(slot) && !string.IsNullOrWhiteSpace(componentType))
            _contributions.Add(new UiContribution(slot, componentType, props ?? [], order));
        return this;
    }

    /// <summary>F91.6: contribute a tab to the per-site editor (SiteEdit.vue). Props must include <c>name</c> + <c>label</c>.</summary>
    public UiSchemaBuilder ContributeSiteEditTab(string componentType,
        Dictionary<string, object> props, int order = 100)
        => Contribute("site-edit-tabs", componentType, props, order);

    /// <summary>F91.6: contribute a stat tile to the Dashboard overview.</summary>
    public UiSchemaBuilder ContributeDashboardTile(string componentType,
        Dictionary<string, object> props, int order = 100)
        => Contribute("dashboard-tiles", componentType, props, order);

    /// <summary>F91.6: contribute a button to the Dashboard quick-actions bar.</summary>
    public UiSchemaBuilder ContributeQuickAction(string componentType,
        Dictionary<string, object> props, int order = 100)
        => Contribute("dashboard-quick-actions", componentType, props, order);

    /// <summary>F91.6: contribute a per-site badge/chip rendered in the Sites list row.</summary>
    public UiSchemaBuilder ContributeSitesBadge(string componentType,
        Dictionary<string, object> props, int order = 100)
        => Contribute("sites-row-badges", componentType, props, order);

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
            surfaces.Count == 0 ? null : surfaces.ToArray(),
            _contributions.Count == 0 ? null : _contributions.ToArray());
    }
}
