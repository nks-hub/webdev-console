using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

public class ProcessManagerTests
{
    private readonly Mock<ILogger<ProcessManager>> _loggerMock = new();
    private readonly SseService _sse = new();
    private readonly ProcessManager _pm;

    public ProcessManagerTests()
    {
        _pm = new ProcessManager(_loggerMock.Object, _sse);
    }

    [Fact]
    public void GetOrCreate_ReturnsNewUnit_WithCorrectProperties()
    {
        var unit = _pm.GetOrCreate("apache", "Apache HTTP");

        Assert.Equal("apache", unit.Id);
        Assert.Equal("Apache HTTP", unit.DisplayName);
        Assert.Equal(ServiceState.Stopped, unit.State);
        Assert.Null(unit.Process);
        Assert.Null(unit.Pid);
    }

    [Fact]
    public void GetOrCreate_ReturnsSameInstance_ForSameId()
    {
        var first = _pm.GetOrCreate("mysql", "MySQL");
        var second = _pm.GetOrCreate("mysql", "MySQL Server");

        Assert.Same(first, second);
        // DisplayName is from the first call (GetOrAdd semantics)
        Assert.Equal("MySQL", second.DisplayName);
    }

    [Fact]
    public void GetOrCreate_ReturnsDifferentInstances_ForDifferentIds()
    {
        var apache = _pm.GetOrCreate("apache", "Apache");
        var mysql = _pm.GetOrCreate("mysql", "MySQL");

        Assert.NotSame(apache, mysql);
    }

    [Fact]
    public void GetStatus_ReturnsStoppedState_ForUnknownService()
    {
        var status = _pm.GetStatus("nonexistent");

        Assert.Equal("nonexistent", status.Id);
        Assert.Equal(ServiceState.Stopped, status.State);
        Assert.Null(status.Pid);
        Assert.Equal(0, status.CpuPercent);
        Assert.Equal(0, status.MemoryBytes);
        Assert.Null(status.Uptime);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectState_ForRegisteredService()
    {
        var unit = _pm.GetOrCreate("apache", "Apache");
        unit.State = ServiceState.Running;

        var status = _pm.GetStatus("apache");

        Assert.Equal("apache", status.Id);
        Assert.Equal("Apache", status.DisplayName);
        Assert.Equal(ServiceState.Running, status.State);
    }

    [Fact]
    public void GetAllStatuses_ReturnsEmpty_WhenNoServices()
    {
        var statuses = _pm.GetAllStatuses().ToList();

        Assert.Empty(statuses);
    }

    [Fact]
    public void GetAllStatuses_ReturnsAllRegisteredServices()
    {
        _pm.GetOrCreate("apache", "Apache");
        _pm.GetOrCreate("mysql", "MySQL");
        _pm.GetOrCreate("redis", "Redis");

        var statuses = _pm.GetAllStatuses().ToList();

        Assert.Equal(3, statuses.Count);
        Assert.Contains(statuses, s => s.Id == "apache");
        Assert.Contains(statuses, s => s.Id == "mysql");
        Assert.Contains(statuses, s => s.Id == "redis");
    }

    [Fact]
    public void CheckPort_AvailablePort_ReturnsTrue()
    {
        // Use port 0 trick: find a free port
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var freePort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var (available, ownerPid, ownerName) = ProcessManager.CheckPort(freePort);

        Assert.True(available);
        Assert.Null(ownerPid);
        Assert.Null(ownerName);
    }

    [Fact]
    public void CheckPort_OccupiedPort_ReturnsFalse()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var occupiedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var (available, _, _) = ProcessManager.CheckPort(occupiedPort);

        Assert.False(available);
    }

    [Fact]
    public void SuggestAlternativePort_FindsNextAvailable()
    {
        // If the preferred port is free, the suggestion should be preferred+1
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var freePort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        // preferred port freePort is available, so freePort+1 should be suggested
        var suggested = _pm.SuggestAlternativePort(freePort);

        Assert.True(suggested > freePort);
        Assert.True(suggested <= freePort + 11);
    }

    [Fact]
    public void SuggestAlternativePort_SkipsOccupiedPorts()
    {
        // Occupy a port, then ask for alternative starting from port-1
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var occupiedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var suggested = _pm.SuggestAlternativePort(occupiedPort - 1);

        // Should skip occupiedPort (since preferredPort-1+1 == occupiedPort) and find another
        Assert.NotEqual(occupiedPort, suggested);
        Assert.NotEqual(0, suggested);
    }

    [Fact]
    public void RestartPolicy_GetBackoff_ReturnsMinBackoff_ForFirstRestart()
    {
        var policy = new RestartPolicy();

        var backoff = policy.GetBackoff(0);

        Assert.Equal(TimeSpan.FromSeconds(2), backoff);
    }

    [Fact]
    public void RestartPolicy_GetBackoff_IncreasesExponentially()
    {
        var policy = new RestartPolicy();

        var b0 = policy.GetBackoff(0);
        var b1 = policy.GetBackoff(1);
        var b2 = policy.GetBackoff(2);

        Assert.True(b1 > b0);
        Assert.True(b2 > b1);
    }

    [Fact]
    public void RestartPolicy_GetBackoff_CapsAtMaxBackoff()
    {
        var policy = new RestartPolicy { MaxBackoff = TimeSpan.FromSeconds(30) };

        var backoff = policy.GetBackoff(100);

        Assert.Equal(TimeSpan.FromSeconds(30), backoff);
    }

    [Fact]
    public void RestartPolicy_ShouldRestart_TrueWhenUnderLimit()
    {
        var policy = new RestartPolicy { MaxRestarts = 5 };

        Assert.True(policy.ShouldRestart(3, DateTime.UtcNow));
    }

    [Fact]
    public void RestartPolicy_ShouldRestart_FalseWhenExceededInWindow()
    {
        var policy = new RestartPolicy
        {
            MaxRestarts = 5,
            Window = TimeSpan.FromSeconds(60)
        };

        Assert.False(policy.ShouldRestart(5, DateTime.UtcNow));
    }

    [Fact]
    public void RestartPolicy_ShouldRestart_TrueWhenExceeded_ButOutsideWindow()
    {
        var policy = new RestartPolicy
        {
            MaxRestarts = 5,
            Window = TimeSpan.FromSeconds(60)
        };

        // firstRestartInWindow was 2 minutes ago, so the window has passed
        Assert.True(policy.ShouldRestart(5, DateTime.UtcNow.AddMinutes(-2)));
    }

    [Fact]
    public void ServiceUnit_DefaultState_IsStopped()
    {
        var unit = new ServiceUnit();

        Assert.Equal(ServiceState.Stopped, unit.State);
        Assert.Equal("", unit.Id);
        Assert.Equal("", unit.DisplayName);
        Assert.Null(unit.Process);
        Assert.Null(unit.Pid);
        Assert.Equal(0, unit.Port);
        Assert.Null(unit.StartedAt);
        Assert.Equal(0, unit.RestartCount);
    }
}
