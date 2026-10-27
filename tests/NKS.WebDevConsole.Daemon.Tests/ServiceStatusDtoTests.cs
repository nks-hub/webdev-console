using System.Text.Json;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

public class ServiceStatusDtoTests
{
    private sealed class WebConfig
    {
        public int HttpPort { get; set; } = 8080;
        public int HttpsPort { get; set; } = 8443;
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

    private static InstalledBinary Binary(string app, string version, bool hasExecutable = true) =>
        new(app, version, string.Join('.', version.Split('.').Take(2)), $"/wdc/binaries/{app}/{version}",
            hasExecutable ? $"/wdc/binaries/{app}/{version}/bin/{app}" : null);

    [Fact]
    public void StoppedService_WithoutBinary_IsNotInstalled()
    {
        var dto = ServiceStatusDto.From(new FakeModule("apache"), Status("apache", ServiceState.Stopped), [Binary("php", "8.4.1")]);

        Assert.False(dto.Installed);
        Assert.Null(dto.Version);
    }

    [Fact]
    public void BinaryWithoutExecutable_DoesNotCountAsInstalled()
    {
        var dto = ServiceStatusDto.From(new FakeModule("mysql"), Status("mysql", ServiceState.Stopped), [Binary("mysql", "9.1.0", hasExecutable: false)]);

        Assert.False(dto.Installed);
    }

    [Fact]
    public void InstalledService_ReportsNewestUsableVersion()
    {
        // ListInstalled() is newest-first; the first usable one is what the module runs.
        var installed = new[] { Binary("mysql", "9.2.0", hasExecutable: false), Binary("mysql", "9.1.0"), Binary("mysql", "8.4.3") };

        var dto = ServiceStatusDto.From(new FakeModule("mysql"), Status("mysql", ServiceState.Stopped), installed);

        Assert.True(dto.Installed);
        Assert.Equal("9.1.0", dto.Version);
    }

    [Fact]
    public void RunningService_IsInstalled_EvenWithoutManagedBinary()
    {
        var dto = ServiceStatusDto.From(new FakeModule("redis"), Status("redis", ServiceState.Running), []);

        Assert.True(dto.Installed);
    }

    [Fact]
    public void ServiceWithoutManagedBinary_IsInstalled()
    {
        var dto = ServiceStatusDto.From(new FakeModule("node"), Status("node", ServiceState.Stopped), []);

        Assert.True(dto.Installed);
        Assert.Null(dto.Version);
    }

    [Fact]
    public void CloudflareService_MapsToCloudflaredBinary()
    {
        Assert.Equal("cloudflared", ServiceStatusDto.BinaryAppFor("cloudflare"));

        var dto = ServiceStatusDto.From(new FakeModule("cloudflare"), Status("cloudflare", ServiceState.Stopped), [Binary("cloudflared", "2025.1.0")]);

        Assert.True(dto.Installed);
        Assert.Equal("2025.1.0", dto.Version);
    }

    [Fact]
    public void Ports_ComeFromModuleConfig_HttpIsPrimary()
    {
        var dto = ServiceStatusDto.From(new FakeModule("apache", new WebConfig()), Status("apache", ServiceState.Stopped), [Binary("apache", "2.4.62")]);

        Assert.Equal(8080, dto.Port);
        Assert.Equal(new Dictionary<string, int> { ["http"] = 8080, ["https"] = 8443 }, dto.Ports);
    }

    [Fact]
    public void Ports_MailpitReportsSmtpAndHttp()
    {
        var dto = ServiceStatusDto.From(new FakeModule("mailpit", new MailConfig()), Status("mailpit", ServiceState.Stopped), [Binary("mailpit", "1.32.0")]);

        Assert.Equal(8025, dto.Port);
        Assert.Equal(1025, dto.Ports["smtp"]);
    }

    [Fact]
    public void Ports_EmptyWhenModuleHasNoConfig()
    {
        var dto = ServiceStatusDto.From(new FakeModule("redis"), Status("redis", ServiceState.Stopped), [Binary("redis", "7.4.2")]);

        Assert.Null(dto.Port);
        Assert.Empty(dto.Ports);
    }

    [Fact]
    public void UptimeSeconds_IsWholeSeconds()
    {
        var dto = ServiceStatusDto.From(new FakeModule("php"), Status("php", ServiceState.Running, TimeSpan.FromSeconds(3725.9)), []);

        Assert.Equal(3725, dto.UptimeSeconds);
    }

    [Fact]
    public void Json_UsesTheFieldNamesTheUiReads()
    {
        var dto = ServiceStatusDto.From(new FakeModule("apache", new WebConfig()), Status("apache", ServiceState.Running, TimeSpan.FromMinutes(2)), [Binary("apache", "2.4.62")]);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = doc.RootElement;

        Assert.Equal("apache", root.GetProperty("id").GetString());
        Assert.Equal((int)ServiceState.Running, root.GetProperty("state").GetInt32());
        Assert.True(root.GetProperty("installed").GetBoolean());
        Assert.Equal("2.4.62", root.GetProperty("version").GetString());
        Assert.Equal(120, root.GetProperty("uptimeSeconds").GetInt64());
        Assert.Equal(8080, root.GetProperty("port").GetInt32());
        Assert.Equal(8443, root.GetProperty("ports").GetProperty("https").GetInt32());
        // The old TimeSpan field stays for the CLI and older renderers.
        Assert.Equal("00:02:00", root.GetProperty("uptime").GetString());
    }

    [Fact]
    public void Json_LeavesOutUnknownPortVersionAndUptime()
    {
        var dto = ServiceStatusDto.From(new FakeModule("redis"), Status("redis", ServiceState.Stopped), []);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("port", out _));
        Assert.False(root.TryGetProperty("version", out _));
        Assert.False(root.TryGetProperty("uptimeSeconds", out _));
        Assert.False(root.GetProperty("installed").GetBoolean());
    }
}
