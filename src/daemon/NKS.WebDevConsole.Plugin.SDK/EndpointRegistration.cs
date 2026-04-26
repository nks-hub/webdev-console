// Type forwarders preserve binary compatibility for plugins compiled against
// pre-move SDK versions where EndpointRegistration and PluginEndpoint lived in
// NKS.WebDevConsole.Plugin.SDK. The types now live in Core so IWdcPlugin can
// reference them without cycle. Removing the forwarders is a MAJOR breaking
// change — keep them through at least one major SDK version after move.
using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(NKS.WebDevConsole.Core.Interfaces.EndpointRegistration))]
[assembly: TypeForwardedTo(typeof(NKS.WebDevConsole.Core.Interfaces.PluginEndpoint))]
