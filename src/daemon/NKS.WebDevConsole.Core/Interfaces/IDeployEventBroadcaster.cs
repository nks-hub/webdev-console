namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Cross-ALC bridge for plugin backends to push events into the daemon's
/// SSE fan-out without taking a hard dependency on <c>SseService</c> (which
/// lives in the daemon assembly and is therefore not visible to plugin
/// AssemblyLoadContexts). The daemon registers a thin adapter implementing
/// this interface that simply forwards into <c>SseService.BroadcastAsync</c>.
///
/// <para>Plugin backends call <see cref="BroadcastAsync"/> from their
/// <c>IProgress&lt;DeployEvent&gt;.Report</c> implementation so live deploy
/// progress reaches the wdc UI's deploy drawer in real time.</para>
///
/// <para>The <paramref name="payload"/> is serialised by the implementation
/// (typically <c>System.Text.Json</c>); plugins should pass a record/anonymous
/// object whose property shape they want to land on the wire — no
/// pre-serialisation needed.</para>
/// </summary>
public interface IDeployEventBroadcaster
{
    /// <summary>
    /// Push a JSON-serialised event to all connected SSE clients under the
    /// supplied event-type name (e.g. <c>"deploy:event"</c>, <c>"deploy:complete"</c>).
    /// </summary>
    Task BroadcastAsync(string eventType, object payload);
}
