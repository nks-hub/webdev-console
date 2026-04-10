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

        var deadClients = new List<string>();
        foreach (var (id, client) in _clients)
        {
            await client.WriteLock.WaitAsync();
            try
            {
                await client.Response.WriteAsync(message);
                await client.Response.Body.FlushAsync();
            }
            catch
            {
                deadClients.Add(id);
            }
            finally
            {
                client.WriteLock.Release();
            }
        }

        foreach (var id in deadClients)
        {
            if (_clients.TryRemove(id, out var dead))
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
