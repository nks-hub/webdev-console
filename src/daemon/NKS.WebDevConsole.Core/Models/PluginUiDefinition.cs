namespace NKS.WebDevConsole.Core.Models;

public record PluginUiDefinition(string PluginId, string Category, string Icon, PanelDef[] Panels);
public record PanelDef(string Type, Dictionary<string, object> Props);
