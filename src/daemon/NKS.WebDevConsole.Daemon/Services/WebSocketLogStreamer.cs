using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Manages WebSocket connections for real-time log streaming.
/// Each connected client gets its own <see cref="Channel{String}"/> for
/// fan-out — when a log line arrives via <see cref="Push"/>, it is
/// written to every subscriber's channel independently so no client
/// steals lines from another.
/// </summary>
public sealed class WebSocketLogStreamer : IDisposable
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<SubscriberChannel>> _subscribers = new();

    /// <summary>
    /// Push a log line for a service. All connected WebSocket clients
    /// subscribed to this service ID receive the line immediately.
    /// </summary>
    public void Push(string serviceId, string line)
    {
        var key = serviceId.ToLowerInvariant();
        if (!_subscribers.TryGetValue(key, out var bag)) return;

        foreach (var sub in bag)
        {
            sub.Channel.Writer.TryWrite(line);
        }
    }

    /// <summary>
    /// Register a WebSocket client for log streaming on a service.
    /// Blocks until the socket closes or the token is cancelled.
    /// Each client gets its own channel for independent fan-out.
    /// </summary>
    public async Task StreamAsync(string serviceId, WebSocket socket, CancellationToken ct)
    {
        var key = serviceId.ToLowerInvariant();
        var sub = new SubscriberChannel(socket);

        var bag = _subscribers.GetOrAdd(key, _ => new ConcurrentBag<SubscriberChannel>());
        bag.Add(sub);

        try
        {
            var reader = sub.Channel.Reader;
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                string line;
                try
                {
                    line = await reader.ReadAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ChannelClosedException) { break; }

                if (socket.State != WebSocketState.Open) break;

                var payload = JsonSerializer.Serialize(new { line, ts = DateTime.UtcNow });
                var bytes = Encoding.UTF8.GetBytes(payload);
                try
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct);
                }
                catch { break; }
            }
        }
        finally
        {
            sub.Channel.Writer.TryComplete();

            // Remove from bag — ConcurrentBag doesn't have Remove, so
            // rebuild without this subscriber. Infrequent (disconnect only).
            if (_subscribers.TryGetValue(key, out var currentBag))
            {
                var remaining = new ConcurrentBag<SubscriberChannel>(
                    currentBag.Where(s => s != sub));
                _subscribers.TryUpdate(key, remaining, currentBag);
            }

            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }
                catch { /* best-effort close */ }
            }
        }
    }

    public int SubscriberCount(string serviceId)
    {
        var key = serviceId.ToLowerInvariant();
        return _subscribers.TryGetValue(key, out var bag) ? bag.Count : 0;
    }

    public void Dispose()
    {
        foreach (var bag in _subscribers.Values)
            foreach (var sub in bag)
                sub.Channel.Writer.TryComplete();
        _subscribers.Clear();
    }

    private sealed class SubscriberChannel
    {
        public WebSocket Socket { get; }
        public Channel<string> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<string>(
            new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

        public SubscriberChannel(WebSocket socket) => Socket = socket;
    }
}
