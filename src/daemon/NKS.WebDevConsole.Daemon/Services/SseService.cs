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
        _clients.TryRemove(id, out _);
    }

    public async Task BroadcastAsync(string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var message = $"event: {eventType}\ndata: {json}\n\n";

        var deadClients = new List<string>();
        foreach (var (id, client) in _clients)
        {
            try
            {
                await client.Response.WriteAsync(message);
                await client.Response.Body.FlushAsync();
            }
            catch
            {
                deadClients.Add(id);
            }
        }

        foreach (var id in deadClients)
            _clients.TryRemove(id, out _);
    }

    public int ClientCount => _clients.Count;
}

public record SseClient(string Id, HttpResponse Response);
