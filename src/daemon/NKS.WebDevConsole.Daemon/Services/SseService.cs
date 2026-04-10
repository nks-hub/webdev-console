using System.Collections.Concurrent;
using System.Text.Json;

namespace NKS.WebDevConsole.Daemon.Services;

public class SseService
{
    private readonly ConcurrentDictionary<string, SseClient> _clients = new();

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
            await client.WriteLock.WaitAsync();
            try
            {
                await client.Response.WriteAsync(message);
                await client.Response.Body.FlushAsync();
                return (id, alive: true);
            }
            catch
            {
                return (id, alive: false);
            }
            finally
            {
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
