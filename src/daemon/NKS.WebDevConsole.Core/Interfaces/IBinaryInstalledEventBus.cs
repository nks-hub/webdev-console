namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Fired after a binary has been extracted and moved into its final
/// install path (<c>~/.wdc/binaries/{app}/{version}/</c>). Subscribed
/// plugin modules use this to trigger re-detection without polling or
/// sprinkling lazy-init checks through their hot paths.
/// </summary>
/// <param name="App">Lowercase app identifier (e.g. "apache", "php").</param>
/// <param name="Version">Version string as published in the catalog.</param>
/// <param name="InstallPath">Absolute path to the extracted install dir.</param>
public sealed record BinaryInstalledEvent(string App, string Version, string InstallPath);

/// <summary>
/// In-process pub/sub for <see cref="BinaryInstalledEvent"/>. Registered
/// as a daemon-wide singleton. Plugins resolve it from
/// <see cref="IPluginContext.ServiceProvider"/> inside their StartAsync and
/// dispose the returned handle inside StopAsync so handlers don't outlive
/// the plugin's lifetime (and, worse, get invoked against disposed
/// module state on the next install).
/// </summary>
public interface IBinaryInstalledEventBus
{
    /// <summary>Publish an event to all current subscribers.</summary>
    void Publish(BinaryInstalledEvent evt);

    /// <summary>
    /// Subscribe a handler. Dispose the returned <see cref="IDisposable"/>
    /// to unsubscribe. Handlers are invoked sequentially off the caller's
    /// thread (fire-and-forget <c>Task.Run</c>) so that a slow/faulty
    /// subscriber doesn't stall <see cref="BinaryManager"/>'s install
    /// completion path. Exceptions are swallowed with a log line — the
    /// bus is best-effort by design and must never fail an install.
    /// </summary>
    IDisposable Subscribe(Func<BinaryInstalledEvent, Task> handler);
}
