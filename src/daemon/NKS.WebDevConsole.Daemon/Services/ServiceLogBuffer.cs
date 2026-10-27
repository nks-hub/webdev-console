using System.Collections.Concurrent;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// The daemon's copy of each service's recent log lines. Several plugins
/// (Mailpit, Redis, PostgreSQL, Caddy, Node, Cloudflare) hand out their log
/// from a queue, so <see cref="IServiceModule.GetLogsAsync"/> removes what it
/// returns: the first viewer got the history and every later one an empty
/// log. Other plugins return a tail window that repeats earlier lines. All
/// log reads go through here, so every reader sees the same history either way.
/// </summary>
public sealed class ServiceLogBuffer
{
    private const int Capacity = 2000;
    private readonly ConcurrentDictionary<string, List<string>> _lines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the module's log and returns only the lines not seen before.</summary>
    public async Task<IReadOnlyList<string>> PullAsync(IServiceModule module, int window, CancellationToken ct)
    {
        var read = await module.GetLogsAsync(window, ct);
        var history = _lines.GetOrAdd(module.ServiceId, _ => new List<string>());
        lock (history)
        {
            // A tail window overlaps what is already kept; a drained queue does not.
            var tail = history.Count > read.Count ? history.GetRange(history.Count - read.Count, read.Count) : history.ToList();
            var fresh = ServiceLogTailer.NewLines(tail, read);
            history.AddRange(fresh);
            if (history.Count > Capacity) history.RemoveRange(0, history.Count - Capacity);
            return fresh;
        }
    }

    /// <summary>Reads the module's log, then returns the last <paramref name="lines"/> lines kept for it.</summary>
    public async Task<IReadOnlyList<string>> TailAsync(IServiceModule module, int lines, CancellationToken ct)
    {
        await PullAsync(module, Math.Max(lines, 1), ct);
        var history = _lines[module.ServiceId];
        lock (history) return history.Skip(Math.Max(0, history.Count - lines)).ToList();
    }
}
