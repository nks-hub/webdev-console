using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Core.Tests;

public class ServiceStatusTests
{
    [Fact]
    public void ServiceStatus_ConstructsWithAllProperties()
    {
        var uptime = TimeSpan.FromMinutes(42);
        var status = new ServiceStatus("httpd", "Apache HTTP Server", ServiceState.Running, 1234, 15.5, 1024 * 1024, uptime);

        Assert.Equal("httpd", status.Id);
        Assert.Equal("Apache HTTP Server", status.DisplayName);
        Assert.Equal(ServiceState.Running, status.State);
        Assert.Equal(1234, status.Pid);
        Assert.Equal(15.5, status.CpuPercent);
        Assert.Equal(1024 * 1024, status.MemoryBytes);
        Assert.Equal(uptime, status.Uptime);
    }

    [Fact]
    public void ServiceStatus_NullPidAndUptime_IsAllowed()
    {
        var status = new ServiceStatus("svc", "Service", ServiceState.Stopped, null, 0, 0, null);

        Assert.Null(status.Pid);
        Assert.Null(status.Uptime);
    }

    [Fact]
    public void ServiceStatus_RecordEquality_WorksCorrectly()
    {
        var uptime = TimeSpan.FromSeconds(100);
        var a = new ServiceStatus("mysql", "MySQL", ServiceState.Running, 999, 5.0, 2048, uptime);
        var b = new ServiceStatus("mysql", "MySQL", ServiceState.Running, 999, 5.0, 2048, uptime);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ServiceStatus_RecordInequality_DifferentState()
    {
        var a = new ServiceStatus("svc", "Svc", ServiceState.Running, 1, 0, 0, null);
        var b = new ServiceStatus("svc", "Svc", ServiceState.Stopped, 1, 0, 0, null);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ServiceStatus_RecordInequality_DifferentId()
    {
        var a = new ServiceStatus("apache", "Apache", ServiceState.Running, 1, 0, 0, null);
        var b = new ServiceStatus("nginx", "Apache", ServiceState.Running, 1, 0, 0, null);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ValidationResult_IsValid_DefaultErrorNull()
    {
        var result = new ValidationResult(true);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ValidationResult_Invalid_WithMessage()
    {
        var result = new ValidationResult(false, "Port out of range");

        Assert.False(result.IsValid);
        Assert.Equal("Port out of range", result.ErrorMessage);
    }

    [Fact]
    public void ValidationResult_RecordEquality()
    {
        var a = new ValidationResult(false, "err");
        var b = new ValidationResult(false, "err");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ValidationResult_Success_HasNullMessage()
    {
        var r = new ValidationResult(true);
        Assert.True(r.IsValid);
        Assert.Null(r.ErrorMessage);
    }
}
