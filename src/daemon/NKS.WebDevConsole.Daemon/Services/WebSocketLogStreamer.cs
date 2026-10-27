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
    // Set semantics per service (the byte value is unused) so a subscriber
    // is removed by identity. Push enumerates lock-free; adding a subscriber
    // and removing one (plus dropping the service key once it is empty) go
    // through _membershipLock so a connect can never land in a set that a
    // concurrent disconnect is about to unlink from the dictionary.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<SubscriberChannel, byte>> _subscribers = new();
    private readonly object _membershipLock = new();

    /// <summary>
    /// Push a log line for a service. All connected WebSocket clients
    /// subscribed to this service ID receive the line immediately.
    /// </summary>
    public void Push(string serviceId, string line)
    {
        var key = serviceId.ToLowerInvariant();
        if (!_subscribers.TryGetValue(key, out var set)) return;

        foreach (var sub in set.Keys)
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

        Subscribe(key, sub);

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
            Unsubscribe(key, sub);

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

    private void Subscribe(string key, SubscriberChannel sub)
    {
        lock (_membershipLock)
        {
            _subscribers.GetOrAdd(key, static _ => new ConcurrentDictionary<SubscriberChannel, byte>())
                .TryAdd(sub, 0);
        }
    }

    private void Unsubscribe(string key, SubscriberChannel sub)
    {
        lock (_membershipLock)
        {
            if (!_subscribers.TryGetValue(key, out var set)) return;
            set.TryRemove(sub, out _);
            // Drop the key once the last subscriber leaves so a service that
            // churns subscribers does not keep an empty set around forever.
            if (set.IsEmpty)
                _subscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<SubscriberChannel, byte>>(key, set));
        }
    }

    public int SubscriberCount(string serviceId)
    {
        var key = serviceId.ToLowerInvariant();
        return _subscribers.TryGetValue(key, out var set) ? set.Count : 0;
    }

    /// <summary>Number of service ids that currently have at least one subscriber.</summary>
    internal int ServiceKeyCount => _subscribers.Count;

    public void Dispose()
    {
        lock (_membershipLock)
        {
            foreach (var set in _subscribers.Values)
                foreach (var sub in set.Keys)
                    sub.Channel.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    private sealed class SubscriberChannel
    {
        public WebSocket Socket { get; }
        public Channel<string> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<string>(
            new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

        public SubscriberChannel(WebSocket socket) => Socket = socket;
    }
}
