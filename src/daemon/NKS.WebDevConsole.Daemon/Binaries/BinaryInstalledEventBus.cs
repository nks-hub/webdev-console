using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Binaries;

/// <summary>
/// In-process pub/sub implementation of <see cref="IBinaryInstalledEventBus"/>.
/// No persistence, no ordering guarantees between subscribers, no delivery
/// retries — the bus is a pure "someone installed X, re-detect yourself"
/// fan-out. Replaces the per-plugin lazy-init snippet that used to sit in
/// every module's <c>StartAsync</c> / getter path (task #9).
/// </summary>
public sealed class BinaryInstalledEventBus : IBinaryInstalledEventBus
{
    private readonly ILogger<BinaryInstalledEventBus> _logger;

    // Copy-on-write list under a short lock. Subscribe/Unsubscribe are
    // rare (once per plugin per daemon lifetime); Publish is also rare
    // (once per install) so the lock is never contended in practice. A
    // plain List<T> + snapshot would work too but ImmutableList keeps
    // the Publish path allocation-free on the hot read side.
    private readonly object _lock = new();
    private System.Collections.Immutable.ImmutableList<Func<BinaryInstalledEvent, Task>> _handlers =
        System.Collections.Immutable.ImmutableList<Func<BinaryInstalledEvent, Task>>.Empty;

    public BinaryInstalledEventBus(ILogger<BinaryInstalledEventBus> logger)
    {
        _logger = logger;
    }

    public void Publish(BinaryInstalledEvent evt)
    {
        var snapshot = _handlers;
        if (snapshot.IsEmpty)
        {
            _logger.LogDebug("BinaryInstalled {App} {Version} — no subscribers", evt.App, evt.Version);
            return;
        }

        _logger.LogInformation(
            "BinaryInstalled {App} {Version} → dispatching to {Count} subscriber(s)",
            evt.App, evt.Version, snapshot.Count);

        foreach (var handler in snapshot)
        {
            // Fire-and-forget off the caller thread so a slow re-init
            // (PHP scans every version dir, writes php.ini files, shells
            // out to php-config — multi-hundred-ms) doesn't stall the
            // BinaryManager.InstallAsync response that the /api/binaries/install
            // endpoint is awaiting. Each handler gets its own continuation so
            // one failing subscriber doesn't poison the others.
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(evt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "BinaryInstalled handler for {App} {Version} threw",
                        evt.App, evt.Version);
                }
            });
        }
    }

    public IDisposable Subscribe(Func<BinaryInstalledEvent, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            _handlers = _handlers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    private void Unsubscribe(Func<BinaryInstalledEvent, Task> handler)
    {
        lock (_lock)
        {
            _handlers = _handlers.Remove(handler);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private BinaryInstalledEventBus? _bus;
        private readonly Func<BinaryInstalledEvent, Task> _handler;

        public Subscription(BinaryInstalledEventBus bus, Func<BinaryInstalledEvent, Task> handler)
        {
            _bus = bus;
            _handler = handler;
        }

        public void Dispose()
        {
            // Idempotent: StopAsync may race with a plugin reload; we never
            // want the double-dispose to throw because the daemon ignores
            // the exception and the next reload leaks a subscription.
            var bus = System.Threading.Interlocked.Exchange(ref _bus, null);
            bus?.Unsubscribe(_handler);
        }
    }
}
