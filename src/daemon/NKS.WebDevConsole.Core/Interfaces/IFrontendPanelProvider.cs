using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Core.Interfaces;

public interface IFrontendPanelProvider
{
    PluginUiDefinition GetUiDefinition();
}
