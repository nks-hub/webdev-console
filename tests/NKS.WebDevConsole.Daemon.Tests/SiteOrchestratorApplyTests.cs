using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Covers the disabled-site apply result (vhost removal and reload failures
/// must be reported) and the per-domain apply lock.
/// </summary>
public sealed class SiteOrchestratorApplyTests
{
    private sealed class ControllableHttpdModule : IServiceModule
    {
        private int _active;
        private int _maxActive;

        public ControllableHttpdModule(string id) => ServiceId = id;

        public string ServiceId { get; }
        public string DisplayName => ServiceId;
        public ServiceType Type => ServiceType.WebServer;

        public Exception? RemoveError { get; set; }
        public Exception? ReloadError { get; set; }

        /// <summary>When set, RemoveVhostAsync waits on this before returning.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Completed once <see cref="EnteredTarget"/> removals are in flight at the same time.</summary>
        public TaskCompletionSource ReachedTarget { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EnteredTarget { get; set; } = int.MaxValue;

        public int MaxConcurrentRemovals => Volatile.Read(ref _maxActive);
        public int RemoveCount;

        public async Task RemoveVhostAsync(string domain, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RemoveCount);
            var now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxActive)))
                Interlocked.CompareExchange(ref _maxActive, now, seen);
            if (now >= EnteredTarget) ReachedTarget.TrySetResult();
            try
            {
                if (Gate is not null) await Gate.Task.WaitAsync(ct);
                if (RemoveError is not null) throw RemoveError;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task<ServiceStatus> GetStatusAsync(CancellationToken ct) =>
            Task.FromResult(new ServiceStatus(ServiceId, DisplayName, ServiceState.Running, null, 0, 0, TimeSpan.Zero));

        public Task ReloadAsync(CancellationToken ct) =>
            ReloadError is null ? Task.CompletedTask : Task.FromException(ReloadError);

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct) => Task.FromResult(new ValidationResult(true));
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static SiteOrchestrator Build(params IServiceModule[] modules)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var m in modules) services.AddSingleton(m);
        var sp = services.BuildServiceProvider();
        return new SiteOrchestrator(sp.GetRequiredService<ILogger<SiteOrchestrator>>(), sp);
    }

    private static SiteConfig Disabled(string domain) => new()
    {
        Domain = domain,
        DocumentRoot = "C:/sites/" + domain,
        Enabled = false,
    };

    [Fact]
    public async Task ApplyAsync_Disabled_AllStepsSucceed_ReportsVhostOk()
    {
        var orchestrator = Build(new ControllableHttpdModule("apache"));

        var result = await orchestrator.ApplyAsync(Disabled("ok.loc"));

        Assert.True(result.Success);
        var step = Assert.Single(result.Steps);
        Assert.Equal("vhost", step.Name);
        Assert.Equal(SiteApplyStep.Succeeded, step.Status);
    }

    [Fact]
    public async Task ApplyAsync_Disabled_RemoveVhostFails_ReportsVhostFailure()
    {
        var apache = new ControllableHttpdModule("apache") { RemoveError = new IOException("file locked") };
        var orchestrator = Build(apache);

        var result = await orchestrator.ApplyAsync(Disabled("locked.loc"));

        Assert.False(result.Success);
        var vhost = Assert.Single(result.Steps, s => s.Name == "vhost");
        Assert.Equal(SiteApplyStep.Failed, vhost.Status);
        Assert.Contains("apache", vhost.Detail);
        Assert.Contains("file locked", vhost.Detail);
        Assert.DoesNotContain(result.Steps, s => s.Status == SiteApplyStep.Succeeded);
    }

    [Fact]
    public async Task ApplyAsync_Disabled_ReloadFails_ReportsReloadFailure()
    {
        var nginx = new ControllableHttpdModule("nginx") { ReloadError = new InvalidOperationException("bad config") };
        var orchestrator = Build(nginx);

        var result = await orchestrator.ApplyAsync(Disabled("reload.loc"));

        Assert.False(result.Success);
        var reload = Assert.Single(result.Steps, s => s.Name == "reload");
        Assert.Equal(SiteApplyStep.Failed, reload.Status);
        Assert.Contains("nginx", reload.Detail);
        Assert.Contains("bad config", reload.Detail);
        // The vhost itself was removed, so that step still reports success.
        Assert.Contains(result.Steps, s => s.Name == "vhost" && s.Status == SiteApplyStep.Succeeded);
    }

    [Fact]
    public async Task ApplyAsync_SameDomain_DifferentCase_Serializes()
    {
        var apache = new ControllableHttpdModule("apache")
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var orchestrator = Build(apache);

        var first = orchestrator.ApplyAsync(Disabled("shop.loc"));
        var second = orchestrator.ApplyAsync(Disabled("SHOP.loc"));

        // Give the second apply time to run if the lock were missing.
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref apache.RemoveCount));
        Assert.False(second.IsCompleted);

        apache.Gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, apache.RemoveCount);
        Assert.Equal(1, apache.MaxConcurrentRemovals);
    }

    [Fact]
    public async Task ApplyAsync_DifferentDomains_RunInParallel()
    {
        var apache = new ControllableHttpdModule("apache")
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            EnteredTarget = 2,
        };
        var orchestrator = Build(apache);

        var a = orchestrator.ApplyAsync(Disabled("a.loc"));
        var b = orchestrator.ApplyAsync(Disabled("b.loc"));

        // Both removals must be in flight at once while the gate is still closed.
        await apache.ReachedTarget.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, apache.MaxConcurrentRemovals);

        apache.Gate.SetResult();
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ApplyAsync_CancelledWhileWaitingForLock_DoesNotRun()
    {
        var apache = new ControllableHttpdModule("apache")
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var orchestrator = Build(apache);

        var first = orchestrator.ApplyAsync(Disabled("wait.loc"));
        using var cts = new CancellationTokenSource();
        var second = orchestrator.ApplyAsync(Disabled("wait.loc"), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        apache.Gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, apache.RemoveCount);

        // The lock was released, so a later apply still goes through.
        await orchestrator.ApplyAsync(Disabled("wait.loc")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, apache.RemoveCount);
    }
}
