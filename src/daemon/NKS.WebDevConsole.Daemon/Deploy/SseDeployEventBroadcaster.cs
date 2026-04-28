using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Deploy;

/// <summary>
/// Thin adapter that satisfies <see cref="IDeployEventBroadcaster"/> by
/// forwarding into <see cref="SseService.BroadcastAsync"/>. Lives in the
/// daemon assembly because SseService does; the interface lives in Core so
/// plugin backends (loaded in isolated AssemblyLoadContexts) can resolve
/// it through DI without the SseService type leaking across the boundary.
/// </summary>
public sealed class SseDeployEventBroadcaster : IDeployEventBroadcaster
{
    private readonly SseService _sse;

    public SseDeployEventBroadcaster(SseService sse)
    {
        _sse = sse;
    }

    public Task BroadcastAsync(string eventType, object payload) =>
        _sse.BroadcastAsync(eventType, payload);
}
