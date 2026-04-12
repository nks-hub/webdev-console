using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Core.Tests;

public class ServiceStateTests
{
    [Theory]
    [InlineData(ServiceState.Stopped, 0)]
    [InlineData(ServiceState.Starting, 1)]
    [InlineData(ServiceState.Running, 2)]
    [InlineData(ServiceState.Stopping, 3)]
    [InlineData(ServiceState.Crashed, 4)]
    [InlineData(ServiceState.Disabled, 5)]
    public void ServiceState_HasExpectedValues(ServiceState state, int expected)
    {
        Assert.Equal(expected, (int)state);
    }

    [Fact]
    public void ServiceState_HasExactlySixMembers()
    {
        var values = Enum.GetValues<ServiceState>();
        Assert.Equal(6, values.Length);
    }

    [Theory]
    [InlineData(ServiceType.WebServer, 0)]
    [InlineData(ServiceType.Database, 1)]
    [InlineData(ServiceType.Cache, 2)]
    [InlineData(ServiceType.MailServer, 3)]
    [InlineData(ServiceType.Other, 4)]
    public void ServiceType_HasExpectedValues(ServiceType type, int expected)
    {
        Assert.Equal(expected, (int)type);
    }

    [Fact]
    public void ServiceType_HasExactlyFiveMembers()
    {
        var values = Enum.GetValues<ServiceType>();
        Assert.Equal(5, values.Length);
    }

    [Theory]
    [InlineData("Stopped", ServiceState.Stopped)]
    [InlineData("Running", ServiceState.Running)]
    [InlineData("Crashed", ServiceState.Crashed)]
    [InlineData("Disabled", ServiceState.Disabled)]
    public void ServiceState_ParsesFromString(string name, ServiceState expected)
    {
        Assert.True(Enum.TryParse<ServiceState>(name, out var result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ServiceState_InvalidString_FailsParse()
    {
        Assert.False(Enum.TryParse<ServiceState>("NotAState", out _));
    }

    [Fact]
    public void ServiceState_AllValuesHaveDistinctIntRepresentation()
    {
        var values = Enum.GetValues<ServiceState>().Select(v => (int)v).ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
    }
}
