using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="ServiceInfoProjection"/> and <see cref="ServiceRunTracker"/>:
/// the installed flag, version and crash kind the UI reads from /api/services.
/// </summary>
public class ServiceInfoProjectionTests
{
    private static ServiceStatus Status(string id, ServiceState state)
        => new(id, id, state, null, 0, 0, null);

    private static InstalledBinary Binary(string app, string version)
        => new(app, version, version, $"/bin/{app}/{version}", null);

    [Fact]
    public void Build_MarksServiceNotInstalled_WhenItsBinaryIsMissing()
    {
        var dto = ServiceInfoProjection.Build(
            Status("apache", ServiceState.Stopped), [Binary("php", "8.5.1")], new ServiceRunTracker());

        Assert.False(dto.Installed);
        Assert.Null(dto.Version);
        Assert.Equal("apache", dto.BinaryApp);
    }

    [Fact]
    public void Build_ReportsInstalledVersion_WhenBinaryIsPresent()
    {
        var dto = ServiceInfoProjection.Build(
            Status("php", ServiceState.Stopped),
            [Binary("php", "8.5.12"), Binary("php", "8.3.1")],
            new ServiceRunTracker());

        Assert.True(dto.Installed);
        Assert.Equal("8.5.12", dto.Version);
    }

    [Fact]
    public void Build_MatchesServiceIdCaseInsensitively()
    {
        var dto = ServiceInfoProjection.Build(
            Status("MySQL", ServiceState.Stopped), [Binary("mysql", "9.7.1")], new ServiceRunTracker());

        Assert.True(dto.Installed);
        Assert.Equal("9.7.1", dto.Version);
        Assert.Equal("mysql", dto.BinaryApp);
    }

    [Theory]
    [InlineData("mariadb", "mysql", "9.7.1")]
    [InlineData("mysql", "mariadb", "11.4.10")]
    public void Build_NeverBorrowsTheVersionOfARelatedApp(string serviceId, string otherApp, string otherVersion)
    {
        // The MySQL and MariaDB plugins each run only their own binary.
        var dto = ServiceInfoProjection.Build(
            Status(serviceId, ServiceState.Stopped), [Binary(otherApp, otherVersion)], new ServiceRunTracker());

        Assert.False(dto.Installed);
        Assert.Null(dto.Version);
        Assert.Equal(serviceId, dto.BinaryApp);
    }

    [Fact]
    public void Build_TreatsUnmappedServiceAsInstalled()
    {
        var dto = ServiceInfoProjection.Build(
            Status("node", ServiceState.Stopped), [], new ServiceRunTracker());

        Assert.True(dto.Installed);
        Assert.Null(dto.BinaryApp);
    }

    [Fact]
    public void Build_RunningServiceCountsAsInstalled_EvenWithoutFolderMatch()
    {
        var dto = ServiceInfoProjection.Build(
            Status("redis", ServiceState.Running), [], new ServiceRunTracker());

        Assert.True(dto.Installed);
    }

    [Fact]
    public void CrashKind_IsNull_WhenServiceIsNotCrashed()
    {
        var tracker = new ServiceRunTracker();
        Assert.Null(tracker.CrashKind(Status("apache", ServiceState.Stopped)));
        Assert.Null(tracker.CrashKind(Status("apache", ServiceState.Running)));
    }

    [Fact]
    public void CrashKind_IsStartFailed_WhenServiceNeverWasSeenRunning()
    {
        var tracker = new ServiceRunTracker();
        Assert.Equal(ServiceRunTracker.StartFailed, tracker.CrashKind(Status("apache", ServiceState.Crashed)));
    }

    [Fact]
    public void CrashKind_IsExited_WhenServiceWasRunningBeforeCrashing()
    {
        var tracker = new ServiceRunTracker();
        ServiceInfoProjection.Build(Status("apache", ServiceState.Running), [], tracker);

        var dto = ServiceInfoProjection.Build(Status("apache", ServiceState.Crashed), [], tracker);

        Assert.Equal(ServiceRunTracker.Exited, dto.CrashKind);
    }

    private static Mock<IServiceModule> Module(string id)
    {
        var module = new Mock<IServiceModule>();
        module.SetupGet(m => m.ServiceId).Returns(id);
        module.SetupGet(m => m.DisplayName).Returns(id);
        return module;
    }

    [Fact]
    public async Task StatusAsync_ReportsTheLastKnownState_WhenTheProbeThrows()
    {
        // Apache's probe throws between new Process() and Start().
        var tracker = new ServiceRunTracker();
        var module = Module("apache");
        module.SetupSequence(m => m.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Status("apache", ServiceState.Running))
            .ThrowsAsync(new InvalidOperationException("No process is associated with this object."));

        Assert.Equal(ServiceState.Running, (await tracker.StatusAsync(module.Object, NullLogger.Instance)).State);
        Assert.Equal(ServiceState.Running, (await tracker.StatusAsync(module.Object, NullLogger.Instance)).State);
    }

    [Fact]
    public async Task StatusAsync_ReportsStopped_WhenTheFirstProbeThrows()
    {
        var module = Module("apache");
        module.Setup(m => m.GetStatusAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());

        var status = await new ServiceRunTracker().StatusAsync(module.Object, NullLogger.Instance);

        Assert.Equal(ServiceState.Stopped, status.State);
        Assert.Equal("apache", status.Id);
    }

    [Fact]
    public void MarkStartRequested_ForgetsEarlierRun()
    {
        var tracker = new ServiceRunTracker();
        tracker.Observe(Status("apache", ServiceState.Running));
        tracker.MarkStartRequested("apache");

        Assert.Equal(ServiceRunTracker.StartFailed, tracker.CrashKind(Status("apache", ServiceState.Crashed)));
    }
}
