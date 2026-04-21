using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Verifies that SiteOrchestrator.ApplyAsync honours the Enabled flag:
/// disabled sites must have their vhosts removed and must not re-generate them.
/// </summary>
public sealed class SiteOrchestratorEnabledTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A fake IServiceModule that also exposes RemoveVhostAsync and GenerateVhostAsync
    /// as public methods so the orchestrator can call them via reflection.
    /// Tracks invocations so tests can assert behaviour.
    /// </summary>
    private sealed class FakeHttpdModule : IServiceModule
    {
        public string ServiceId { get; }
        public string DisplayName => ServiceId;
        public ServiceType Type => ServiceType.WebServer;

        public List<string> GenerateCalls { get; } = [];
        public List<string> RemoveCalls { get; } = [];
        public bool ReloadCalled { get; private set; }

        public FakeHttpdModule(string id) => ServiceId = id;

        public Task GenerateVhostAsync(SiteConfig site, CancellationToken ct = default)
        {
            GenerateCalls.Add(site.Domain);
            return Task.CompletedTask;
        }

        public Task RemoveVhostAsync(string domain, CancellationToken ct = default)
        {
            RemoveCalls.Add(domain);
            return Task.CompletedTask;
        }

        public Task<ServiceStatus> GetStatusAsync(CancellationToken ct) =>
            Task.FromResult(new ServiceStatus(ServiceId, DisplayName, ServiceState.Running, null, 0, 0, TimeSpan.Zero));

        public Task ReloadAsync(CancellationToken ct)
        {
            ReloadCalled = true;
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct) => Task.FromResult(new ValidationResult(true));
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static SiteOrchestrator BuildOrchestrator(
        IEnumerable<IServiceModule> modules,
        out IServiceProvider sp)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var m in modules)
            services.AddSingleton(m);
        sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILogger<SiteOrchestrator>>();
        return new SiteOrchestrator(logger, sp);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WhenDisabled_CallsRemoveVhostAndNotGenerate()
    {
        var apacheFake = new FakeHttpdModule("apache");
        var orchestrator = BuildOrchestrator([apacheFake], out _);

        var site = new SiteConfig
        {
            Domain = "disabled.loc",
            DocumentRoot = "C:/sites/disabled",
            Enabled = false,
        };

        await orchestrator.ApplyAsync(site);

        Assert.Contains("disabled.loc", apacheFake.RemoveCalls);
        Assert.Empty(apacheFake.GenerateCalls);
    }

    [Fact]
    public async Task ApplyAsync_WhenDisabled_ReloadsWebServer()
    {
        var apacheFake = new FakeHttpdModule("apache");
        var orchestrator = BuildOrchestrator([apacheFake], out _);

        var site = new SiteConfig
        {
            Domain = "off.loc",
            DocumentRoot = "C:/sites/off",
            Enabled = false,
        };

        await orchestrator.ApplyAsync(site);

        Assert.True(apacheFake.ReloadCalled);
    }

    [Fact]
    public async Task ApplyAsync_WhenEnabled_DoesNotCallRemoveVhost()
    {
        var apacheFake = new FakeHttpdModule("apache");
        // Enabled = true path hits GenerateVhostAsync via reflection.
        // We cannot fully exercise that without a real Apache binary,
        // but we CAN confirm RemoveVhostAsync is never called for an
        // enabled site by checking the tracking list stays empty even
        // if the generate call itself throws (best-effort, caught by orchestrator).
        var orchestrator = BuildOrchestrator([apacheFake], out _);

        var site = new SiteConfig
        {
            Domain = "active.loc",
            DocumentRoot = "C:/sites/active",
            Enabled = true,
            PhpVersion = "none",
        };

        // ApplyAsync will fail internally (no real Apache), but that's caught.
        await orchestrator.ApplyAsync(site);

        Assert.Empty(apacheFake.RemoveCalls);
    }
}
