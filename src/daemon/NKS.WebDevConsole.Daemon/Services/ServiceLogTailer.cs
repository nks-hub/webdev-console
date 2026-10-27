using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Streams new log lines of plugin-managed services (Apache, MySQL, PHP,
/// Redis, …) to the UI.
///
/// Those services run their own processes and only expose
/// <see cref="IServiceModule.GetLogsAsync"/>; nothing ever pushed their
/// output, so the log viewer showed the initial history and then stayed
/// silent, and the per-service WebSocket stream never received a line.
/// This tails each module's log window every couple of seconds, but only
/// while someone is listening (SSE clients, or WebSocket subscribers for
/// that service), and forwards the lines that are new since the last poll
/// as <c>log</c> SSE events and to <see cref="WebSocketLogStreamer"/>.
/// </summary>
public sealed class ServiceLogTailer : BackgroundService
{
    private const int WindowLines = 200;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _sp;
    private readonly SseService _sse;
    private readonly WebSocketLogStreamer _streamer;
    private readonly ILogger<ServiceLogTailer> _logger;
    private readonly Dictionary<string, IReadOnlyList<string>> _lastWindow = new(StringComparer.OrdinalIgnoreCase);

    public ServiceLogTailer(IServiceProvider sp, SseService sse, WebSocketLogStreamer streamer, ILogger<ServiceLogTailer> logger)
    {
        _sp = sp;
        _sse = sse;
        _streamer = streamer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Service log tail tick failed"); }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        // The UI log viewer consumes the shared /api/events stream and the
        // daemon cannot tell which SSE clients have a log viewer open, so any
        // SSE client counts as a listener; WebSocket subscribers are tracked
        // per service.
        var sseListening = _sse.ClientCount > 0;
        foreach (var module in _sp.GetServices<IServiceModule>())
        {
            var id = module.ServiceId;
            var wsListening = _streamer.SubscriberCount(id) > 0;
            if (!sseListening && !wsListening)
            {
                // Nobody watching: forget the baseline so a later listener
                // doesn't receive a burst of everything logged meanwhile.
                _lastWindow.Remove(id);
                continue;
            }

            IReadOnlyList<string> window;
            try { window = await module.GetLogsAsync(WindowLines, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading the log window of {ServiceId} failed", id);
                continue;
            }

            if (!_lastWindow.TryGetValue(id, out var previous))
            {
                // First look establishes the baseline; history is already
                // delivered by GET /api/services/{id}/logs and the WS handshake.
                _lastWindow[id] = window;
                continue;
            }
            _lastWindow[id] = window;

            foreach (var line in NewLines(previous, window))
            {
                if (wsListening) _streamer.Push(id, line);
                if (sseListening)
                    await _sse.BroadcastAsync("log", new { serviceId = id, level = "info", message = line });
            }
        }
    }

    /// <summary>
    /// Lines in <paramref name="next"/> that follow the longest suffix of
    /// <paramref name="previous"/> appearing as a prefix of <paramref name="next"/>
    /// (both are tail windows of the same log). No overlap means the log
    /// rotated or more than a full window was written: everything is new.
    /// Window content alone cannot tell "unchanged" from "the same lines were
    /// appended again" once a full window is periodic (for example all lines
    /// identical); such repeats are not reported.
    /// </summary>
    public static IReadOnlyList<string> NewLines(IReadOnlyList<string> previous, IReadOnlyList<string> next)
    {
        if (next.Count == 0) return Array.Empty<string>();
        if (previous.Count == 0) return next;
        for (var overlap = Math.Min(previous.Count, next.Count); overlap > 0; overlap--)
        {
            var match = true;
            for (var i = 0; i < overlap && match; i++)
                match = string.Equals(previous[previous.Count - overlap + i], next[i], StringComparison.Ordinal);
            if (match)
                return next.Skip(overlap).ToList();
        }
        return next;
    }
}
