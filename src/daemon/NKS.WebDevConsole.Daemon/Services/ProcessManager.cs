using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Services;

public class ServiceUnit
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ServiceState State { get; set; } = ServiceState.Stopped;
    public Process? Process { get; set; }
    public int? Pid => Process?.Id;
    public DateTime? StartedAt { get; set; }
    public int RestartCount { get; set; }
    public DateTime LastRestartAttempt { get; set; }
}

public class ProcessManager
{
    private readonly ILogger<ProcessManager> _logger;
    private readonly SseService _sse;
    private readonly ConcurrentDictionary<string, ServiceUnit> _services = new();

    public ProcessManager(ILogger<ProcessManager> logger, SseService sse)
    {
        _logger = logger;
        _sse = sse;
    }

    public ServiceUnit GetOrCreate(string id, string displayName)
    {
        return _services.GetOrAdd(id, _ => new ServiceUnit { Id = id, DisplayName = displayName });
    }

    public async Task<bool> StartAsync(string id, string executable, string arguments, string? workingDir = null, CancellationToken ct = default)
    {
        var unit = _services.GetValueOrDefault(id);
        if (unit == null) return false;
        if (unit.State == ServiceState.Running) return true;

        unit.State = ServiceState.Starting;
        await BroadcastState(unit);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDir ?? Path.GetDirectoryName(executable) ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                unit.State = ServiceState.Crashed;
                await BroadcastState(unit);
                return false;
            }

            unit.Process = process;
            unit.State = ServiceState.Running;
            unit.StartedAt = DateTime.UtcNow;
            unit.RestartCount = 0;

            process.EnableRaisingEvents = true;
            process.Exited += async (_, _) =>
            {
                _logger.LogWarning("Service {Id} (PID {Pid}) exited with code {Code}", id, process.Id, process.ExitCode);
                unit.State = ServiceState.Crashed;
                unit.Process = null;
                await BroadcastState(unit);
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    _ = _sse.BroadcastAsync("log", new { serviceId = id, level = "info", message = e.Data });
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    _ = _sse.BroadcastAsync("log", new { serviceId = id, level = "error", message = e.Data });
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await BroadcastState(unit);
            _logger.LogInformation("Started {Id} (PID {Pid})", id, process.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {Id}", id);
            unit.State = ServiceState.Crashed;
            await BroadcastState(unit);
            return false;
        }
    }

    public async Task<bool> StopAsync(string id, CancellationToken ct = default)
    {
        var unit = _services.GetValueOrDefault(id);
        if (unit?.Process == null || unit.State != ServiceState.Running) return false;

        unit.State = ServiceState.Stopping;
        await BroadcastState(unit);

        try
        {
            if (!unit.Process.HasExited)
            {
                unit.Process.Kill(entireProcessTree: true);
                await unit.Process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            }

            unit.Process = null;
            unit.State = ServiceState.Stopped;
            unit.StartedAt = null;
            await BroadcastState(unit);
            _logger.LogInformation("Stopped {Id}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop {Id}", id);
            unit.State = ServiceState.Crashed;
            await BroadcastState(unit);
            return false;
        }
    }

    public ServiceStatus GetStatus(string id)
    {
        var unit = _services.GetValueOrDefault(id);
        if (unit == null)
            return new ServiceStatus(id, id, ServiceState.Stopped, null, 0, 0, null);

        double cpu = 0;
        long mem = 0;
        TimeSpan? uptime = null;

        if (unit.Process is { HasExited: false } p)
        {
            try
            {
                p.Refresh();
                mem = p.WorkingSet64;
                uptime = DateTime.UtcNow - unit.StartedAt;
            }
            catch { }
        }

        return new ServiceStatus(unit.Id, unit.DisplayName, unit.State, unit.Pid, cpu, mem, uptime);
    }

    public IEnumerable<ServiceStatus> GetAllStatuses()
        => _services.Values.Select(u => GetStatus(u.Id));

    private async Task BroadcastState(ServiceUnit unit)
    {
        await _sse.BroadcastAsync("service", new
        {
            id = unit.Id,
            name = unit.DisplayName,
            status = unit.State.ToString().ToLowerInvariant(),
            pid = unit.Pid,
            startedAt = unit.StartedAt
        });
    }
}
