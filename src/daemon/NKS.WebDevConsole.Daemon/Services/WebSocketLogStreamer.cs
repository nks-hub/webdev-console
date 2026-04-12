using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Manages WebSocket connections for real-time log streaming.
/// Each connected client subscribes to a specific service ID and
/// receives log lines as they arrive, with zero batching delay.
///
/// Phase 11 item: replaces the polling-based /api/services/{id}/logs
/// endpoint with a persistent WebSocket connection for the log viewer.
/// SSE events continue to work for service state — this only handles
/// log-specific streaming where latency matters.
/// </summary>
public sealed class WebSocketLogStreamer : IDisposable
{
    private readonly ConcurrentDictionary<string, Channel<string>> _channels = new();
    private readonly ConcurrentDictionary<string, List<WebSocket>> _subscribers = new();
    private readonly object _lock = new();

    /// <summary>
    /// Push a log line for a service. All connected WebSocket clients
    /// subscribed to this service ID receive the line immediately.
    /// </summary>
    public void Push(string serviceId, string line)
    {
        var key = serviceId.ToLowerInvariant();
        if (_channels.TryGetValue(key, out var channel))
            channel.Writer.TryWrite(line);
    }

    /// <summary>
    /// Register a WebSocket client for log streaming on a service.
    /// Blocks until the socket closes or the token is cancelled.
    /// </summary>
    public async Task StreamAsync(string serviceId, WebSocket socket, CancellationToken ct)
    {
        var key = serviceId.ToLowerInvariant();

        // Ensure channel exists
        var channel = _channels.GetOrAdd(key, _ =>
            Channel.CreateBounded<string>(new BoundedChannelOptions(5000)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            }));

        // Track subscriber
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(key, out var list))
            {
                list = new List<WebSocket>();
                _subscribers[key] = list;
            }
            list.Add(socket);
        }

        try
        {
            // Read from channel and send to this specific socket
            var reader = channel.Reader;
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
            lock (_lock)
            {
                if (_subscribers.TryGetValue(key, out var list))
                {
                    list.Remove(socket);
                    if (list.Count == 0) _subscribers.TryRemove(key, out _);
                }
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
        lock (_lock)
        {
            return _subscribers.TryGetValue(key, out var list) ? list.Count : 0;
        }
    }

    public void Dispose()
    {
        foreach (var ch in _channels.Values)
            ch.Writer.TryComplete();
        _channels.Clear();
    }
}
