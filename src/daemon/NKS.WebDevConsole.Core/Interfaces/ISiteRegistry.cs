using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Read-only access to the site registry from plugins. Implemented by the
/// daemon's SiteManager and registered as a singleton in the host DI container
/// so plugins can resolve it across the AssemblyLoadContext boundary
/// (interface lives in shared Core, implementation in daemon, returned as
/// the shared interface type).
/// </summary>
public interface ISiteRegistry
{
    IReadOnlyDictionary<string, SiteConfig> Sites { get; }
    SiteConfig? GetSite(string domain);
}
