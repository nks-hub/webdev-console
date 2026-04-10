namespace NKS.WebDevConsole.Plugin.SDK;

/// <summary>
/// Allows plugins to register custom HTTP endpoints under /api/{pluginId}/.
/// Reserved for future third-party plugin use — not wired by the daemon yet.
/// </summary>
public class EndpointRegistration
{
    private readonly string _pluginId;
    private readonly List<PluginEndpoint> _endpoints = [];

    public IReadOnlyList<PluginEndpoint> Endpoints => _endpoints;

    public EndpointRegistration(string pluginId)
    {
        _pluginId = pluginId;
    }

    public EndpointRegistration MapGet(string path, Delegate handler)
    {
        _endpoints.Add(new PluginEndpoint("GET", $"/api/{_pluginId}/{path.TrimStart('/')}", handler));
        return this;
    }

    public EndpointRegistration MapPost(string path, Delegate handler)
    {
        _endpoints.Add(new PluginEndpoint("POST", $"/api/{_pluginId}/{path.TrimStart('/')}", handler));
        return this;
    }

    public EndpointRegistration MapPut(string path, Delegate handler)
    {
        _endpoints.Add(new PluginEndpoint("PUT", $"/api/{_pluginId}/{path.TrimStart('/')}", handler));
        return this;
    }

    public EndpointRegistration MapDelete(string path, Delegate handler)
    {
        _endpoints.Add(new PluginEndpoint("DELETE", $"/api/{_pluginId}/{path.TrimStart('/')}", handler));
        return this;
    }
}

public record PluginEndpoint(string Method, string Path, Delegate Handler);
