namespace NKS.WebDevConsole.Core.Models;

/// <summary>
/// UI contract returned by <c>IFrontendPanelProvider.GetUiDefinition</c>. The
/// frontend consumes this per-enabled-plugin to decide which nav entries to
/// render and which panel components to mount on the plugin's route.
/// </summary>
/// <param name="PluginId">Canonical plugin id, e.g. <c>nks.wdc.composer</c>.</param>
/// <param name="Category">Sidebar grouping label. Conventional values:
/// <c>Services</c>, <c>Tools</c>, <c>Integrations</c>.</param>
/// <param name="Icon">Element Plus icon component name used as the default
/// icon when a nav entry does not specify its own.</param>
/// <param name="Panels">Panel components rendered inside the plugin's page.</param>
/// <param name="NavEntries">Sidebar nav contributions. When populated and the
/// plugin is enabled, the frontend SHOULD render each entry as a top-level
/// sidebar link. When empty/null the plugin ships no nav surface — useful
/// for plugins that only expose panels embedded elsewhere (e.g. a metric
/// card on the dashboard).</param>
public record PluginUiDefinition(
    string PluginId,
    string Category,
    string Icon,
    PanelDef[] Panels,
    NavContribution[]? NavEntries = null);

public record PanelDef(string Type, Dictionary<string, object> Props);

/// <summary>
/// Sidebar nav contribution. A plugin may declare one or more entries; each
/// becomes visible only while the plugin is enabled.
/// </summary>
/// <param name="Id">Stable nav entry id — used as Vue Router route name.</param>
/// <param name="Label">Human-readable label shown in the sidebar.</param>
/// <param name="Icon">Element Plus icon component name; empty string falls
/// back to the plugin-level <see cref="PluginUiDefinition.Icon"/>.</param>
/// <param name="Route">Router path, e.g. <c>/composer</c>. Must start with
/// a forward slash.</param>
/// <param name="Order">Sort key inside the category; lower numbers first.
/// Default <c>100</c> keeps contributed entries stable.</param>
public record NavContribution(
    string Id,
    string Label,
    string Icon,
    string Route,
    int Order = 100);
