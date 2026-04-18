using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NKS.WebDevConsole.Daemon.Services;

public class SseService
{
    private readonly ConcurrentDictionary<string, SseClient> _clients = new();
    private readonly ILogger<SseService> _logger;

    // Per-client write timeout. A broadcast never blocks longer than this
    // on a slow client before the client gets evicted. Keeps a stalled
    // browser tab from wedging the lock — without this, the next broadcast
    // to the same client serializes behind the slow writer until its TCP
    // timeout (typically minutes). 5 s is well above a healthy LAN flush
    // and fast enough that stale clients disappear from /metrics within
    // one tick of the HealthMonitor.
    private static readonly TimeSpan ClientWriteTimeout = TimeSpan.FromSeconds(5);

    public SseService(ILogger<SseService>? logger = null)
    {
        _logger = logger ?? NullLogger<SseService>.Instance;
    }

    public string AddClient(HttpResponse response)
    {
        var id = Guid.NewGuid().ToString("N");
        _clients[id] = new SseClient(id, response);
        return id;
    }

    public void RemoveClient(string id)
    {
        if (_clients.TryRemove(id, out var client))
            client.Dispose();
    }

    public async Task BroadcastAsync(string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var message = $"event: {eventType}\ndata: {json}\n\n";

        // Snapshot the current client set so concurrent add/remove doesn't race.
        // Writes are sent in PARALLEL — previous sequential version blocked the whole
        // broadcast on any slow client, which broke live metrics when one renderer
        // paused or the network stalled.
        var snapshot = _clients.ToArray();
        if (snapshot.Length == 0) return;

        var tasks = snapshot.Select(async kvp =>
        {
            var (id, client) = kvp;
            using var cts = new CancellationTokenSource(ClientWriteTimeout);
            // Pass the timeout token to WaitAsync too so a stalled
            // prior write on the same client doesn't extend the
            // combined lock+write budget past ClientWriteTimeout.
            try
            {
                await client.WriteLock.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return (id, alive: false);
            }
            try
            {
                await client.Response.WriteAsync(message, cts.Token);
                await client.Response.Body.FlushAsync(cts.Token);
                return (id, alive: true);
            }
            catch (Exception ex)
            {
                // Debug-level — dead clients are expected when browsers
                // close tabs or networks hiccup. Log at a level that
                // lets operators diagnose "why did metrics stop" without
                // spamming the daemon log.
                _logger.LogDebug(ex, "SSE client {ClientId} evicted on write: {Error}", id, ex.Message);
                return (id, alive: false);
            }
            finally
            {
                // Release only if WaitAsync actually acquired — the
                // early-return path for the cancelled lock-wait above
                // never touched the semaphore, so this block only runs
                // for the successful-acquire code path.
                client.WriteLock.Release();
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        foreach (var (id, alive) in results)
        {
            if (!alive && _clients.TryRemove(id, out var dead))
                dead.Dispose();
        }
    }

    public int ClientCount => _clients.Count;
}

public class SseClient : IDisposable
{
    public string Id { get; }
    public HttpResponse Response { get; }
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public SseClient(string id, HttpResponse response) { Id = id; Response = response; }
    public void Dispose() => WriteLock.Dispose();
}
