using System.Text.Json;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// A start that throws (from the UI, the API or auto-start) leaves the
/// service stopped; <see cref="ServiceRunTracker"/> keeps the reason so
/// /api/services can show it until the service runs or is started again.
/// </summary>
public class ServiceStartErrorTests
{
    private static ServiceStatus Status(string id, ServiceState state) => new(id, id, state, null, 0, 0, null);

    [Fact]
    public void FailedStart_IsReportedWhileTheServiceIsStopped()
    {
        var tracker = new ServiceRunTracker();
        tracker.MarkStartRequested("mailpit");
        tracker.RecordStartFailure("mailpit", "mailpit.exe not found");

        var dto = ServiceInfoProjection.Build(Status("mailpit", ServiceState.Stopped), [], tracker);

        Assert.Equal("mailpit.exe not found", dto.StartError);
    }

    [Fact]
    public void StartError_IsClearedByANewStartRequest()
    {
        var tracker = new ServiceRunTracker();
        tracker.RecordStartFailure("mysql", "boom");
        tracker.MarkStartRequested("mysql");

        Assert.Null(ServiceInfoProjection.Build(Status("mysql", ServiceState.Stopped), [], tracker).StartError);
    }

    [Fact]
    public void StartError_IsClearedOnceTheServiceRuns()
    {
        var tracker = new ServiceRunTracker();
        tracker.RecordStartFailure("redis", "port in use");

        Assert.Null(ServiceInfoProjection.Build(Status("redis", ServiceState.Running), [], tracker).StartError);
        Assert.Null(ServiceInfoProjection.Build(Status("redis", ServiceState.Stopped), [], tracker).StartError);
    }

    [Fact]
    public void StartError_IsLeftOutOfTheJsonWhenThereIsNone()
    {
        var dto = ServiceInfoProjection.Build(Status("apache", ServiceState.Stopped), [], new ServiceRunTracker());

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.False(doc.RootElement.TryGetProperty("startError", out _));
    }
}
