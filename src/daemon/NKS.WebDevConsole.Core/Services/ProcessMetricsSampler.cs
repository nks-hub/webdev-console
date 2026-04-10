using System.Collections.Concurrent;
using System.Diagnostics;

namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Thread-safe delta-based CPU sampler for managed child processes.
///
/// Previous implementations in individual plugins computed CPU% as
/// <c>TotalProcessorTime / (cores * uptime) * 100</c> which gives the
/// cumulative average since process start — a flat, uninformative line that
/// hides activity spikes. This helper stores the previous sample per process
/// id and returns the *delta* between samples so charts show real-time load.
///
/// Plugins call <see cref="Sample"/> on each health-monitor tick (5s interval).
/// The first sample for a PID returns 0% (no delta available). Subsequent
/// samples return the percent of wall-clock time the process spent on CPU
/// across all cores.
/// </summary>
public static class ProcessMetricsSampler
{
    private sealed record Snapshot(TimeSpan Cpu, DateTime WallClock);

    private static readonly ConcurrentDictionary<int, Snapshot> _lastByPid = new();

    /// <summary>
    /// Samples the process and returns (cpuPercent, workingSetBytes). Returns
    /// (0, 0) if the process has exited or cannot be queried.
    /// </summary>
    public static (double cpuPercent, long memoryBytes) Sample(Process? process)
    {
        if (process is null) return (0, 0);

        try
        {
            if (process.HasExited) return (0, 0);

            process.Refresh();
            var mem = process.WorkingSet64;
            var nowCpu = process.TotalProcessorTime;
            var nowWall = DateTime.UtcNow;
            var pid = process.Id;

            double cpuPct = 0;
            if (_lastByPid.TryGetValue(pid, out var prev))
            {
                var cpuDelta = (nowCpu - prev.Cpu).TotalMilliseconds;
                var wallDelta = (nowWall - prev.WallClock).TotalMilliseconds;
                if (wallDelta > 0)
                {
                    cpuPct = cpuDelta / (Environment.ProcessorCount * wallDelta) * 100.0;
                    if (cpuPct < 0) cpuPct = 0;
                    if (cpuPct > 100) cpuPct = 100;
                }
            }

            _lastByPid[pid] = new Snapshot(nowCpu, nowWall);
            return (cpuPct, mem);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Aggregates multiple child processes into a single metrics reading.
    /// Used by PHP plugin which manages several php-cgi worker processes.
    /// </summary>
    public static (double cpuPercent, long memoryBytes) SampleMany(IEnumerable<Process?> processes)
    {
        double totalCpu = 0;
        long totalMem = 0;
        foreach (var p in processes)
        {
            var (cpu, mem) = Sample(p);
            totalCpu += cpu;
            totalMem += mem;
        }
        if (totalCpu > 100) totalCpu = 100;
        return (totalCpu, totalMem);
    }

    /// <summary>
    /// Drops the cached snapshot for a terminated process so the next start
    /// does not inherit stale CPU accounting.
    /// </summary>
    public static void Forget(int pid)
    {
        _lastByPid.TryRemove(pid, out _);
    }
}
