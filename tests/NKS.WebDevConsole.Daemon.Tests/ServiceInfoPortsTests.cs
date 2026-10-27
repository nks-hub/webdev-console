using System.Text.Json;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The ports and the uptime in seconds that <see cref="ServiceInfoProjection"/>
/// adds to every /api/services row.
/// </summary>
public class ServiceInfoPortsTests
{
    private sealed class WebConfig
    {
        public int HttpPort { get; set; } = 8080;
        public int HttpsPort { get; set; } = 8443;
        // Not a listen port: must not be reported.
        public int DefaultPort { get; set; } = 1;
    }

    private sealed class MailConfig
    {
        public int SmtpPort { get; set; } = 1025;
        public int HttpPort { get; set; } = 8025;
    }

    private sealed class FakeModule(string id, object? config = null) : IServiceModule
    {
        private readonly object? _config = config;
        public object? Config => _config;
        public string ServiceId => id;
        public string DisplayName => id;
        public ServiceType Type => ServiceType.Other;
        public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct) => Task.FromResult(new ValidationResult(true));
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ServiceStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct) => throw new NotSupportedException();
    }

    private static ServiceStatus Status(string id, ServiceState state, TimeSpan? uptime = null) =>
        new(id, id, state, state == ServiceState.Running ? 42 : null, 0, 0, uptime);

    private static ServiceInfoDto Build(IServiceModule module, ServiceStatus status) =>
        ServiceInfoProjection.Build(status, [], new ServiceRunTracker(), module);

    [Fact]
    public void Ports_ComeFromModuleConfig_HttpIsPrimary()
    {
        var dto = Build(new FakeModule("apache", new WebConfig()), Status("apache", ServiceState.Stopped));

        Assert.Equal(8080, dto.Port);
        Assert.Equal(new Dictionary<string, int> { ["http"] = 8080, ["https"] = 8443 }, dto.Ports);
    }

    [Fact]
    public void Ports_MailpitReportsSmtpAndHttp()
    {
        var dto = Build(new FakeModule("mailpit", new MailConfig()), Status("mailpit", ServiceState.Stopped));

        Assert.Equal(8025, dto.Port);
        Assert.Equal(1025, dto.Ports!["smtp"]);
    }

    [Fact]
    public void Ports_EmptyWhenModuleHasNoConfig()
    {
        var dto = Build(new FakeModule("redis"), Status("redis", ServiceState.Stopped));

        Assert.Null(dto.Port);
        Assert.Empty(dto.Ports!);
    }

    [Fact]
    public void BuildAll_TakesPortsFromTheModuleAtTheSameIndex()
    {
        var modules = new IServiceModule[] { new FakeModule("apache", new WebConfig()), new FakeModule("mailpit", new MailConfig()) };

        var rows = ServiceInfoProjection.BuildAll(
            [Status("apache", ServiceState.Stopped), Status("mailpit", ServiceState.Stopped)], [], new ServiceRunTracker(), modules);

        Assert.Equal(8080, rows[0].Port);
        Assert.Equal(1025, rows[1].Ports!["smtp"]);
    }

    [Fact]
    public void UptimeSeconds_IsWholeSeconds()
    {
        var dto = Build(new FakeModule("php"), Status("php", ServiceState.Running, TimeSpan.FromSeconds(3725.9)));

        Assert.Equal(3725, dto.UptimeSeconds);
    }

    [Fact]
    public void Json_UsesTheFieldNamesTheUiReads()
    {
        var dto = Build(new FakeModule("apache", new WebConfig()), Status("apache", ServiceState.Running, TimeSpan.FromMinutes(2)));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = doc.RootElement;

        Assert.Equal(120, root.GetProperty("uptimeSeconds").GetInt64());
        Assert.Equal(8080, root.GetProperty("port").GetInt32());
        Assert.Equal(8443, root.GetProperty("ports").GetProperty("https").GetInt32());
        // The TimeSpan field stays for the CLI and older renderers.
        Assert.Equal("00:02:00", root.GetProperty("uptime").GetString());
    }

    [Fact]
    public void Json_LeavesOutUnknownPortAndUptime()
    {
        var dto = Build(new FakeModule("redis"), Status("redis", ServiceState.Stopped));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.False(doc.RootElement.TryGetProperty("port", out _));
        Assert.False(doc.RootElement.TryGetProperty("uptimeSeconds", out _));
    }
}
